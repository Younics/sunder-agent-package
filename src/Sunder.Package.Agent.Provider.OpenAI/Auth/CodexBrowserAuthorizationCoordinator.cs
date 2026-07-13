using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Provider.OpenAI.Auth;

internal sealed class CodexBrowserAuthorizationCoordinator : IDisposable
{
    private const string AuthorizeUrl = "https://auth.openai.com/oauth/authorize";
    private const string Scope = "openid profile email offline_access api.connectors.read api.connectors.invoke";
    private readonly CodexOAuthClient _oauthClient;
    private readonly CodexAuthTelemetry _telemetry;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pendingLifetime;
    private readonly ConcurrentDictionary<string, PendingBrowserAuthorization> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _interactiveGate = new(1, 1);

    public CodexBrowserAuthorizationCoordinator(
        CodexOAuthClient oauthClient,
        CodexAuthTelemetry telemetry,
        TimeProvider timeProvider,
        TimeSpan pendingLifetime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pendingLifetime, TimeSpan.Zero);
        _oauthClient = oauthClient;
        _telemetry = telemetry;
        _timeProvider = timeProvider;
        _pendingLifetime = pendingLifetime;
    }

    public static TimeSpan DefaultPendingLifetime { get; } = TimeSpan.FromMinutes(10);

    public string CreateAuthorizationUrl(string authSessionId, Uri callbackUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authSessionId);
        ArgumentNullException.ThrowIfNull(callbackUri);
        ValidateCallbackUri(callbackUri);
        RemoveExpiredPendingFlows();
        var verifier = CreatePkceVerifier();
        var pending = new PendingBrowserAuthorization(
            verifier,
            callbackUri.ToString(),
            _timeProvider.GetUtcNow().Add(_pendingLifetime));
        if (_pending.TryGetValue(authSessionId, out var replaced))
        {
            replaced.CancelExpiration();
        }

        _pending[authSessionId] = pending;
        _ = ExpirePendingFlowAsync(authSessionId, pending);
        _telemetry.Write(
            PackageLogLevel.Information,
            "openai.codex.auth.browser.start",
            "OpenAI browser authorization URL was created.",
            attributes: new Dictionary<string, object?>
            {
                ["auth.flow"] = "runtime_callback",
                ["auth.callback_uri"] = callbackUri.GetLeftPart(UriPartial.Path),
            });
        return BuildAuthorizationUrl(authSessionId, CreatePkceChallenge(verifier), pending.RedirectUri);
    }

    public async Task<OpenAiCodexSession> CompleteAuthorizationAsync(
        string authSessionId,
        IReadOnlyDictionary<string, string?> queryValues,
        CancellationToken cancellationToken)
    {
        await _interactiveGate.WaitAsync(cancellationToken);
        try
        {
            var pending = TakePending(authSessionId);
            ValidateCallback(queryValues, out var code);
            return await _oauthClient.ExchangeAuthorizationCodeAsync(
                code,
                pending.Verifier,
                pending.RedirectUri,
                cancellationToken);
        }
        finally
        {
            _interactiveGate.Release();
        }
    }

    public void Dispose()
    {
        CancelPendingAuthorizations();
        _interactiveGate.Dispose();
    }

    public void CancelPendingAuthorizations()
    {
        foreach (var entry in _pending)
        {
            if (_pending.TryRemove(entry.Key, out var pending))
            {
                pending.CancelExpiration();
            }
        }
    }

    private PendingBrowserAuthorization TakePending(string authSessionId)
    {
        if (!_pending.TryRemove(authSessionId, out var pending))
        {
            throw new InvalidOperationException("No pending OpenAI browser auth session was found.");
        }

        pending.CancelExpiration();
        if (pending.ExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            throw new InvalidOperationException("The pending OpenAI browser auth session expired. Start authorization again.");
        }

        return pending;
    }

    private async Task ExpirePendingFlowAsync(string authSessionId, PendingBrowserAuthorization pending)
    {
        try
        {
            await Task.Delay(_pendingLifetime, _timeProvider, pending.ExpirationCancellation.Token);
            if (_pending.TryGetValue(authSessionId, out var current) && ReferenceEquals(current, pending))
            {
                _pending.TryRemove(authSessionId, out _);
            }
        }
        catch (OperationCanceledException) when (pending.ExpirationCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            pending.DisposeExpiration();
        }
    }

    private void RemoveExpiredPendingFlows()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var entry in _pending)
        {
            if (entry.Value.ExpiresAtUtc <= now && _pending.TryRemove(entry.Key, out var expired))
            {
                expired.CancelExpiration();
            }
        }
    }

    private static void ValidateCallback(IReadOnlyDictionary<string, string?> queryValues, out string code)
    {
        if (queryValues.TryGetValue("error", out var error) && !string.IsNullOrWhiteSpace(error))
        {
            queryValues.TryGetValue("error_description", out var description);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(description) ? error : description);
        }

        if (!queryValues.TryGetValue("code", out var codeValue) || string.IsNullOrWhiteSpace(codeValue))
        {
            throw new InvalidOperationException("OpenAI browser sign-in did not return an authorization code.");
        }

        code = codeValue;
    }

    private static void ValidateCallbackUri(Uri callbackUri)
    {
        if (!string.Equals(callbackUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(callbackUri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || callbackUri.Port is not (1455 or 1457)
            || !string.Equals(callbackUri.AbsolutePath, "/auth/callback", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(callbackUri.Query)
            || !string.IsNullOrEmpty(callbackUri.Fragment))
        {
            throw new InvalidOperationException(
                "OpenAI Codex authorization requires the host callback URI http://localhost:1455/auth/callback or http://localhost:1457/auth/callback.");
        }
    }

    private static string BuildAuthorizationUrl(string state, string challenge, string redirectUri)
    {
        var uriBuilder = new UriBuilder(AuthorizeUrl)
        {
            Query = string.Join("&", new[]
            {
                "response_type=code",
                $"client_id={Uri.EscapeDataString(CodexOAuthClient.ClientId)}",
                $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
                $"scope={Uri.EscapeDataString(Scope)}",
                $"code_challenge={Uri.EscapeDataString(challenge)}",
                "code_challenge_method=S256",
                $"state={Uri.EscapeDataString(state)}",
                "id_token_add_organizations=true",
                "codex_cli_simplified_flow=true",
                "originator=sunder",
            }),
        };
        return uriBuilder.ToString();
    }

    private static string CreatePkceVerifier()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string CreatePkceChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private sealed class PendingBrowserAuthorization(
        string verifier,
        string redirectUri,
        DateTimeOffset expiresAtUtc)
    {
        public string Verifier { get; } = verifier;
        public string RedirectUri { get; } = redirectUri;
        public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;
        public CancellationTokenSource ExpirationCancellation { get; } = new();

        public void CancelExpiration()
        {
            if (!ExpirationCancellation.IsCancellationRequested)
            {
                ExpirationCancellation.Cancel();
            }
        }

        public void DisposeExpiration() => ExpirationCancellation.Dispose();
    }
}
