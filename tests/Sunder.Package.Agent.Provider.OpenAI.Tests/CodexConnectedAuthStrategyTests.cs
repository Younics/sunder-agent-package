using System.Net;
using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Provider.OpenAI.Auth;
using Sunder.Package.Agent.Provider.TestSupport;
using Sunder.Sdk.Authentication;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Logging;
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
            new Dictionary<string, string?>
            {
                ["code"] = "authorization-code",
                ["state"] = "auth-session",
            });

        Assert.NotNull(tokenRequestBody);
        var form = ParseFormValues(tokenRequestBody);
        Assert.Equal(RegisteredCallbackUri.AbsoluteUri, form["redirect_uri"]);
        Assert.Equal(CodexOAuthClient.ClientId, form["client_id"]);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("authorization-code", form["code"]);
        Assert.False(string.IsNullOrWhiteSpace(form["code_verifier"]));
    }

    [Fact]
    public void TokenParser_OfficialResponseWithoutExpiresIn_UsesAccessTokenJwtExpiration()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var accessExpiration = now.AddMinutes(20);
        var accessToken = CreateJwt(new Dictionary<string, object?>
        {
            ["exp"] = accessExpiration.ToUnixTimeSeconds(),
        });
        var idToken = CreateJwt(new Dictionary<string, object?>
        {
            ["chatgpt_account_id"] = "account-id",
            ["exp"] = now.AddHours(1).ToUnixTimeSeconds(),
        });
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id_token"] = idToken,
            ["access_token"] = accessToken,
            ["refresh_token"] = "refresh-token",
        });

        var session = new CodexOAuthTokenParser(new MutableTimeProvider(now)).TryParse(payload);

        Assert.NotNull(session);
        Assert.Equal(accessToken, session.AccessToken);
        Assert.Equal("refresh-token", session.RefreshToken);
        Assert.Equal(accessExpiration, session.ExpiresAtUtc);
        Assert.Equal("account-id", session.ChatGptAccountId);
    }

    [Fact]
    public void TokenParser_OptionalPositiveExpiresIn_TakesPrecedenceOverJwtExpiration()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var accessToken = CreateJwt(new Dictionary<string, object?>
        {
            ["exp"] = now.AddHours(1).ToUnixTimeSeconds(),
        });
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id_token"] = CreateJwt(new Dictionary<string, object?>
            {
                ["chatgpt_account_id"] = "account-id",
            }),
            ["access_token"] = accessToken,
            ["refresh_token"] = "refresh-token",
            ["expires_in"] = 90,
        });

        var session = new CodexOAuthTokenParser(new MutableTimeProvider(now)).TryParse(payload);

        Assert.NotNull(session);
        Assert.Equal(now.AddSeconds(90), session.ExpiresAtUtc);
    }

    [Theory]
    [InlineData("{\"access_token\":\"access-token\",\"refresh_token\":\"refresh-token\",\"expires_in\":3600}")]
    [InlineData("{\"id_token\":\"e30.eyJjaGF0Z3B0X2FjY291bnRfaWQiOiJhY2NvdW50LWlkIn0.signature\",\"refresh_token\":\"refresh-token\",\"expires_in\":3600}")]
    public void TokenParser_InitialAuthorizationRejectsSparseResponse(string payload)
    {
        var session = new CodexOAuthTokenParser(TimeProvider.System).TryParse(payload);

        Assert.Null(session);
    }

    [Theory]
    [InlineData("wrong-state")]
    [InlineData("AUTH-SESSION")]
    [InlineData("auth-session.onboarding_entrypoint=unknown")]
    [InlineData("auth-session.onboarding_entrypoint=Life_Sciences")]
    [InlineData("auth-session.onboarding_entrypoint=life_sciences.onboarding_entrypoint=life_sciences")]
    [InlineData("auth-session.extra=value.onboarding_entrypoint=life_sciences")]
    public async Task CompleteAuthorizationAsync_WrongStateIsRejectedWithoutConsumingPendingFlow(string callbackState)
    {
        var requestCount = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(CreateTokenResponse("access-token", "refresh-token"));
        });
        var context = CreateContext();
        using var strategy = CreateStrategy(context, handler);
        strategy.CreateAuthorizationUrl("auth-session", RegisteredCallbackUri);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            strategy.CompleteAuthorizationAsync(
                "auth-session",
                new Dictionary<string, string?>
                {
                    ["code"] = "authorization-code",
                    ["state"] = callbackState,
                }));

        Assert.Contains("state", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, requestCount);

        var session = await strategy.CompleteAuthorizationAsync(
            "auth-session",
            new Dictionary<string, string?>
            {
                ["code"] = "authorization-code",
                ["state"] = "auth-session",
            });
        Assert.Equal("access-token", session.AccessToken);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task CompleteAuthorizationAsync_AcceptsAllowlistedLifeSciencesStateSuffix()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(CreateTokenResponse("access-token", "refresh-token")));
        var context = CreateContext();
        using var strategy = CreateStrategy(context, handler);
        strategy.CreateAuthorizationUrl("auth-session", RegisteredCallbackUri);

        var session = await strategy.CompleteAuthorizationAsync(
            "auth-session",
            new Dictionary<string, string?>
            {
                ["code"] = "authorization-code",
                ["state"] = "auth-session.onboarding_entrypoint=life_sciences",
            });

        Assert.Equal("access-token", session.AccessToken);
    }

    [Fact]
    public async Task CompleteAuthorizationAsync_AuthSessionIdsAreOrdinal()
    {
        var requestCount = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(CreateTokenResponse("access-token", "refresh-token"));
        });
        var context = CreateContext();
        using var strategy = CreateStrategy(context, handler);
        strategy.CreateAuthorizationUrl("Auth-Session", RegisteredCallbackUri);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            strategy.CompleteAuthorizationAsync(
                "auth-session",
                new Dictionary<string, string?>
                {
                    ["code"] = "authorization-code",
                    ["state"] = "auth-session",
                }));

        Assert.Equal(0, requestCount);
        var session = await strategy.CompleteAuthorizationAsync(
            "Auth-Session",
            new Dictionary<string, string?>
            {
                ["code"] = "authorization-code",
                ["state"] = "Auth-Session",
            });
        Assert.Equal("access-token", session.AccessToken);
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
    public async Task TryRefreshSessionAsync_ResponseWithoutAccountClaim_PreservesExistingAccount()
    {
        var existing = CreateExpiredSession();
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("{\"access_token\":\"new-access\",\"expires_in\":3600}"),
        }));
        var context = CreateContext(existing);
        using var strategy = CreateStrategy(context, handler);

        var refreshed = await strategy.TryRefreshSessionAsync(existing);

        Assert.NotNull(refreshed);
        Assert.Equal("new-access", refreshed.AccessToken);
        Assert.Equal(existing.RefreshToken, refreshed.RefreshToken);
        Assert.Equal(existing.ChatGptAccountId, refreshed.ChatGptAccountId);
        Assert.True(refreshed.ExpiresAtUtc > existing.ExpiresAtUtc);
    }

    [Fact]
    public async Task TryRefreshSessionAsync_EmptyResponsePreservesCachedSession()
    {
        var existing = CreateExpiredSession();
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("{}"),
        }));
        var context = CreateContext(existing);
        using var strategy = CreateStrategy(context, handler);

        var refreshed = await strategy.TryRefreshSessionAsync(existing);

        Assert.Equal(existing, refreshed);
        Assert.Equal(existing, await strategy.GetCachedSessionAsync());
    }

    [Fact]
    public async Task TryRefreshSessionAsync_IdTokenOnlyUpdatesClaimsAndPreservesTokens()
    {
        var existing = CreateExpiredSession();
        var replacementExpiration = DateTimeOffset.FromUnixTimeSeconds(
            DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds());
        var idToken = CreateJwt(new Dictionary<string, object?>
        {
            ["chatgpt_account_id"] = "new-account-id",
            ["exp"] = replacementExpiration.ToUnixTimeSeconds(),
        });
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id_token"] = idToken,
            })),
        }));
        var context = CreateContext(existing);
        using var strategy = CreateStrategy(context, handler);

        var refreshed = await strategy.TryRefreshSessionAsync(existing);

        Assert.NotNull(refreshed);
        Assert.Equal(existing.AccessToken, refreshed.AccessToken);
        Assert.Equal(existing.RefreshToken, refreshed.RefreshToken);
        Assert.Equal("new-account-id", refreshed.ChatGptAccountId);
        Assert.Equal(replacementExpiration, refreshed.ExpiresAtUtc);
    }

    [Fact]
    public async Task TryRefreshSessionAsync_RefreshTokenOnlyReplacesRefreshToken()
    {
        var existing = CreateExpiredSession();
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("{\"refresh_token\":\"new-refresh\"}"),
        }));
        var context = CreateContext(existing);
        using var strategy = CreateStrategy(context, handler);

        var refreshed = await strategy.TryRefreshSessionAsync(existing);

        Assert.NotNull(refreshed);
        Assert.Equal(existing.AccessToken, refreshed.AccessToken);
        Assert.Equal("new-refresh", refreshed.RefreshToken);
        Assert.Equal(existing.ChatGptAccountId, refreshed.ChatGptAccountId);
        Assert.Equal(existing.ExpiresAtUtc, refreshed.ExpiresAtUtc);
    }

    [Fact]
    public async Task TryRefreshSessionAsync_SendsOfficialJsonWireFormat()
    {
        string? contentType = null;
        string? requestBody = null;
        var existing = CreateExpiredSession();
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://auth.openai.com/oauth/token", request.RequestUri?.AbsoluteUri);
            contentType = request.Content?.Headers.ContentType?.ToString();
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return CreateTokenResponse("new-access");
        });
        var context = CreateContext(existing);
        using var strategy = CreateStrategy(context, handler);

        var refreshed = await strategy.TryRefreshSessionAsync(existing);

        Assert.NotNull(refreshed);
        Assert.Equal("application/json", contentType);
        Assert.NotNull(requestBody);
        using var document = JsonDocument.Parse(requestBody);
        var properties = document.RootElement.EnumerateObject().ToArray();
        Assert.Equal(3, properties.Length);
        Assert.Equal(CodexOAuthClient.ClientId, document.RootElement.GetProperty("client_id").GetString());
        Assert.Equal("refresh_token", document.RootElement.GetProperty("grant_type").GetString());
        Assert.Equal(existing.RefreshToken, document.RootElement.GetProperty("refresh_token").GetString());
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
                new Dictionary<string, string?>
                {
                    ["code"] = "authorization-code",
                    ["state"] = "auth-session",
                }));

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

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}")]
    [InlineData(HttpStatusCode.BadRequest, "{\"code\":\"refresh_token_expired\"}")]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"refresh_token_reused\"}}")]
    [InlineData(HttpStatusCode.BadRequest, "{\"code\":\"refresh_token_invalidated\"}")]
    [InlineData(HttpStatusCode.Unauthorized, "{\"code\":\"unknown_error\"}")]
    public async Task TryRefreshSessionAsync_TerminalFailure_ClearsCachedSession(
        HttpStatusCode statusCode,
        string payload)
    {
        var existing = CreateExpiredSession();
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = JsonContent(payload),
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

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}")]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}")]
    [InlineData(HttpStatusCode.BadRequest, "{\"code\":\"refresh_token_expired\"}")]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"REFRESH_TOKEN_REUSED\"}}")]
    [InlineData(HttpStatusCode.Forbidden, "{\"code\":\"refresh_token_invalidated\"}")]
    public void KnownRefreshFailures_AreTerminal(HttpStatusCode statusCode, string payload)
    {
        Assert.Equal(CodexAuthFailureKind.Terminal, CodexOAuthClient.ClassifyFailure(statusCode, payload));
    }

    [Theory]
    [InlineData(
        "{\"error\":\"invalid_grant\",\"error_description\":\"Authorization code expired.\\nSign in again.\"}",
        "Authorization code expired. Sign in again.")]
    [InlineData(
        "{\"error\":{\"code\":\"invalid_grant\",\"message\":\"The provider rejected this request.\\r\\nRetry authorization.\"}}",
        "The provider rejected this request. Retry authorization.")]
    public async Task CompleteAuthorizationAsync_ProviderErrorIncludesSanitizedDetail(
        string payload,
        string expectedDetail)
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = JsonContent(payload),
        }));
        var context = CreateContext();
        using var strategy = CreateStrategy(context, handler);
        strategy.CreateAuthorizationUrl("auth-session", RegisteredCallbackUri);

        var error = await Assert.ThrowsAsync<CodexOAuthException>(() =>
            strategy.CompleteAuthorizationAsync(
                "auth-session",
                new Dictionary<string, string?>
                {
                    ["code"] = "authorization-code",
                    ["state"] = "auth-session",
                }));

        Assert.Equal(CodexAuthFailureKind.Terminal, error.FailureKind);
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Contains("invalid_grant", error.Message, StringComparison.Ordinal);
        Assert.Contains(expectedDetail, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExchangeAuthorizationCodeAsync_StalledResponseBodyLogsTimeoutAfterHeaders()
    {
        const string authorizationCode = "authorization-code-must-not-be-logged";
        const string verifier = "pkce-verifier-must-not-be-logged";
        const string accessToken = "access-token-must-not-be-logged";
        const string refreshToken = "refresh-token-must-not-be-logged";
        const string rawPayloadMarker = "raw-stalled-payload-must-not-be-logged";
        var rawPayload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["access_token"] = accessToken,
            ["refresh_token"] = refreshToken,
            ["diagnostic_marker"] = rawPayloadMarker,
        });
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingReadStream(rawPayload)),
        }));
        var eventLogger = new RecordingEventLogger();
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.openai",
            logging: new TestPackageLogging(eventLogger));
        var oauthClient = new CodexOAuthClient(
            () => new HttpClient(handler, disposeHandler: false),
            new CodexOAuthTokenParser(TimeProvider.System),
            new CodexAuthTelemetry(context, "codex-connected"),
            TimeSpan.FromMilliseconds(100));

        var error = await Assert.ThrowsAsync<CodexOAuthException>(() =>
            oauthClient.ExchangeAuthorizationCodeAsync(
                authorizationCode,
                verifier,
                RegisteredCallbackUri.AbsoluteUri,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(CodexAuthFailureKind.Transient, error.FailureKind);
        Assert.Equal(HttpStatusCode.OK, error.StatusCode);
        Assert.Contains("timed out", error.Message, StringComparison.OrdinalIgnoreCase);
        var events = eventLogger.Entries;
        var headersIndex = Array.FindIndex(
            events,
            entry => entry.EventName == "openai.codex.auth.token_exchange.headers_received");
        var timeoutIndex = Array.FindIndex(
            events,
            entry => entry.EventName == "openai.codex.auth.token_exchange.timeout");
        Assert.True(headersIndex >= 0, "Expected headers-received telemetry.");
        Assert.True(timeoutIndex > headersIndex, "Expected terminal timeout telemetry after headers telemetry.");
        Assert.Equal(
            timeoutIndex,
            Array.FindLastIndex(events, entry => entry.EventName.StartsWith(
                "openai.codex.auth.token_exchange.",
                StringComparison.Ordinal)));
        Assert.Equal(PackageLogLevel.Warning, events[timeoutIndex].Level);
        Assert.Equal(200, events[timeoutIndex].Attributes["http.status_code"]);
        var diagnostics = FormatDiagnostics(events);
        Assert.DoesNotContain(authorizationCode, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(verifier, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(accessToken, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(refreshToken, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(rawPayloadMarker, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(rawPayload, diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExchangeAuthorizationCodeAsync_DiagnosticsDoNotExposeSecretsOrRawPayload()
    {
        const string authorizationCode = "authorization-code-diagnostic-secret";
        const string verifier = "pkce-verifier-diagnostic-secret";
        const string accessToken = "access-token-diagnostic-secret";
        const string refreshToken = "refresh-token-diagnostic-secret";
        const string rawPayloadMarker = "raw-response-payload-diagnostic-secret";
        var idToken = CreateJwt(new Dictionary<string, object?>
        {
            ["chatgpt_account_id"] = "account-id",
        });
        var rawPayload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id_token"] = idToken,
            ["access_token"] = accessToken,
            ["refresh_token"] = refreshToken,
            ["expires_in"] = 3600,
            ["diagnostic_marker"] = rawPayloadMarker,
        });
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent(rawPayload),
        }));
        var eventLogger = new RecordingEventLogger();
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.openai",
            logging: new TestPackageLogging(eventLogger));
        var oauthClient = new CodexOAuthClient(
            () => new HttpClient(handler, disposeHandler: false),
            new CodexOAuthTokenParser(TimeProvider.System),
            new CodexAuthTelemetry(context, "codex-connected"));

        var session = await oauthClient.ExchangeAuthorizationCodeAsync(
            authorizationCode,
            verifier,
            RegisteredCallbackUri.AbsoluteUri,
            CancellationToken.None);

        Assert.Equal(accessToken, session.AccessToken);
        Assert.Equal(refreshToken, session.RefreshToken);
        var diagnostics = FormatDiagnostics(eventLogger.Entries);
        Assert.DoesNotContain(authorizationCode, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(verifier, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(idToken, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(accessToken, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(refreshToken, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(rawPayloadMarker, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(rawPayload, diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExchangeAuthorizationCodeAsync_ThrowingEventLoggerDoesNotBreakSuccess()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(CreateTokenResponse("access-token", "refresh-token")));
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.openai",
            logging: new TestPackageLogging(new ThrowingEventLogger()));
        var oauthClient = new CodexOAuthClient(
            () => new HttpClient(handler, disposeHandler: false),
            new CodexOAuthTokenParser(TimeProvider.System),
            new CodexAuthTelemetry(context, "codex-connected"));

        var session = await oauthClient.ExchangeAuthorizationCodeAsync(
            "authorization-code",
            "verifier",
            RegisteredCallbackUri.AbsoluteUri,
            CancellationToken.None);

        Assert.Equal("access-token", session.AccessToken);
        Assert.Equal("refresh-token", session.RefreshToken);
    }

    [Fact]
    public async Task CompleteAuthorizationAsync_NeverCompletingEventLoggerIsBoundedAndSessionIsSaved()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(CreateTokenResponse("access-token", "refresh-token")));
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.openai",
            logging: new TestPackageLogging(new NeverCompletingEventLogger()));
        using var strategy = CreateStrategy(
            context,
            handler,
            telemetryWriteTimeout: TimeSpan.FromMilliseconds(20));
        strategy.CreateAuthorizationUrl("auth-session", RegisteredCallbackUri);

        var session = await strategy.CompleteAuthorizationAsync(
            "auth-session",
            new Dictionary<string, string?>
            {
                ["code"] = "authorization-code",
                ["state"] = "auth-session",
            }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("access-token", session.AccessToken);
        Assert.Equal(session, await strategy.GetCachedSessionAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, false)]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    public async Task RefreshAsync_StalledResponseBodyPreservesKnownStatus(
        HttpStatusCode statusCode,
        bool expectTerminal)
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StreamContent(new StallingReadStream("{\"code\":\"temporarily_unavailable\"}")),
        }));
        var context = CreateContext();
        var oauthClient = new CodexOAuthClient(
            () => new HttpClient(handler, disposeHandler: false),
            new CodexOAuthTokenParser(TimeProvider.System),
            new CodexAuthTelemetry(context, "codex-connected"),
            TimeSpan.FromMilliseconds(100));

        var result = await oauthClient.RefreshAsync(CreateExpiredSession(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(result.Session);
        Assert.Equal(
            expectTerminal ? CodexAuthFailureKind.Terminal : CodexAuthFailureKind.Transient,
            result.FailureKind);
        Assert.Equal(statusCode, result.StatusCode);
    }

    [Fact]
    public async Task ExchangeAuthorizationCodeAsync_OversizedResponseIsRejected()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[CodexOAuthClient.MaximumTokenResponseBytes + 1]),
        }));
        var context = CreateContext();
        var oauthClient = new CodexOAuthClient(
            () => new HttpClient(handler, disposeHandler: false),
            new CodexOAuthTokenParser(TimeProvider.System),
            new CodexAuthTelemetry(context, "codex-connected"));

        var error = await Assert.ThrowsAsync<CodexOAuthException>(() =>
            oauthClient.ExchangeAuthorizationCodeAsync(
                "authorization-code",
                "verifier",
                RegisteredCallbackUri.AbsoluteUri,
                CancellationToken.None));

        Assert.Equal(CodexAuthFailureKind.Terminal, error.FailureKind);
        Assert.Contains("size", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelCallbackAsync_RemovesPendingPkceState()
    {
        var requestCount = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(CreateTokenResponse("unexpected", "refresh-token"));
        });
        var context = CreateContext();
        using var strategy = CreateStrategy(context, handler);
        var authHandler = new OpenAiPackageAuthHandler(context, strategy);
        await authHandler.StartAuthorizationAsync(
            new PackageAuthSessionStartContext("auth-session", RegisteredCallbackUri));

        await authHandler.CancelCallbackAsync(new PackageCallbackCancellationContext(
            "auth-session",
            PackageCallbackCancellationReason.Expired));
        var status = await authHandler.CompleteAuthorizationAsync(
            new PackageAuthSessionCompletionContext(
                "auth-session",
                new Dictionary<string, string?>
                {
                    ["code"] = "authorization-code",
                    ["state"] = "auth-session",
                }));

        Assert.Equal(PackageAuthStatusKind.Failed, status.Status);
        Assert.Equal(0, requestCount);
        Assert.Null(await strategy.GetCachedSessionAsync());
    }

    [Fact]
    public async Task CancelCallbackAsync_CancelsInFlightTokenExchange()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            requestStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                requestCanceled.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("Unreachable.");
        });
        var context = CreateContext();
        using var strategy = CreateStrategy(context, handler);
        var authHandler = new OpenAiPackageAuthHandler(context, strategy);
        await authHandler.StartAuthorizationAsync(
            new PackageAuthSessionStartContext("auth-session", RegisteredCallbackUri));
        var completion = authHandler.CompleteAuthorizationAsync(
            new PackageAuthSessionCompletionContext(
                "auth-session",
                new Dictionary<string, string?>
                {
                    ["code"] = "authorization-code",
                    ["state"] = "auth-session",
                }));
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await authHandler.CancelCallbackAsync(new PackageCallbackCancellationContext(
            "auth-session",
            PackageCallbackCancellationReason.CallerRequested));
        var status = await completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PackageAuthStatusKind.Failed, status.Status);
        await requestCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await strategy.GetCachedSessionAsync());
    }

    [Fact]
    public async Task CancelCallbackAsync_AfterSecretPersistenceClearsNewSession()
    {
        var secrets = new BlockingAfterSaveSecrets();
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(CreateTokenResponse("access-token", "refresh-token")));
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.openai",
            secrets: secrets);
        using var strategy = CreateStrategy(context, handler);
        var authHandler = new OpenAiPackageAuthHandler(context, strategy);
        await authHandler.StartAuthorizationAsync(
            new PackageAuthSessionStartContext("auth-session", RegisteredCallbackUri));
        var completion = authHandler.CompleteAuthorizationAsync(
            new PackageAuthSessionCompletionContext(
                "auth-session",
                new Dictionary<string, string?>
                {
                    ["code"] = "authorization-code",
                    ["state"] = "auth-session",
                }));
        await secrets.Saved.WaitAsync(TimeSpan.FromSeconds(5));

        await authHandler.CancelCallbackAsync(new PackageCallbackCancellationContext(
            "auth-session",
            PackageCallbackCancellationReason.CallerRequested));
        secrets.Release();
        var status = await completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PackageAuthStatusKind.Failed, status.Status);
        Assert.Null(await strategy.GetCachedSessionAsync());
        Assert.Equal(1, secrets.DeleteCount);
    }

    [Fact]
    public async Task CancelCallbackAsync_AfterSecretPersistenceRestoresPreviousSession()
    {
        var previousSession = CreateExpiredSession();
        var secrets = new BlockingAfterSaveSecrets(previousSession);
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(CreateTokenResponse("new-access", "new-refresh")));
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.openai",
            secrets: secrets);
        using var strategy = CreateStrategy(context, handler);
        var authHandler = new OpenAiPackageAuthHandler(context, strategy);
        await authHandler.StartAuthorizationAsync(
            new PackageAuthSessionStartContext("auth-session", RegisteredCallbackUri));
        var completion = authHandler.CompleteAuthorizationAsync(
            new PackageAuthSessionCompletionContext(
                "auth-session",
                new Dictionary<string, string?>
                {
                    ["code"] = "authorization-code",
                    ["state"] = "auth-session",
                }));
        await secrets.Saved.WaitAsync(TimeSpan.FromSeconds(5));

        await authHandler.CancelCallbackAsync(new PackageCallbackCancellationContext(
            "auth-session",
            PackageCallbackCancellationReason.CallerRequested));
        secrets.Release();
        var status = await completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PackageAuthStatusKind.Failed, status.Status);
        Assert.Equal(previousSession, await strategy.GetCachedSessionAsync());
        Assert.Equal(2, secrets.SaveCount);
    }

    [Fact]
    public async Task LifecycleChangeDuringSavedAuthorizationSkipsPreviousSessionRestore()
    {
        var previousSession = CreateExpiredSession();
        var secrets = new BlockingAfterSaveSecrets(previousSession);
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(CreateTokenResponse("new-access", "new-refresh")));
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.openai",
            secrets: secrets);
        using var strategy = CreateStrategy(context, handler);
        var authHandler = new OpenAiPackageAuthHandler(context, strategy);
        await authHandler.StartAuthorizationAsync(
            new PackageAuthSessionStartContext("auth-session", RegisteredCallbackUri));
        var completion = authHandler.CompleteAuthorizationAsync(
            new PackageAuthSessionCompletionContext(
                "auth-session",
                new Dictionary<string, string?>
                {
                    ["code"] = "authorization-code",
                    ["state"] = "auth-session",
                }));
        await secrets.Saved.WaitAsync(TimeSpan.FromSeconds(5));

        var clear = strategy.ClearSessionAsync();
        secrets.Release();
        var status = await completion.WaitAsync(TimeSpan.FromSeconds(5));
        await clear.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PackageAuthStatusKind.Failed, status.Status);
        Assert.Null(await strategy.GetCachedSessionAsync());
        Assert.Equal(1, secrets.SaveCount);
    }

    [Fact]
    public async Task DisposeDuringTokenExchange_CancelsWithoutObjectDisposedException()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });
        var context = CreateContext();
        var strategy = CreateStrategy(context, handler);
        strategy.CreateAuthorizationUrl("auth-session", RegisteredCallbackUri);
        var completion = strategy.CompleteAuthorizationAsync(
            "auth-session",
            new Dictionary<string, string?>
            {
                ["code"] = "authorization-code",
                ["state"] = "auth-session",
            });
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        strategy.Dispose();
        var error = await Record.ExceptionAsync(async () =>
            await completion.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.IsNotType<ObjectDisposedException>(error);
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
            new Dictionary<string, string?>
            {
                ["code"] = "authorization-code",
                ["state"] = "auth-session",
            }));

        Assert.Equal(0, requestCount);
        Assert.Null(await strategy.GetCachedSessionAsync());
    }

    private static CodexConnectedAuthStrategy CreateStrategy(
        ProviderTestPackageContext context,
        HttpMessageHandler handler,
        TimeProvider? timeProvider = null,
        TimeSpan? pendingLifetime = null,
        TimeSpan? telemetryWriteTimeout = null)
        => new(
            context,
            () => new HttpClient(handler, disposeHandler: false),
            timeProvider ?? TimeProvider.System,
            pendingLifetime ?? CodexBrowserAuthorizationCoordinator.DefaultPendingLifetime,
            telemetryWriteTimeout);

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

    private static string FormatDiagnostics(IEnumerable<RecordedEvent> events)
        => string.Join(
            "\n",
            events.Select(entry =>
                $"{entry.Level}|{entry.EventName}|{entry.Message}|{string.Join(",", entry.Attributes.Select(attribute => $"{attribute.Key}={attribute.Value}"))}|{entry.Exception}"));

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

    private sealed class TestPackageLogging(IPackageEventLogger events) : IPackageLogging
    {
        public Microsoft.Extensions.Logging.ILoggerFactory LoggerFactory
            => NullPackageLogging.Instance.LoggerFactory;

        public IPackageEventLogger Events { get; } = events;
    }

    private sealed class RecordingEventLogger : IPackageEventLogger
    {
        private readonly object _gate = new();
        private readonly List<RecordedEvent> _entries = [];

        public RecordedEvent[] Entries
        {
            get
            {
                lock (_gate)
                {
                    return [.. _entries];
                }
            }
        }

        public ValueTask WriteAsync(
            PackageLogLevel level,
            string eventName,
            string message,
            IReadOnlyDictionary<string, object?>? attributes = null,
            Exception? exception = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var copiedAttributes = attributes?.ToDictionary(
                static attribute => attribute.Key,
                static attribute => attribute.Value,
                StringComparer.Ordinal) ?? new Dictionary<string, object?>(StringComparer.Ordinal);
            lock (_gate)
            {
                _entries.Add(new RecordedEvent(level, eventName, message, copiedAttributes, exception));
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingEventLogger : IPackageEventLogger
    {
        public ValueTask WriteAsync(
            PackageLogLevel level,
            string eventName,
            string message,
            IReadOnlyDictionary<string, object?>? attributes = null,
            Exception? exception = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Test event logger failure.");
    }

    private sealed class NeverCompletingEventLogger : IPackageEventLogger
    {
        private readonly Task _never = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously).Task;

        public ValueTask WriteAsync(
            PackageLogLevel level,
            string eventName,
            string message,
            IReadOnlyDictionary<string, object?>? attributes = null,
            Exception? exception = null,
            CancellationToken cancellationToken = default)
            => new(_never);
    }

    private sealed class BlockingAfterSaveSecrets : Sunder.Sdk.Abstractions.IPackageSecrets
    {
        private readonly ProviderTestSecrets _inner;
        private readonly TaskCompletionSource _saved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _deleteCount;
        private int _saveCount;

        public BlockingAfterSaveSecrets(OpenAiCodexSession? initialSession = null)
        {
            _inner = new ProviderTestSecrets(initialSession is null
                ? null
                : new Dictionary<string, string>
                {
                    ["auth.codex.session"] = JsonSerializer.Serialize(initialSession),
                });
        }

        public Task Saved => _saved.Task;
        public int DeleteCount => Volatile.Read(ref _deleteCount);
        public int SaveCount => Volatile.Read(ref _saveCount);

        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
            => _inner.GetSecretAsync(key, cancellationToken);

        public async Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            var saveCount = Interlocked.Increment(ref _saveCount);
            await _inner.SetSecretAsync(key, value, cancellationToken);
            if (saveCount == 1)
            {
                _saved.TrySetResult();
                await _release.Task;
            }
        }

        public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _deleteCount);
            return _inner.DeleteSecretAsync(key, cancellationToken);
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed record RecordedEvent(
        PackageLogLevel Level,
        string EventName,
        string Message,
        IReadOnlyDictionary<string, object?> Attributes,
        Exception? Exception);

    private sealed class StallingReadStream(string prefix) : Stream
    {
        private readonly byte[] _prefix = Encoding.UTF8.GetBytes(prefix);
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_offset < _prefix.Length)
            {
                var count = Math.Min(buffer.Length, _prefix.Length - _offset);
                _prefix.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }
}
