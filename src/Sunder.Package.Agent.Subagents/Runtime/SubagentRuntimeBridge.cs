using Microsoft.Extensions.Logging;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Package.Agent.Shared.PackageViews;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Services;
#if SUNDER_SUBAGENTS_APP
using Sunder.Generated.Packages.Younics_Ai_Agent_Subagents_Services;
#endif

namespace Sunder.Package.Agent.Subagents.Runtime;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal interface ISubagentManagementGateway
{
    event Action? SubagentsChanged;
    event Action? CatalogChanged;
    Task<IReadOnlyList<SubagentRecord>> ListSubagentsAsync(CancellationToken cancellationToken = default);
    Task<SubagentRecord> CreateSubagentAsync(string displayName, CancellationToken cancellationToken = default);
    Task<SubagentRecord> SaveSubagentAsync(SubagentSaveRequest request, CancellationToken cancellationToken = default);
    Task DeleteSubagentAsync(string subagentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProviderCatalogOption>> ListChatProvidersAsync(
        CancellationToken cancellationToken = default);
    Task<ProviderModelCatalogResult> LoadChatModelsAsync(string providerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentToolDescriptor>> ListLocalToolsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListPackageCapabilitiesAsync(CancellationToken cancellationToken = default);
}

#if SUNDER_SUBAGENTS_APP
internal sealed partial class SubagentAppRuntimeGateway :
    ISubagentManagementGateway,
    ISubsessionSessionReader,
    ISubsessionCheckpointReader,
    ISubsessionTranscriptPageReader,
    ISubsessionChangeNotifications,
    ISubagentPresentationInitialization,
    IOwnerStreamAvailability,
    IDisposable,
    IAsyncDisposable
{
    private readonly ISubagentsManagementService _client;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _runtimeSnapshotLock = new();
    private Task<SubagentProjection>? _runtimeSnapshot;
    private Task<SubagentProjection>? _abandonedRuntimeSnapshot;
    private Task? _observationTask;
    private Task? _ownerObservationTask;
    private readonly ILogger<SubagentAppRuntimeGateway>? _logger;
    private readonly OwnerStreamObserver _observer;
    private long _changeRevision;
    private string? _changeRuntimeInstanceId;
    private int _disposed;
    private int _lifetimeDisposed;

    internal SubagentAppRuntimeGateway(ISubagentsManagementService client)
        : this(client, loggerFactory: null)
    {
    }

    internal SubagentAppRuntimeGateway(
        ISubagentsManagementService client,
        ILoggerFactory? loggerFactory)
    {
        _client = client;
        _logger = loggerFactory?.CreateLogger<SubagentAppRuntimeGateway>();
        _observer = new OwnerStreamObserver(LogObservationRetry);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_runtimeSnapshotLock)
        {
            return _observationTask ??= StartObservingChangesAsync();
        }
    }

    private async Task StartObservingChangesAsync()
    {
        await Task.Yield();
        _ownerObservationTask = _observer.RunAsync(ObserveChangesOnceAsync, _lifetime.Token);
    }

    public event Action? SubagentsChanged;
    public event Action? CatalogChanged;
    public event Action<Guid>? SessionChanged;
    public event Action<Guid, AgentTurnRecord, AgentTranscriptActivityGroup?>? TurnChanged;
    public event Action? ResnapshotRequired;
    public event Action<Guid, AgentRunActivityUpdate>? RunActivityChanged;
    public bool IsOwnerStreamAvailable => _observer.IsAvailable;
    public event Action<bool>? OwnerStreamAvailabilityChanged
    {
        add => _observer.AvailabilityChanged += value;
        remove => _observer.AvailabilityChanged -= value;
    }

    internal Task? OwnerObservationTask => _ownerObservationTask;

    public async Task<IReadOnlyList<SubagentRecord>> ListSubagentsAsync(CancellationToken cancellationToken = default)
    {
        var projection = await InvokeAsync(_client.ManagementAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        return projection.Subagents ?? [];
    }
    public async Task<SubagentRecord> CreateSubagentAsync(string displayName, CancellationToken cancellationToken = default)
        => (await InvokeAsync(_client.CreateAsync(displayName, cancellationToken), cancellationToken).ConfigureAwait(false)).Subagent
           ?? throw new InvalidOperationException("Runtime did not return the created subagent.");
    public async Task<SubagentRecord> SaveSubagentAsync(SubagentSaveRequest request, CancellationToken cancellationToken = default)
        => (await InvokeAsync(_client.SaveAsync(request, cancellationToken), cancellationToken).ConfigureAwait(false)).Subagent
           ?? throw new InvalidOperationException("Runtime did not return the saved subagent.");
    public async Task DeleteSubagentAsync(string subagentId, CancellationToken cancellationToken = default)
        => _ = await InvokeAsync(_client.DeleteAsync(subagentId, cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<ProviderCatalogOption>> ListChatProvidersAsync(
        CancellationToken cancellationToken = default)
    {
        // Explicit editor refreshes must query the current catalog even when its
        // asynchronous change notification has not reached the App yet.
        var projection = await InvokeAsync(_client.ManagementAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        return (projection.Providers ?? []).Select(provider =>
            new ProviderCatalogOption(provider.ProviderId, provider.DisplayName, provider.PackageId)).ToArray();
    }

    public async Task<ProviderModelCatalogResult> LoadChatModelsAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var provider = (await InvokeAsync(_client.ProviderModelsAsync(providerId, cancellationToken), cancellationToken))
            .Providers?.FirstOrDefault();
        if (provider is null) return new([], "Chat provider is no longer installed.");
        var readiness = provider.Readiness;
        return new(
            (provider.Models ?? []).OrderNewestFirst().Select(ProviderModelCatalogAdapter.ToCatalogOption).ToArray(),
            readiness is null ? "Chat provider status is unavailable." : $"Chat provider status: {readiness.Status} - {readiness.Message}",
            readiness?.Status == AgentProviderReadinessStatus.Ready ? null : readiness?.Message);
    }

    public async Task<IReadOnlyList<AgentToolDescriptor>> ListLocalToolsAsync(CancellationToken cancellationToken = default)
        => (await InvokeAsync(_client.ManagementAsync(cancellationToken), cancellationToken)).Tools ?? [];
    public async Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListPackageCapabilitiesAsync(CancellationToken cancellationToken = default)
        => (await InvokeAsync(_client.ManagementAsync(cancellationToken), cancellationToken)).Capabilities ?? [];

    public async Task<SubsessionSessionCatalog> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        var projection = await GetRuntimeSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return new SubsessionSessionCatalog(projection.Sessions ?? [], projection.Profiles ?? [], projection.Activities);
    }

    public async Task<IReadOnlyList<AgentRunCheckpointRecord>> ListLatestCheckpointsAsync(
        CancellationToken cancellationToken = default)
        => (await GetRuntimeSnapshotAsync(cancellationToken).ConfigureAwait(false)).Checkpoints ?? [];

    public async Task<SubsessionTranscriptPage> ListRecentTurnsAsync(
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken = default)
        => (await InvokeAsync(_client.RecentTurnsAsync(sessionId, Math.Clamp(limit, 1, 500), cancellationToken), cancellationToken)
            .ConfigureAwait(false)).TranscriptPage
           ?? throw new InvalidOperationException("Runtime did not return the recent subsession transcript page.");

    public async Task<SubsessionTranscriptPage> ListTurnsBeforeAsync(
        Guid sessionId,
        DateTimeOffset beforeCreatedAtUtc,
        Guid beforeTurnId,
        int limit,
        CancellationToken cancellationToken = default)
        => (await InvokeAsync(_client.TurnsBeforeAsync(sessionId, beforeCreatedAtUtc, beforeTurnId, Math.Clamp(limit, 1, 500), cancellationToken), cancellationToken)
            .ConfigureAwait(false)).TranscriptPage
           ?? throw new InvalidOperationException("Runtime did not return the older subsession transcript page.");

    public async Task<SubsessionTranscriptPage> ListTurnsAfterAsync(
        Guid sessionId,
        DateTimeOffset afterCreatedAtUtc,
        Guid afterTurnId,
        int limit,
        CancellationToken cancellationToken = default)
        => (await InvokeAsync(_client.TurnsAfterAsync(sessionId, afterCreatedAtUtc, afterTurnId, Math.Clamp(limit, 1, 500), cancellationToken), cancellationToken)
            .ConfigureAwait(false)).TranscriptPage
           ?? throw new InvalidOperationException("Runtime did not return the newer subsession transcript page.");

    public async Task<SubsessionAroundTurnPage> LoadAroundTurnAsync(
        Guid sessionId,
        Guid turnId,
        DateTimeOffset turnCreatedAtUtc,
        Guid itemId,
        int beforeLimit,
        int afterLimit,
        CancellationToken cancellationToken = default)
    {
        var projection = await InvokeAsync(_client.AroundTurnAsync(sessionId, turnId, turnCreatedAtUtc, itemId,
            Math.Clamp(beforeLimit, 0, 30), Math.Clamp(afterLimit, 0, 30), cancellationToken), cancellationToken).ConfigureAwait(false);
        return projection.AroundTurn
               ?? throw new InvalidOperationException("Runtime did not return the requested transcript anchor.");
    }

    public async Task<AgentTranscriptToolDetailRecord?> LoadToolDetailAsync(
        AgentTranscriptToolDetailRequest request,
        CancellationToken cancellationToken = default)
        => (await InvokeAsync(
                _client.ToolDetailAsync(request, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false)).ToolDetail;

    private async Task<SubagentProjection> GetRuntimeSnapshotAsync(CancellationToken cancellationToken)
    {
        Task<SubagentProjection> snapshot;
        bool retryIfFaulted;
        lock (_runtimeSnapshotLock)
        {
            if (_runtimeSnapshot is { IsFaulted: true } or { IsCanceled: true })
            {
                _ = _runtimeSnapshot.Exception;
                _runtimeSnapshot = null;
                _abandonedRuntimeSnapshot = null;
            }
            snapshot = _runtimeSnapshot ??= ReadRuntimeSnapshotAsync();
            retryIfFaulted = ReferenceEquals(_abandonedRuntimeSnapshot, snapshot);
        }

        try
        {
            var projection = await snapshot.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_runtimeSnapshotLock)
            {
                if (ReferenceEquals(_abandonedRuntimeSnapshot, snapshot))
                {
                    _abandonedRuntimeSnapshot = null;
                }
            }
            return projection;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_runtimeSnapshotLock)
            {
                if (ReferenceEquals(_runtimeSnapshot, snapshot))
                {
                    _abandonedRuntimeSnapshot = snapshot;
                }
            }
            throw;
        }
        catch
        {
            lock (_runtimeSnapshotLock)
            {
                if (ReferenceEquals(_runtimeSnapshot, snapshot))
                {
                    _runtimeSnapshot = null;
                }
                if (ReferenceEquals(_abandonedRuntimeSnapshot, snapshot))
                {
                    _abandonedRuntimeSnapshot = null;
                }
            }
            if (retryIfFaulted)
            {
                return await GetRuntimeSnapshotAsync(cancellationToken).ConfigureAwait(false);
            }
            throw;
        }
    }

    private async Task<SubagentProjection> ReadRuntimeSnapshotAsync()
    {
        var owner = _lifetime.Token;
        try { return await InvokeAsync(_client.RuntimeCatalogAsync(owner), owner).ConfigureAwait(false); }
        catch (Sunder.Sdk.Rpc.SunderRpcException error) when (IsRuntimeSnapshotAvailability(error))
        {
            owner.ThrowIfCancellationRequested();
            // This one shared operation reads a projection; it is not a command
            // retry policy. Publication can temporarily fence the catalog before
            // a retained App's caller replacement commits. Reacquire only this
            // known read, never a mutation, with one bounded recovery window.
            using var recovery = CancellationTokenSource.CreateLinkedTokenSource(owner);
            recovery.CancelAfter(TimeSpan.FromSeconds(3));
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    await Task.Delay(Math.Min(100 << attempt, 1000), recovery.Token).ConfigureAwait(false);
                    return await InvokeAsync(_client.RuntimeCatalogAsync(recovery.Token), recovery.Token).ConfigureAwait(false);
                }
                catch (Sunder.Sdk.Rpc.SunderRpcException next) when (IsRuntimeSnapshotAvailability(next))
                {
                    owner.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) when (recovery.IsCancellationRequested && !owner.IsCancellationRequested)
                {
                    break;
                }
            }
            throw; // Preserve the original actionable failure; no cached success or enlarged deadline.
        }
    }

