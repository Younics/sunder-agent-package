using System.Threading.Channels;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.HistorySearch;

internal sealed partial class HistorySearchIndexingService :
    IPackageBackgroundService,
    IAgentSessionDataCleaner,
    IAsyncDisposable
{
    private const int MaximumPendingKeys = 4_096;
    private const int EmbeddingBatchSize = 16;
    private static readonly TimeSpan StreamingDebounce = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan InitialTextRebuildRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumTextRebuildRetryDelay = TimeSpan.FromMinutes(1);

    private readonly HistorySearchStore _projection;
    private readonly AgentLocalStore _authoritative;
    private readonly AgentSessionService _sessions;
    private readonly AgentWorkspaceService _workspaces;
    private readonly HistoryEmbeddingProviderCatalog _embeddingProviders;
    private readonly HistorySemanticOperationFence _semanticFence;
    private readonly HistorySearchRuntimeState _state;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _pendingLock = new();
    private readonly Dictionary<Guid, DateTimeOffset> _pendingTurns = [];
    private readonly Dictionary<Guid, DateTimeOffset> _pendingSessions = [];
    private readonly Dictionary<Guid, HistorySourceSession> _knownSessions = [];
    private Channel<bool> _signal = CreateSignalChannel();
    private CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _semanticChangeGate = new(1, 1);
    private Task? _worker;
    private DateTimeOffset _nextReconciliation;
    private DateTimeOffset? _periodicLowerWatermark;
    private DateTimeOffset? _periodicUpperWatermark;
    private DateTimeOffset? _periodicContinuationAt;
    private Guid? _periodicContinuationId;
    private bool _periodicProjectionChanged;
    private bool _startupReconciliationRequired;
    private bool _rebuildRequested;
    private bool _embeddingRebuildRequested;
    private bool _reconcileRequested;
    private bool _fullReconciliationRequested;
    private bool _providerRefreshRequested;
    private DateTimeOffset? _textRebuildRetryAt;
    private int _textRebuildFailureCount;
    private bool _manuallyCleared;
    private bool _started;
    private bool _disposed;

    internal HistorySearchIndexingService(
        HistorySearchStore projection,
        AgentLocalStore authoritative,
        AgentSessionService sessions,
        AgentWorkspaceService workspaces,
        HistoryEmbeddingProviderCatalog embeddingProviders,
        HistorySemanticOperationFence semanticFence,
        HistorySearchRuntimeState state,
        TimeProvider? timeProvider = null)
    {
        _projection = projection;
        _authoritative = authoritative;
        _sessions = sessions;
        _workspaces = workspaces;
        _embeddingProviders = embeddingProviders;
        _semanticFence = semanticFence;
        _state = state;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _nextReconciliation = UtcNow + ReconciliationInterval;

        sessions.TurnMutated += OnTurnMutated;
        sessions.TranscriptReset += OnTranscriptReset;
        sessions.SessionChanged += OnSessionChanged;
        workspaces.WorkspacesChanged += OnWorkspacesChanged;
    }

    public string CleanerId => "agent.history-search";

    internal void RequestRebuild()
    {
        lock (_pendingLock)
        {
            _rebuildRequested = true;
            _textRebuildRetryAt = null;
            _textRebuildFailureCount = 0;
            _manuallyCleared = false;
            _startupReconciliationRequired = false;
        }
        _projection.SetManuallyCleared(false);
        Signal();
    }

    internal void RequestEmbeddingRebuild()
    {
        // Semantic projection work is dormant while History Search is local-only.
    }

    internal void RequestProviderRefresh()
    {
        // Provider discovery is intentionally disabled for local-only History Search.
    }

    internal void ReportSemanticUnavailable(string failureCode, string message)
    {
        try
        {
            _projection.MarkSemanticUnavailable();
            PublishSemanticUnavailable(_projection.GetConfiguration(), failureCode, message);
        }
        catch
        {
            // Provider failure reporting must not replace the original search outcome.
        }
    }

    internal void QueueAuthoritativeTurnReindex(HistoryStoredDocument document)
        => QueueSession(document.SessionId, UtcNow);

    internal async Task<HistoryProjectionConfiguration> ConfigureSemanticAsync(
        bool enabled,
        ResolvedEmbeddingSelection? selection,
        CancellationToken cancellationToken = default)
    {
        // The contract remains for a future semantic-search release, but this release is local-only.
        enabled = false;
        selection = null;
        await _semanticChangeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _semanticFence.SuspendAndDrainAsync().ConfigureAwait(false);
            await _operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var configuration = _projection.ChangeConfiguration(
                    enabled,
                    enabled ? selection?.OwnedProvider.PackageId : null,
                    enabled ? selection?.OwnedProvider.ProviderId : null,
                    enabled ? selection?.ModelId : null,
                    enabled ? selection?.SpaceFingerprint : null);
                lock (_pendingLock)
                {
                    _embeddingRebuildRequested = enabled && !_manuallyCleared;
                }
                _semanticFence.Activate(configuration.Revision);
                _state.PublishProjectionChanged(status => status with
                {
                    Availability = HistorySearchAvailability.Ready,
                    FailureCode = null,
                    FailureMessage = null,
                    ProgressCompleted = 0,
                    ProgressTotal = null,
                });
                return configuration;
            }
            catch
            {
                _semanticFence.Activate(_projection.GetConfiguration().Revision);
                throw;
            }
            finally
            {
                _operationGate.Release();
            }
        }
        finally
        {
            _semanticChangeGate.Release();
        }
    }

    internal async Task ClearDerivedIndexAsync(CancellationToken cancellationToken = default)
    {
        await _semanticChangeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _semanticFence.SuspendAndDrainAsync().ConfigureAwait(false);
            await _operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _state.Publish(status => status with
                {
                    Availability = HistorySearchAvailability.Clearing,
                    ProgressCompleted = 0,
                    ProgressTotal = null,
                });
                var configuration = _projection.ClearDerivedIndex();
                lock (_pendingLock)
                {
                    _pendingTurns.Clear();
                    _pendingSessions.Clear();
                    _rebuildRequested = false;
                    _embeddingRebuildRequested = false;
                    _reconcileRequested = false;
                    _fullReconciliationRequested = false;
                    _startupReconciliationRequired = false;
                    _manuallyCleared = true;
                }
                _periodicLowerWatermark = null;
                _periodicUpperWatermark = null;
                _periodicContinuationAt = null;
                _periodicContinuationId = null;
                _semanticFence.Activate(configuration.Revision);
                _state.PublishProjectionChanged(status => status with
                {
                    Availability = HistorySearchAvailability.Ready,
                    PendingChanges = 0,
                    ProgressCompleted = 0,
                    ProgressTotal = null,
                    FailureCode = null,
                    FailureMessage = null,
                });
            }
            catch
            {
                _semanticFence.Activate(_projection.GetConfiguration().Revision);
                throw;
            }
            finally
            {
                _operationGate.Release();
            }
        }
        finally
        {
            _semanticChangeGate.Release();
        }
    }

    internal async Task ReconcileNowAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReconcileFullActiveGenerationAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    internal async Task<bool> RecoverProjectionAsync(
        Exception exception,
        CancellationToken cancellationToken = default)
    {
        if (exception is OperationCanceledException)
        {
            return false;
        }
        await _semanticChangeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _semanticFence.SuspendAndDrainAsync().ConfigureAwait(false);
            await _operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (!_projection.TryRecover(exception))
                {
                    _semanticFence.Activate(_projection.GetConfiguration().Revision);
                    return false;
                }
                var configuration = _projection.GetConfiguration();
                _semanticFence.Activate(configuration.Revision);
                lock (_pendingLock)
                {
                    _rebuildRequested = true;
                    _embeddingRebuildRequested = false;
                    _startupReconciliationRequired = false;
                    _manuallyCleared = false;
                }
                _state.RefreshProjection();
                Signal();
                return true;
            }
            finally
            {
                _operationGate.Release();
            }
        }
        finally
        {
            _semanticChangeGate.Release();
        }
    }

    private async Task RunAsync(Channel<bool> signal, CancellationToken cancellationToken)
    {
        if (!_projection.IsAvailable)
        {
            return;
        }
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!await WaitForWakeAsync(signal, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
                await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    _authoritative.EnsureRuntimeGenerationCurrent();
                    if (TakeRebuildRequest())
                    {
                        await RebuildTextProjectionAsync(cancellationToken).ConfigureAwait(false);
                    }
                    if (_manuallyCleared)
                    {
                        continue;
                    }
                    var fullReconciliationRequested = TakeFullReconciliationRequest();
                    if (_startupReconciliationRequired || fullReconciliationRequested)
                    {
                        try
                        {
                            await ReconcileFullActiveGenerationAsync(cancellationToken).ConfigureAwait(false);
                            _startupReconciliationRequired = false;
                        }
                        catch
                        {
                            if (fullReconciliationRequested)
                            {
                                RequestFullReconciliation();
                            }
                            throw;
                        }
                    }
                    var hasActiveTextGeneration = _projection.GetSnapshot().ActiveTextGenerationId is not null;
                    if (hasActiveTextGeneration
                        && (TakeReconciliationRequest() || UtcNow >= _nextReconciliation
                            || _periodicUpperWatermark is not null))
                    {
                        await ReconcileChangedSliceAsync(cancellationToken).ConfigureAwait(false);
                    }
                    await ProcessPendingChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _operationGate.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // A concurrent maintenance operation superseded this pass.
            }
            catch (Exception exception)
            {
                PublishIndexingFailure(exception);
            }
        }
    }

    private void OnTurnMutated(AgentTurnMutation mutation)
    {
        var dueAt = mutation.Kind == AgentTurnMutationKind.Complete
            ? UtcNow
            : UtcNow + StreamingDebounce;
        QueueTurn(mutation.TurnId, dueAt);
    }

    private void OnTranscriptReset(Guid sessionId) => QueueSession(sessionId, UtcNow);

    private void OnWorkspacesChanged() => RequestFullReconciliation();

    private void OnSessionChanged(Guid sessionId)
    {
        var current = _authoritative.GetHistorySourceSession(sessionId);
        lock (_pendingLock)
        {
            if (current is not null
                && _knownSessions.TryGetValue(sessionId, out var known)
                && Equals(current, known))
            {
                return;
            }
            if (current is not null) _knownSessions[sessionId] = current;
            else _knownSessions.Remove(sessionId);
        }
        QueueSession(sessionId, UtcNow);
    }

    private void QueueTurn(Guid turnId, DateTimeOffset dueAt)
    {
        lock (_pendingLock)
        {
            ResumeAfterManualClear();
            if (_pendingTurns.Count + _pendingSessions.Count >= MaximumPendingKeys) _fullReconciliationRequested = true;
            else _pendingTurns[turnId] = dueAt;
        }
        PublishPendingCount();
        Signal();
    }

    private void QueueSession(Guid sessionId, DateTimeOffset dueAt)
    {
        lock (_pendingLock)
        {
            ResumeAfterManualClear();
            if (_pendingTurns.Count + _pendingSessions.Count >= MaximumPendingKeys) _fullReconciliationRequested = true;
            else _pendingSessions[sessionId] = dueAt;
        }
        PublishPendingCount();
        Signal();
    }

    private void ResumeAfterManualClear()
    {
        if (!_manuallyCleared)
        {
            return;
        }
        _manuallyCleared = false;
        _rebuildRequested = true;
        try
        {
            _projection.SetManuallyCleared(false);
        }
        catch
        {
            _reconcileRequested = true;
        }
    }

    private int PendingCount()
    {
        lock (_pendingLock)
        {
            return _pendingTurns.Count + _pendingSessions.Count;
        }
    }

    private void PublishPendingCount()
        => _state.Publish(status => status with { PendingChanges = PendingCount() });

    private bool TakeRebuildRequest()
    {
        lock (_pendingLock)
        {
            var retryDue = _textRebuildRetryAt is { } retryAt && retryAt <= UtcNow;
            var value = _rebuildRequested || retryDue;
            _rebuildRequested = false;
            if (retryDue)
            {
                _textRebuildRetryAt = null;
            }
            return value;
        }
    }

    private bool TakeEmbeddingRebuildRequest()
    {
        lock (_pendingLock)
        {
            var value = _embeddingRebuildRequested;
            _embeddingRebuildRequested = false;
            return value;
        }
    }

    private bool TakeReconciliationRequest()
    {
        lock (_pendingLock)
        {
            var value = _reconcileRequested;
            _reconcileRequested = false;
            return value;
        }
    }

    private bool TakeFullReconciliationRequest()
    {
        lock (_pendingLock)
        {
            var value = _fullReconciliationRequested;
            _fullReconciliationRequested = false;
            return value;
        }
    }

    private void RequestFullReconciliation()
    {
        lock (_pendingLock)
        {
            ResumeAfterManualClear();
            _fullReconciliationRequested = true;
        }
        Signal();
    }

    private bool TakeProviderRefreshRequest()
    {
        lock (_pendingLock)
        {
            var value = _providerRefreshRequested;
            _providerRefreshRequested = false;
            return value;
        }
    }

    private void Signal() => _signal.Writer.TryWrite(true);

    private async Task<bool> WaitForWakeAsync(
        Channel<bool> signal,
        CancellationToken cancellationToken)
    {
        var delay = GetNextWakeDelay();
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var signalTask = signal.Reader.WaitToReadAsync(waitCancellation.Token).AsTask();
        var delayTask = Task.Delay(delay, _timeProvider, waitCancellation.Token);
        var completed = await Task.WhenAny(signalTask, delayTask).ConfigureAwait(false);
        var channelOpen = true;
        if (ReferenceEquals(completed, signalTask))
        {
            channelOpen = await signalTask.ConfigureAwait(false);
        }
        await waitCancellation.CancelAsync().ConfigureAwait(false);
        await ConsumeLosingWaitAsync(signalTask, waitCancellation.Token).ConfigureAwait(false);
        await ConsumeLosingWaitAsync(delayTask, waitCancellation.Token).ConfigureAwait(false);
        if (!channelOpen)
        {
            return false;
        }
        while (signal.Reader.TryRead(out _))
        {
        }
        return true;
    }

    internal TimeSpan GetNextWakeDelay()
    {
        var now = UtcNow;
        DateTimeOffset? next = null;
        var hasActiveTextGeneration = _state.Current.TextGeneration is not null;
        lock (_pendingLock)
        {
            if (_rebuildRequested
                || _fullReconciliationRequested
                || hasActiveTextGeneration
                && (_reconcileRequested || _periodicUpperWatermark is not null))
            {
                return TimeSpan.Zero;
            }
            if (hasActiveTextGeneration && _pendingTurns.Count > 0)
            {
                next = _pendingTurns.Values.Min();
            }
            if (hasActiveTextGeneration && _pendingSessions.Count > 0)
            {
                var sessionDue = _pendingSessions.Values.Min();
                next = next is null || sessionDue < next ? sessionDue : next;
            }
            if (_textRebuildRetryAt is { } retryAt)
            {
                next = next is null || retryAt < next ? retryAt : next;
            }
            if (!_manuallyCleared && hasActiveTextGeneration)
            {
                next = next is null || _nextReconciliation < next
                    ? _nextReconciliation
                    : next;
            }
        }
        if (next is null)
        {
            return Timeout.InfiniteTimeSpan;
        }
        return next <= now ? TimeSpan.Zero : next.Value - now;
    }

    private static async Task ConsumeLosingWaitAsync(Task task, CancellationToken cancellationToken)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void PublishIndexingFailure(Exception exception)
    {
        _ = exception;
        try
        {
            var searchable = _projection.TryPinActiveProjection() is not null;
            _state.Publish(status => status with
            {
                Availability = searchable
                    ? HistorySearchAvailability.Ready
                    : HistorySearchAvailability.Unavailable,
                FailureCode = "indexing-failed",
                FailureMessage = searchable
                    ? "History indexing could not complete. The current lexical index remains available."
                    : "History indexing could not complete. Search remains unavailable until a current index is built.",
            });
        }
        catch
        {
            // A failed projection cannot safely publish a refreshed projection-backed status.
        }
    }

    private static Channel<bool> CreateSignalChannel()
        => Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    private void PublishSemanticUnavailable(
        HistoryProjectionConfiguration configuration,
        string failureCode,
        string message)
    {
        _ = configuration;
        _projection.MarkSemanticUnavailable();
        _state.Publish(status => status with
        {
            Availability = HistorySearchAvailability.Ready,
            FailureCode = failureCode,
            FailureMessage = message,
        });
    }

    private static bool GenerationMatches(
        HistoryProjectionGeneration generation,
        HistoryProjectionConfiguration configuration)
        => generation.ConfigurationRevision == configuration.Revision
           && string.Equals(generation.ProviderPackageId, configuration.EmbeddingProviderPackageId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(generation.ProviderId, configuration.EmbeddingProviderId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(generation.ModelId, configuration.EmbeddingModelId, StringComparison.Ordinal)
           && string.Equals(generation.EmbeddingSpaceFingerprint, configuration.EmbeddingSpaceFingerprint, StringComparison.Ordinal);

    private DateTimeOffset UtcNow => _timeProvider.GetUtcNow();
}
