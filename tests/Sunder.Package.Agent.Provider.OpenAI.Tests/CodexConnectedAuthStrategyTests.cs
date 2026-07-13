using System.Net;
using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Provider.OpenAI.Auth;
using Sunder.Package.Agent.Provider.TestSupport;
using Xunit;

namespace Sunder.Package.Agent.Provider.OpenAI.Tests;

public sealed class CodexConnectedAuthStrategyTests
{
    private static readonly Uri RegisteredCallbackUri = new("http://localhost:1455/auth/callback");

    [Fact]
    public void CreateAuthorizationUrl_EmitsCompleteCodexAuthorizeRequest()
    {
        var context = CreateContext();
        using var strategy = CreateStrategy(
            context,
            new StubHttpMessageHandler((_, _) => Task.FromResult(CreateTokenResponse("unused"))));

        var authorizationUri = new Uri(strategy.CreateAuthorizationUrl("auth-session", RegisteredCallbackUri));
        var query = ParseFormValues(authorizationUri.Query);

        Assert.Equal("https", authorizationUri.Scheme);
        Assert.Equal("auth.openai.com", authorizationUri.Host);
        Assert.Equal("/oauth/authorize", authorizationUri.AbsolutePath);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(CodexOAuthClient.ClientId, query["client_id"]);
        Assert.Equal(RegisteredCallbackUri.AbsoluteUri, query["redirect_uri"]);
        Assert.Equal("openid profile email offline_access api.connectors.read api.connectors.invoke", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("auth-session", query["state"]);
        Assert.Equal("true", query["id_token_add_organizations"]);
        Assert.Equal("true", query["codex_cli_simplified_flow"]);
        Assert.Equal("sunder", query["originator"]);
        Assert.False(string.IsNullOrWhiteSpace(query["code_challenge"]));
        Assert.Equal(10, query.Count);
    }

    [Theory]
    [InlineData("http://localhost:1455/callbacks/session")]
    [InlineData("http://127.0.0.1:1455/auth/callback")]
    [InlineData("http://localhost:1456/auth/callback")]
    public void CreateAuthorizationUrl_RejectsUnregisteredCodexCallback(string callbackUri)
    {
        var context = CreateContext();
        using var strategy = CreateStrategy(
            context,
            new StubHttpMessageHandler((_, _) => Task.FromResult(CreateTokenResponse("unused"))));

        var error = Assert.Throws<InvalidOperationException>(() =>
            strategy.CreateAuthorizationUrl("auth-session", new Uri(callbackUri)));

        Assert.Contains("http://localhost:1455/auth/callback", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAuthorizationAsync_ReusesAuthorizeRedirectForTokenExchange()
    {
        string? tokenRequestBody = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            tokenRequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return CreateTokenResponse("access-token", "refresh-token");
        });
        var context = CreateContext();
        using var strategy = CreateStrategy(context, handler);
        strategy.CreateAuthorizationUrl("auth-session", RegisteredCallbackUri);

        await strategy.CompleteAuthorizationAsync(
            "auth-session",
            new Dictionary<string, string?> { ["code"] = "authorization-code" });

        Assert.NotNull(tokenRequestBody);
        var form = ParseFormValues(tokenRequestBody);
        Assert.Equal(RegisteredCallbackUri.AbsoluteUri, form["redirect_uri"]);
        Assert.Equal(CodexOAuthClient.ClientId, form["client_id"]);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("authorization-code", form["code"]);
        Assert.False(string.IsNullOrWhiteSpace(form["code_verifier"]));
    }

    [Fact]
    public async Task TryRefreshSessionAsync_ResponseWithoutRefreshToken_PreservesExistingToken()
    {
        var existing = CreateExpiredSession();
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(CreateTokenResponse("new-access")));
        var context = CreateContext(existing);
        using var strategy = CreateStrategy(context, handler);

        var refreshed = await strategy.TryRefreshSessionAsync(existing);

        Assert.NotNull(refreshed);
        Assert.Equal("new-access", refreshed.AccessToken);
        Assert.Equal(existing.RefreshToken, refreshed.RefreshToken);
        Assert.Equal(existing.RefreshToken, (await strategy.GetCachedSessionAsync())!.RefreshToken);
    }

