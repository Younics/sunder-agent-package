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
        TimeSpan pendingAuthorizationLifetime,
        TimeSpan? telemetryWriteTimeout = null)
    {
        _timeProvider = timeProvider;
        _telemetry = new CodexAuthTelemetry(packageContext, ModeId, telemetryWriteTimeout);
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

            await _telemetry.WriteAsync(
                PackageLogLevel.Information,
                "openai.codex.auth.silent.refresh_required",
                "Cached Codex session is expired or near expiry; refreshing silently.",
                attributes: new Dictionary<string, object?> { ["auth.expires_at"] = cached.ExpiresAtUtc });
            return await RefreshAndSaveAsync(cached, "silent_refresh", operation.Generation, operation.Token);
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
                await _telemetry.WriteAsync(
                    PackageLogLevel.Warning,
                    "openai.codex.auth.refresh.no_token",
                    "No Codex refresh token is available for silent refresh.");
                return null;
            }

            return await RefreshAndSaveAsync(cached, "explicit_refresh", operation.Generation, operation.Token);
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

    internal void CancelAuthorization(string authSessionId)
    {
        lock (_lifecycleGate)
        {
            _browserAuthorization.CancelAuthorization(authSessionId);
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
            var previousSession = await _sessionStore.GetAsync(operation.Token);
            OpenAiCodexSession? savedSession = null;
            try
            {
                var session = await _browserAuthorization.CompleteAuthorizationAsync(
                    authSessionId,
                    queryValues,
                    async (authorizedSession, authorizationToken) =>
                    {
                        if (!await TrySaveSessionAsync(
                                "runtime_callback",
                                authorizedSession,
                                operation.Generation,
                                authorizationToken,
                                () => savedSession = authorizedSession))
                        {
                            throw new OperationCanceledException(authorizationToken);
                        }
                    },
                    operation.Token);
                return session;
            }
            catch (OperationCanceledException) when (savedSession is not null)
            {
                await TryRestoreSessionAfterCancellationAsync(
                    previousSession,
                    savedSession,
                    operation.Generation);
                throw;
            }
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
    }

    private bool RequiresRefresh(OpenAiCodexSession session)
        => session.ExpiresAtUtc <= _timeProvider.GetUtcNow().Add(SessionExpiryBuffer);

    private async Task<OpenAiCodexSession?> RefreshAndSaveAsync(
        OpenAiCodexSession existingSession,
        string flow,
        long generation,
        CancellationToken cancellationToken)
    {
        var result = await _oauthClient.RefreshAsync(existingSession, cancellationToken);
        if (result.Session is not null)
        {
            return await TrySaveSessionAsync(flow, result.Session, generation, cancellationToken)
                ? result.Session
                : null;
        }

        if (result.FailureKind == CodexAuthFailureKind.Terminal)
        {
            await TryClearSessionAsync(generation, cancellationToken);
            await _telemetry.WriteAsync(
                PackageLogLevel.Warning,
                "openai.codex.auth.refresh.terminal_failure",
                "Codex refresh failed terminally; cached session was removed.",
                attributes: StatusAttributes(result.StatusCode));
        }
        else
        {
            await _telemetry.WriteAsync(
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
        CancellationToken cancellationToken,
        Action? sessionSaved = null)
    {
        lock (_lifecycleGate)
        {
            if (generation != _sessionGeneration || _lifecycleCancellation.IsCancellationRequested)
            {
                return false;
            }

        }

        await _sessionStore.SaveAsync(session, cancellationToken);
        sessionSaved?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        await _telemetry.SessionSavedAsync(flow, session);
        cancellationToken.ThrowIfCancellationRequested();
        return true;
    }

    private async Task TryRestoreSessionAfterCancellationAsync(
        OpenAiCodexSession? previousSession,
        OpenAiCodexSession canceledSession,
        long generation)
    {
        if (!IsCurrentGeneration(generation))
        {
            return;
        }

        try
        {
            var currentSession = await _sessionStore.GetAsync(CancellationToken.None);
            if (!IsCurrentGeneration(generation) || currentSession != canceledSession)
            {
                return;
            }

            if (previousSession is null)
            {
                await _sessionStore.ClearAsync(CancellationToken.None);
            }
            else
            {
                await _sessionStore.SaveAsync(previousSession, CancellationToken.None);
            }

            if (IsCurrentGeneration(generation))
            {
                await _telemetry.WriteAsync(
                    PackageLogLevel.Information,
                    "openai.codex.auth.session.restored_after_cancellation",
                    previousSession is null
                        ? "Canceled Codex authorization session was removed."
                        : "Previous Codex auth session was restored after cancellation.",
                    attributes: new Dictionary<string, object?>
                    {
                        ["auth.previous_session_present"] = previousSession is not null,
                    });
            }
        }
        catch
        {
            await _telemetry.WriteAsync(
                PackageLogLevel.Warning,
                "openai.codex.auth.session.restore_failed",
                "Codex auth session could not be restored after cancellation.",
                attributes: new Dictionary<string, object?>
                {
                    ["auth.previous_session_present"] = previousSession is not null,
                });
        }
    }

    private bool IsCurrentGeneration(long generation)
    {
        lock (_lifecycleGate)
        {
            return generation == _sessionGeneration && !_lifecycleCancellation.IsCancellationRequested;
        }
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
