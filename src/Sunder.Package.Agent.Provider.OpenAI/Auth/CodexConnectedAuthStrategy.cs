using System.Net;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Provider.OpenAI.Auth;

public sealed class CodexConnectedAuthStrategy : IDisposable
{
    private static readonly TimeSpan SessionExpiryBuffer = TimeSpan.FromMinutes(1);
    private readonly CodexSessionStore _sessionStore;
    private readonly CodexOAuthClient _oauthClient;
    private readonly CodexBrowserAuthorizationCoordinator _browserAuthorization;
    private readonly CodexAuthTelemetry _telemetry;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly object _lifecycleGate = new();
    private CancellationTokenSource _lifecycleCancellation = new();
    private long _sessionGeneration;

    public CodexConnectedAuthStrategy(IPackageContext packageContext)
        : this(
            packageContext,
            CodexHttpClientFactory.CreateAuthClient,
            TimeProvider.System,
            CodexBrowserAuthorizationCoordinator.DefaultPendingLifetime)
    {
    }

    internal CodexConnectedAuthStrategy(
        IPackageContext packageContext,
        Func<HttpClient> authClientFactory,
        TimeProvider timeProvider,
        TimeSpan pendingAuthorizationLifetime)
    {
        _timeProvider = timeProvider;
        _telemetry = new CodexAuthTelemetry(packageContext, ModeId);
        _sessionStore = new CodexSessionStore(packageContext.Secrets);
        _oauthClient = new CodexOAuthClient(authClientFactory, new CodexOAuthTokenParser(timeProvider), _telemetry);
        _browserAuthorization = new CodexBrowserAuthorizationCoordinator(
            _oauthClient,
            _telemetry,
            timeProvider,
            pendingAuthorizationLifetime);
    }

    public string ModeId { get; } = "codex-connected";

    public Task<OpenAiCodexSession?> GetCachedSessionAsync(CancellationToken cancellationToken = default)
        => _sessionStore.GetAsync(cancellationToken);

    public Task<OpenAiCodexSession> EnsureAuthenticatedAsync(CancellationToken cancellationToken = default)
        => EnsureAuthenticatedAsync(allowInteractive: true, cancellationToken);

    public async Task<OpenAiCodexSession?> TryEnsureAuthenticatedSilentlyAsync(CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation(cancellationToken);
        var enteredGate = false;
        try
        {
            await _sessionGate.WaitAsync(operation.Token);
            enteredGate = true;
            var cached = await _sessionStore.GetAsync(operation.Token);
            if (cached is null)
            {
                return null;
            }

            if (!RequiresRefresh(cached))
            {
                return cached;
            }

            _telemetry.Write(
                PackageLogLevel.Information,
                "openai.codex.auth.silent.refresh_required",
                "Cached Codex session is expired or near expiry; refreshing silently.",
                attributes: new Dictionary<string, object?> { ["auth.expires_at"] = cached.ExpiresAtUtc });
            return await RefreshAndSaveAsync(cached.RefreshToken, "silent_refresh", operation.Generation, operation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && operation.Token.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (enteredGate)
            {
                _sessionGate.Release();
            }
        }
    }

    public async Task<OpenAiCodexSession> EnsureAuthenticatedAsync(
        bool allowInteractive,
        CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation(cancellationToken);
        var enteredGate = false;
        try
        {
            await _sessionGate.WaitAsync(operation.Token);
            enteredGate = true;
            var cached = await _sessionStore.GetAsync(operation.Token);
            if (cached is not null && !RequiresRefresh(cached))
            {
                return cached;
            }

            if (cached is not null)
            {
                var refreshed = await RefreshAndSaveAsync(cached.RefreshToken, "refresh", operation.Generation, operation.Token);
                if (refreshed is not null)
                {
                    return refreshed;
                }
            }

            if (!allowInteractive)
            {
                throw new InvalidOperationException("OpenAI Codex session could not be refreshed silently.");
            }

            _telemetry.Write(
                PackageLogLevel.Information,
                "openai.codex.auth.interactive.start",
                "Starting interactive Codex browser authorization.");
            var session = await _browserAuthorization.SignInWithBrowserAsync(operation.Token);
            if (!await TrySaveSessionAsync("interactive", session, operation.Generation, operation.Token))
            {
                throw new OperationCanceledException(operation.Token);
            }

            return session;
        }
        finally
        {
            if (enteredGate)
            {
                _sessionGate.Release();
            }
        }
    }

    public async Task<OpenAiCodexSession?> TryRefreshSessionAsync(
        OpenAiCodexSession? expectedSession,
        CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation(cancellationToken);
        var enteredGate = false;
        try
        {
            await _sessionGate.WaitAsync(operation.Token);
            enteredGate = true;
            var cached = await _sessionStore.GetAsync(operation.Token);
            if (cached is null)
            {
                return null;
            }

            if (expectedSession is not null
                && !string.Equals(cached.AccessToken, expectedSession.AccessToken, StringComparison.Ordinal))
            {
                return cached;
            }

            var refreshToken = cached.RefreshToken;
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                _telemetry.Write(
                    PackageLogLevel.Warning,
                    "openai.codex.auth.refresh.no_token",
                    "No Codex refresh token is available for silent refresh.");
                return null;
            }

            return await RefreshAndSaveAsync(refreshToken, "explicit_refresh", operation.Generation, operation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && operation.Token.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (enteredGate)
            {
                _sessionGate.Release();
            }
        }
    }

    public string CreateAuthorizationUrl(string authSessionId, Uri callbackUri)
    {
        lock (_lifecycleGate)
        {
            return _browserAuthorization.CreateAuthorizationUrl(authSessionId, callbackUri);
        }
    }

    public async Task<OpenAiCodexSession> CompleteAuthorizationAsync(
        string authSessionId,
        IReadOnlyDictionary<string, string?> queryValues,
        CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation(cancellationToken);
        var enteredGate = false;
        try
        {
            await _sessionGate.WaitAsync(operation.Token);
            enteredGate = true;
            var session = await _browserAuthorization.CompleteAuthorizationAsync(
                authSessionId,
                queryValues,
                operation.Token);
            if (!await TrySaveSessionAsync("runtime_callback", session, operation.Generation, operation.Token))
            {
                throw new OperationCanceledException(operation.Token);
            }

            return session;
        }
        finally
        {
            if (enteredGate)
            {
                _sessionGate.Release();
            }
        }
    }

    public async Task ClearSessionAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource canceledGeneration;
        lock (_lifecycleGate)
        {
            canceledGeneration = _lifecycleCancellation;
            _lifecycleCancellation = new CancellationTokenSource();
            _sessionGeneration++;
            canceledGeneration.Cancel();
            _browserAuthorization.CancelPendingAuthorizations();
        }

        var enteredGate = false;
        try
        {
            await _sessionGate.WaitAsync(cancellationToken);
            enteredGate = true;
            await _sessionStore.ClearAsync(cancellationToken);
        }
        finally
        {
            if (enteredGate)
            {
                _sessionGate.Release();
            }

            canceledGeneration.Dispose();
        }
    }