    private static bool IsRuntimeSnapshotAvailability(Sunder.Sdk.Rpc.SunderRpcException error)
        => error.Error is { Kind: Sunder.Sdk.Rpc.SunderRpcErrorKind.Unavailable,
                Code: "runtime.v1.unavailable" or "rpc.transport.unavailable" or "rpc.app-session.retired" }
            or { Kind: Sunder.Sdk.Rpc.SunderRpcErrorKind.StaleEndpoint, Code: "rpc.caller-retired" };

    private void InvalidateRuntimeSnapshot()
    {
        lock (_runtimeSnapshotLock)
        {
            _runtimeSnapshot = null;
            _abandonedRuntimeSnapshot = null;
        }
    }

    private static async Task<SubagentProjection> InvokeAsync(ValueTask<SunderContent> response, CancellationToken cancellationToken)
        => await SubagentProjectionContent.ReadAsync<SubagentProjection>(response, cancellationToken).ConfigureAwait(false);

    private async Task ObserveChangesOnceAsync(
        Action markAvailable,
        CancellationToken cancellationToken)
    {
        var connected = false;
        await foreach (var content in _client.ChangesAsync(
                           Interlocked.Read(ref _changeRevision),
                           cancellationToken))
        {
            var change = await SubagentProjectionContent.ReadAsync<SubagentChanged>(ValueTask.FromResult(content), cancellationToken).ConfigureAwait(false);
            if (!connected)
            {
                connected = true;
                markAvailable();
            }
            if (change.Kind == SubagentChangeKind.Connected)
            {
                var previousInstanceId = _changeRuntimeInstanceId;
                _changeRuntimeInstanceId = change.RuntimeInstanceId;
                Interlocked.Exchange(ref _changeRevision, change.Revision);
                if (previousInstanceId is not null
                    && !string.Equals(
                        previousInstanceId,
                        change.RuntimeInstanceId,
                        StringComparison.Ordinal))
                {
                    RequireResnapshot();
                }
                continue;
            }
            if (change.Kind == SubagentChangeKind.ResnapshotRequired)
            {
                _changeRuntimeInstanceId = change.RuntimeInstanceId ?? _changeRuntimeInstanceId;
                Interlocked.Exchange(ref _changeRevision, change.Revision);
                RequireResnapshot();
                continue;
            }

            var previousRevision = Interlocked.Read(ref _changeRevision);
            if (change.Revision <= previousRevision)
            {
                continue;
            }
            if (previousRevision > 0 && change.Revision != previousRevision + 1)
            {
                Interlocked.Exchange(ref _changeRevision, change.Revision);
                RequireResnapshot();
                continue;
            }
            try
            {
                await ProcessChangeAsync(change, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!PresentationExceptionPolicy.IsFatal(exception))
            {
                RequireResnapshot();
                throw;
            }
            Interlocked.Exchange(ref _changeRevision, change.Revision);
        }
    }

    private void LogObservationRetry(Exception exception, TimeSpan retryDelay)
        => _logger?.LogWarning(
            "Subagents owner change observation failed and will retry in {RetryDelayMilliseconds} ms ({ExceptionType}).",
            retryDelay.TotalMilliseconds,
            exception.GetType().Name);

    private async Task ProcessChangeAsync(
        SubagentChanged change,
        CancellationToken cancellationToken)
    {
        switch (change.Kind)
        {
            case SubagentChangeKind.Subagents:
                Dispatch(SubagentsChanged);
                break;
            case SubagentChangeKind.Catalog:
                _ = await ListChatProvidersAsync(cancellationToken).ConfigureAwait(false);
                Dispatch(CatalogChanged);
                break;
            case SubagentChangeKind.Session when change.SessionId is { } sessionId:
                InvalidateRuntimeSnapshot();
                Dispatch(SessionChanged, sessionId);
                break;
            case SubagentChangeKind.Turn when change.SessionId is { } turnSessionId
                                                   && change.Turn is { } turn:
                Dispatch(TurnChanged, turnSessionId, turn, change.ActivityGroup);
                break;
            case SubagentChangeKind.Profile:
                InvalidateRuntimeSnapshot();
                break;
            case SubagentChangeKind.Activity when change.SessionId is { } activitySessionId && change.Activity is { } activity:
                InvalidateRuntimeSnapshot();
                Dispatch(RunActivityChanged, activitySessionId, activity);
                break;
            default:
                throw new InvalidDataException("The Subagents Runtime returned a malformed change event.");
        }
    }

    private void RequireResnapshot()
    {
        InvalidateRuntimeSnapshot();
        Dispatch(ResnapshotRequired);
    }

    private static void Dispatch(Action? handlers)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Action handler in handlers.GetInvocationList())
        {
            try
            {
                handler();
            }
            catch (Exception exception) when (!PresentationExceptionPolicy.IsFatal(exception))
            {
                // Change listeners are independent presentation refresh hints.
            }
        }
    }

    private static void Dispatch<T1, T2, T3>(Action<T1, T2, T3>? handlers, T1 first, T2 second, T3 third)
    {
        if (handlers is null) return;
        foreach (Action<T1, T2, T3> handler in handlers.GetInvocationList())
        {
            try { handler(first, second, third); }
            catch (Exception exception) when (!PresentationExceptionPolicy.IsFatal(exception))
            {
                // Presentation listeners do not own the Runtime change stream.
            }
        }
    }

    private static void Dispatch<T>(Action<T>? handlers, T value)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(value);
            }
            catch (Exception exception) when (!PresentationExceptionPolicy.IsFatal(exception))
            {
                // Change listeners are independent presentation refresh hints.
            }
        }
    }

    private static void Dispatch<TFirst, TSecond>(
        Action<TFirst, TSecond>? handlers,
        TFirst first,
        TSecond second)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Action<TFirst, TSecond> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(first, second);
            }
            catch (Exception exception) when (!PresentationExceptionPolicy.IsFatal(exception))
            {
                // Change listeners are independent presentation refresh hints.
            }
        }
    }

    public void Dispose()
    {
        StopObservation();
        DisposeLifetime();
    }

    public async ValueTask DisposeAsync()
    {
        StopObservation();
        try
        {
            if (_observationTask is { } startupTask)
            {
                await startupTask.ConfigureAwait(false);
            }
            if (_ownerObservationTask is { } observationTask)
            {
                await observationTask.ConfigureAwait(false);
            }
        }
        finally
        {
            DisposeLifetime();
        }
    }

    private void StopObservation()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _lifetime.Cancel();
        }
    }

    private void DisposeLifetime()
    {
        if (Interlocked.Exchange(ref _lifetimeDisposed, 1) == 0)
        {
            _lifetime.Dispose();
        }
    }

}
#endif
