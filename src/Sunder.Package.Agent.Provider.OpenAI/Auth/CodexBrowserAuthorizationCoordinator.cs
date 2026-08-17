using System.Security.Cryptography;
using System.Text;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Provider.OpenAI.Auth;

internal sealed class CodexBrowserAuthorizationCoordinator : IDisposable
{
    private const string AuthorizeUrl = "https://auth.openai.com/oauth/authorize";
    private const string Scope = "openid profile email offline_access api.connectors.read api.connectors.invoke";
    private const string LifeSciencesStateSuffix = ".onboarding_entrypoint=life_sciences";
    private readonly CodexOAuthClient _oauthClient;
    private readonly CodexAuthTelemetry _telemetry;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pendingLifetime;
    private readonly object _pendingGate = new();
    private readonly Dictionary<string, PendingBrowserAuthorization> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingBrowserAuthorization> _inFlight = new(StringComparer.Ordinal);
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

    public static TimeSpan DefaultPendingLifetime { get; } = TimeSpan.FromMinutes(5);

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
        PendingBrowserAuthorization? replacedPending;
        PendingBrowserAuthorization? replacedInFlight;
        lock (_pendingGate)
        {
            _pending.Remove(authSessionId, out replacedPending);
            _inFlight.Remove(authSessionId, out replacedInFlight);
            _pending[authSessionId] = pending;
        }

        CancelPending(replacedPending);
        replacedInFlight?.CancelWork();
        _ = ExpirePendingFlowAsync(authSessionId, pending);
        _telemetry.WriteObserved(
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
        Func<OpenAiCodexSession, CancellationToken, Task> saveSession,
        CancellationToken cancellationToken)
    {
        await _interactiveGate.WaitAsync(cancellationToken);
        PendingBrowserAuthorization? pending = null;
        var completed = false;
        try
        {
            ValidateCallbackState(authSessionId, queryValues);
            pending = TakePending(authSessionId);
            ValidateCallback(queryValues, out var code);
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                pending.WorkCancellation.Token);
            var session = await _oauthClient.ExchangeAuthorizationCodeAsync(
                code,
                pending.Verifier,
                pending.RedirectUri,
                operationCancellation.Token);
            await saveSession(session, operationCancellation.Token);
            operationCancellation.Token.ThrowIfCancellationRequested();
            if (!TryCompleteInFlight(authSessionId, pending))
            {
                pending.CancelWork();
                throw new OperationCanceledException(pending.WorkCancellation.Token);
            }

            completed = true;
            return session;
        }
        finally
        {
            if (pending is not null)
            {
                FinishInFlight(authSessionId, pending, completed);
            }

            _interactiveGate.Release();
        }
    }

    public void Dispose()
    {
        CancelPendingAuthorizations();
    }

    public void CancelPendingAuthorizations()
    {
        PendingBrowserAuthorization[] pending;
        PendingBrowserAuthorization[] inFlight;
        lock (_pendingGate)
        {
            pending = _pending.Values.ToArray();
            inFlight = _inFlight.Values.ToArray();
            _pending.Clear();
            _inFlight.Clear();
        }

        foreach (var authorization in pending)
        {
            CancelPending(authorization);
        }

        foreach (var authorization in inFlight)
        {
            authorization.CancelWork();
        }
    }

    public void CancelAuthorization(string authSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authSessionId);
        PendingBrowserAuthorization? pending;
        PendingBrowserAuthorization? inFlight;
        lock (_pendingGate)
        {
            _pending.Remove(authSessionId, out pending);
            _inFlight.Remove(authSessionId, out inFlight);
        }

        CancelPending(pending);
        inFlight?.CancelWork();
    }

    private PendingBrowserAuthorization TakePending(string authSessionId)
    {
        PendingBrowserAuthorization? pending;
        var expired = false;
        lock (_pendingGate)
        {
            if (!_pending.Remove(authSessionId, out pending))
            {
                throw new InvalidOperationException("No pending OpenAI browser auth session was found.");
            }

            expired = pending.ExpiresAtUtc <= _timeProvider.GetUtcNow();
            if (!expired)
            {
                _inFlight[authSessionId] = pending;
            }
        }

        pending.CancelExpiration();
        if (expired)
        {
            pending.CancelWork();
            pending.DisposeWork();
            throw new InvalidOperationException("The pending OpenAI browser auth session expired. Start authorization again.");
        }

        return pending;
    }

    private async Task ExpirePendingFlowAsync(string authSessionId, PendingBrowserAuthorization pending)
    {
        try
        {
            await Task.Delay(_pendingLifetime, _timeProvider, pending.ExpirationCancellation.Token);
            var removed = false;
            lock (_pendingGate)
            {
                if (_pending.TryGetValue(authSessionId, out var current) && ReferenceEquals(current, pending))
                {
                    removed = _pending.Remove(authSessionId);
                }
            }

            if (removed)
            {
                pending.CancelWork();
                pending.DisposeWork();
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
        List<PendingBrowserAuthorization>? expired = null;
        lock (_pendingGate)
        {
            foreach (var entry in _pending.ToArray())
            {
                if (entry.Value.ExpiresAtUtc <= now && _pending.Remove(entry.Key))
                {
                    (expired ??= []).Add(entry.Value);
                }
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var authorization in expired)
        {
            CancelPending(authorization);
        }
    }

    private bool TryCompleteInFlight(string authSessionId, PendingBrowserAuthorization pending)
    {
        lock (_pendingGate)
        {
            if (!_inFlight.TryGetValue(authSessionId, out var current)
                || !ReferenceEquals(current, pending)
                || pending.WorkCancellation.IsCancellationRequested)
            {
                return false;
            }

            _inFlight.Remove(authSessionId);
            return true;
        }
    }

    private void FinishInFlight(
        string authSessionId,
        PendingBrowserAuthorization pending,
        bool completed)
    {
        if (!completed)
        {
            lock (_pendingGate)
            {
                if (_inFlight.TryGetValue(authSessionId, out var current) && ReferenceEquals(current, pending))
                {
                    _inFlight.Remove(authSessionId);
                }
            }
        }

        pending.DisposeWork();
    }

    private static void CancelPending(PendingBrowserAuthorization? pending)
    {
        if (pending is null)
        {
            return;
        }

        pending.CancelExpiration();
        pending.CancelWork();
        pending.DisposeWork();
    }

    private static void ValidateCallbackState(
        string authSessionId,
        IReadOnlyDictionary<string, string?> queryValues)
    {
        if (!queryValues.TryGetValue("state", out var state)
            || (!string.Equals(state, authSessionId, StringComparison.Ordinal)
                && !string.Equals(
                    state,
                    authSessionId + LifeSciencesStateSuffix,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("OpenAI browser sign-in returned an invalid authorization state.");
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
        public CancellationTokenSource WorkCancellation { get; } = new();

        public void CancelExpiration()
        {
            try
            {
                if (!ExpirationCancellation.IsCancellationRequested)
                {
                    ExpirationCancellation.Cancel();
                }
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void CancelWork()
        {
            try
            {
                if (!WorkCancellation.IsCancellationRequested)
                {
                    WorkCancellation.Cancel();
                }
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void DisposeExpiration() => ExpirationCancellation.Dispose();

        public void DisposeWork() => WorkCancellation.Dispose();
    }
}