    internal static string? ExtractChatGptAccountId(string jwt) => CodexAccountClaimReader.ExtractAccountId(jwt);

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            _sessionGeneration++;
            _lifecycleCancellation.Cancel();
        }

        _browserAuthorization.Dispose();
        _lifecycleCancellation.Dispose();
        _sessionGate.Dispose();
    }

    private bool RequiresRefresh(OpenAiCodexSession session)
        => session.ExpiresAtUtc <= _timeProvider.GetUtcNow().Add(SessionExpiryBuffer);

    private async Task<OpenAiCodexSession?> RefreshAndSaveAsync(
        string refreshToken,
        string flow,
        long generation,
        CancellationToken cancellationToken)
    {
        var result = await _oauthClient.RefreshAsync(refreshToken, cancellationToken);
        if (result.Session is not null)
        {
            return await TrySaveSessionAsync(flow, result.Session, generation, cancellationToken)
                ? result.Session
                : null;
        }

        if (result.FailureKind == CodexAuthFailureKind.Terminal)
        {
            await TryClearSessionAsync(generation, cancellationToken);
            _telemetry.Write(
                PackageLogLevel.Warning,
                "openai.codex.auth.refresh.terminal_failure",
                "Codex refresh failed terminally; cached session was removed.",
                attributes: StatusAttributes(result.StatusCode));
        }
        else
        {
            _telemetry.Write(
                PackageLogLevel.Warning,
                "openai.codex.auth.refresh.transient_failure",
                "Codex refresh failed transiently; the cached session was retained.",
                attributes: StatusAttributes(result.StatusCode));
        }

        return null;
    }

    private async Task<bool> TrySaveSessionAsync(
        string flow,
        OpenAiCodexSession session,
        long generation,
        CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            if (generation != _sessionGeneration || _lifecycleCancellation.IsCancellationRequested)
            {
                return false;
            }

        }

        await _sessionStore.SaveAsync(session, cancellationToken);
        _telemetry.SessionSaved(flow, session);
        return true;
    }

    private async Task TryClearSessionAsync(long generation, CancellationToken cancellationToken)
    {
        var shouldClear = false;
        lock (_lifecycleGate)
        {
            shouldClear = generation == _sessionGeneration;
        }

        if (shouldClear)
        {
            await _sessionStore.ClearAsync(cancellationToken);
        }
    }

    private AuthOperation BeginOperation(CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            return new AuthOperation(
                _sessionGeneration,
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _lifecycleCancellation.Token));
        }
    }

    private static IReadOnlyDictionary<string, object?>? StatusAttributes(HttpStatusCode? statusCode)
        => statusCode is null
            ? null
            : new Dictionary<string, object?> { ["http.status_code"] = (int)statusCode.Value };

    private sealed class AuthOperation(long generation, CancellationTokenSource cancellation) : IDisposable
    {
        public long Generation { get; } = generation;
        public CancellationToken Token => cancellation.Token;
        public void Dispose() => cancellation.Dispose();
    }
}