    [Fact]
    public async Task TryRefreshSessionAsync_ConcurrentCallers_SendSingleRefreshRequest()
    {
        var existing = CreateExpiredSession();
        var requestCount = 0;
        var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref requestCount);
            await Task.Delay(50, cancellationToken);
            return CreateTokenResponse("new-access");
        });
        var context = CreateContext(existing);
        using var strategy = CreateStrategy(context, handler);

        var refreshes = await Task.WhenAll(
            strategy.TryRefreshSessionAsync(existing),
            strategy.TryRefreshSessionAsync(existing),
            strategy.TryRefreshSessionAsync(existing));

        Assert.Equal(1, requestCount);
        Assert.All(refreshes, session => Assert.Equal("new-access", session?.AccessToken));
    }

    [Fact]
    public async Task CompleteAuthorizationAsync_ExpiredPendingFlow_IsRejectedBeforeTokenExchange()
    {
        var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var requestCount = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(CreateTokenResponse("unexpected"));
        });
        var context = CreateContext();
        using var strategy = CreateStrategy(context, handler, timeProvider, TimeSpan.FromMinutes(5));
        strategy.CreateAuthorizationUrl("auth-session", RegisteredCallbackUri);
        timeProvider.Advance(TimeSpan.FromMinutes(6));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            strategy.CompleteAuthorizationAsync(
                "auth-session",
                new Dictionary<string, string?> { ["code"] = "authorization-code" }));

        Assert.Contains("expired", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, requestCount);
    }

    [Fact]
    public async Task TryRefreshSessionAsync_Cancellation_PropagatesToHttpRequest()
    {
        var existing = CreateExpiredSession();
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            requestStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });
        var context = CreateContext(existing);
        using var strategy = CreateStrategy(context, handler);
        using var cancellation = new CancellationTokenSource();
        var refresh = strategy.TryRefreshSessionAsync(existing, cancellation.Token);
        await requestStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
    }

    [Fact]
    public async Task TryRefreshSessionAsync_TerminalFailure_ClearsCachedSession()
    {
        var existing = CreateExpiredSession();
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = JsonContent("{\"error\":\"invalid_grant\"}"),
        }));
        var context = CreateContext(existing);
        using var strategy = CreateStrategy(context, handler);

        var refreshed = await strategy.TryRefreshSessionAsync(existing);

        Assert.Null(refreshed);
        Assert.Null(await strategy.GetCachedSessionAsync());
    }

    [Fact]
    public async Task TryRefreshSessionAsync_TransientFailure_RetainsCachedSession()
    {
        var existing = CreateExpiredSession();
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = JsonContent("{\"error\":\"temporarily_unavailable\"}"),
        }));
        var context = CreateContext(existing);
        using var strategy = CreateStrategy(context, handler);

        var refreshed = await strategy.TryRefreshSessionAsync(existing);

        Assert.Null(refreshed);
        Assert.Equal(existing, await strategy.GetCachedSessionAsync());
    }

    [Theory]
    [InlineData("unknown_error")]
    [InlineData("temporarily_unavailable")]
    [InlineData("server_error")]
    public void OAuthErrorsOtherThanInvalidGrant_AreTransient(string error)
    {
        Assert.Equal(
            CodexAuthFailureKind.Transient,
            CodexOAuthClient.ClassifyFailure(HttpStatusCode.BadRequest, $"{{\"error\":\"{error}\"}}"));
    }

    [Fact]
    public async Task DisconnectDuringRefresh_PreventsSessionRestoration()
    {
        var existing = CreateExpiredSession();
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (_, _) =>
        {
            requestStarted.TrySetResult();
            await releaseResponse.Task;
            return CreateTokenResponse("new-access");
        });
        var context = CreateContext(existing);
        using var strategy = CreateStrategy(context, handler);
        var refresh = strategy.TryRefreshSessionAsync(existing);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disconnect = strategy.ClearSessionAsync();
        releaseResponse.TrySetResult();
        await disconnect;

        Assert.Null(await refresh);
        Assert.Null(await strategy.GetCachedSessionAsync());
    }

    [Fact]
    public async Task RefreshAfterDisconnect_DoesNotUseExpectedSessionsStaleRefreshToken()
    {
        var existing = CreateExpiredSession();
        var requestCount = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(CreateTokenResponse("unexpected"));
        });
        var context = CreateContext(existing);
        using var strategy = CreateStrategy(context, handler);

        await strategy.ClearSessionAsync();
        var refreshed = await strategy.TryRefreshSessionAsync(existing);

        Assert.Null(refreshed);
        Assert.Equal(0, requestCount);
    }

    [Fact]
    public async Task Disconnect_CancelsPendingRuntimeBrowserAuthorization()
    {
        var requestCount = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(CreateTokenResponse("unexpected"));
        });
        var context = CreateContext();
        using var strategy = CreateStrategy(context, handler);
        strategy.CreateAuthorizationUrl("auth-session", RegisteredCallbackUri);

        await strategy.ClearSessionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => strategy.CompleteAuthorizationAsync(
            "auth-session",
            new Dictionary<string, string?> { ["code"] = "authorization-code" }));

        Assert.Equal(0, requestCount);
        Assert.Null(await strategy.GetCachedSessionAsync());
    }

    private static CodexConnectedAuthStrategy CreateStrategy(
        ProviderTestPackageContext context,
        HttpMessageHandler handler,
        TimeProvider? timeProvider = null,
        TimeSpan? pendingLifetime = null)
        => new(
            context,
            () => new HttpClient(handler, disposeHandler: false),
            timeProvider ?? TimeProvider.System,
            pendingLifetime ?? TimeSpan.FromMinutes(10));

    private static ProviderTestPackageContext CreateContext(OpenAiCodexSession? session = null)
        => new(
            "sunder.package.agent.provider.openai",
            secretValues: session is null
                ? null
                : new Dictionary<string, string>
                {
                    ["auth.codex.session"] = JsonSerializer.Serialize(session),
                });

    private static OpenAiCodexSession CreateExpiredSession()
        => new("old-access", "old-refresh", DateTimeOffset.UtcNow.AddMinutes(-1), "account-id");

    private static HttpResponseMessage CreateTokenResponse(string accessToken, string? refreshToken = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["access_token"] = accessToken,
            ["expires_in"] = 3600,
            ["id_token"] = CreateJwt(new Dictionary<string, object?>
            {
                ["chatgpt_account_id"] = "account-id",
            }),
        };
        if (refreshToken is not null)
        {
            payload["refresh_token"] = refreshToken;
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent(JsonSerializer.Serialize(payload)),
        };
    }

    private static StringContent JsonContent(string value) => new(value, Encoding.UTF8, "application/json");

    private static string CreateJwt(IReadOnlyDictionary<string, object?> claims)
        => Base64UrlEncode("{}") + "." + Base64UrlEncode(JsonSerializer.Serialize(claims)) + ".signature";

    private static string Base64UrlEncode(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static IReadOnlyDictionary<string, string> ParseFormValues(string value)
        => value.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => part.Split('=', 2))
            .ToDictionary(
                static part => DecodeFormValue(part[0]),
                static part => DecodeFormValue(part.Length == 2 ? part[1] : string.Empty),
                StringComparer.Ordinal);

    private static string DecodeFormValue(string value)
        => Uri.UnescapeDataString(value.Replace('+', ' '));

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => sendAsync(request, cancellationToken);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
