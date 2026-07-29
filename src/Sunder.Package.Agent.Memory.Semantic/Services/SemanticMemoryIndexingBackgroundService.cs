using System.Threading.Channels;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Memory.Semantic.Services;

public sealed class SemanticMemoryIndexingBackgroundService : IPackageRuntimeGenerationParticipant, IAsyncDisposable
{
    private const int DefaultQueueCapacity = 256;
    private const int MaxRetryCount = 3;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultMonitorInterval = TimeSpan.FromSeconds(30);

    private readonly MemoryLocalStore _store;
    private readonly SemanticModelRuntimeResolver _modelRuntimeResolver;
    private readonly SemanticMemoryRetrievalBackend _retrievalBackend;
    private readonly SemanticMemoryMetricsService _metricsService;
    private readonly int _queueCapacity;
    private readonly TimeSpan _monitorInterval;
    private readonly object _queueSync = new();
    private readonly object _statusSync = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Dictionary<string, SemanticMemoryWorkSlot> _scheduled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _retryNotBefore = new(StringComparer.Ordinal);

    private Channel<string> _requests;
    private CancellationTokenSource? _stoppingCts;
    private Task? _processingTask;
    private Task? _monitoringTask;
    private SemanticMemoryIndexWorkerStatus _status = SemanticMemoryIndexWorkerStatus.Stopped();
    private PackageRuntimeGeneration? _generation;
    private bool _prepared;
    private bool _workersStarted;
    private bool _disposed;

    public SemanticMemoryIndexingBackgroundService(
        MemoryLocalStore store,
        SemanticModelRuntimeResolver modelRuntimeResolver,
        MemorySemanticSettingsService settingsService,
        SemanticMemoryRetrievalBackend retrievalBackend,
        SemanticMemoryMetricsService metricsService,
        int queueCapacity = DefaultQueueCapacity,
        TimeSpan? monitorInterval = null)
    {
        if (queueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        }
        if (monitorInterval is { } configuredInterval && configuredInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(monitorInterval));
        }

        _store = store;
        _modelRuntimeResolver = modelRuntimeResolver;
        ArgumentNullException.ThrowIfNull(settingsService);
        _retrievalBackend = retrievalBackend;
        _metricsService = metricsService;
        _queueCapacity = queueCapacity;
        _monitorInterval = monitorInterval ?? DefaultMonitorInterval;
        _requests = CreateQueue(queueCapacity);
    }

    public event Action? StatusChanged;

    public SemanticMemoryIndexWorkerStatus GetStatus()
    {
        lock (_statusSync)
        {
            return _status;
        }
    }

    public bool QueueMemoryIndex(Guid memoryId, string profileId)
        => memoryId != Guid.Empty
           && !string.IsNullOrWhiteSpace(profileId)
           && Queue(new MemoryIndexWorkItem(memoryId, profileId));

    public bool QueueSessionReindex(Guid sessionId, string profileId)
        => sessionId != Guid.Empty
           && !string.IsNullOrWhiteSpace(profileId)
           && Queue(new SessionReindexWorkItem(sessionId, profileId, IsExplicitFullReindex: true, IsUrgent: true));

    internal bool QueueSessionReconciliation(Guid sessionId, string profileId, bool isUrgent = false)
        => sessionId != Guid.Empty
           && !string.IsNullOrWhiteSpace(profileId)
           && Queue(new SessionReindexWorkItem(sessionId, profileId, IsExplicitFullReindex: false, isUrgent));

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_prepared)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            _prepared = true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task CommitGenerationAsync(
        PackageRuntimeGeneration generation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (generation.ActivationId == Guid.Empty || generation.SessionGeneration < 1)
        {
            throw new ArgumentException(
                "A committed Runtime generation requires a non-empty activation id and positive session generation.",
                nameof(generation));
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_prepared)
            {
                throw new InvalidOperationException(
                    "Semantic memory indexing must be prepared before its Runtime generation is committed.");
            }
            if (_workersStarted)
            {
                if (_generation != generation)
                {
                    throw new InvalidOperationException(
                        "Semantic memory indexing is already committed to another Runtime generation.");
                }
                return;
            }
            if (_generation is not null && _generation != generation)
            {
                throw new InvalidOperationException(
                    "Semantic memory indexing cannot change generation identity after commit begins.");
            }

            _generation = generation;
            _stoppingCts = new CancellationTokenSource();
            UpdateStatus(status => status with { IsRunning = true, LastFailureAtUtc = null, LastFailureMessage = null });
            _processingTask = ProcessQueueAsync(_stoppingCts.Token);
            _monitoringTask = MonitorAsync(_stoppingCts.Token);
            try
            {
                await QueueSessionsNeedingReconciliationAsync(cancellationToken).ConfigureAwait(false);
                _workersStarted = true;
            }
            catch
            {
                await StopWorkersAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_prepared)
            {
                return;
            }

            if (_workersStarted || _processingTask is not null || _monitoringTask is not null)
            {
                await StopWorkersAsync().ConfigureAwait(false);
            }
            else
            {
                ResetQueue();
                UpdateStatus(status => status with { IsRunning = false, PendingItemCount = 0 });
            }
            _prepared = false;
            _workersStarted = false;
            _generation = null;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
        lock (_queueSync)
        {
            _requests.Writer.TryComplete();
            _scheduled.Clear();
            _retryNotBefore.Clear();
        }

        _lifecycleGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task StopWorkersAsync()
    {
        lock (_queueSync)
        {
            _requests.Writer.TryComplete();
        }

        _stoppingCts?.Cancel();
        var tasks = new[] { _processingTask, _monitoringTask }.Where(task => task is not null).Cast<Task>();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        _stoppingCts?.Dispose();
        _stoppingCts = null;
        _processingTask = null;
        _monitoringTask = null;
        _workersStarted = false;
        ResetQueue();
        UpdateStatus(status => status with { IsRunning = false, PendingItemCount = 0 });
    }

    private bool Queue(SemanticMemoryIndexWorkItem item)
    {
        var added = false;
        lock (_queueSync)
        {
            if (_disposed || _requests.Reader.Completion.IsCompleted)
            {
                return false;
            }

            if (_retryNotBefore.TryGetValue(item.WorkKey, out var retryAt))
            {
                if (!item.BypassesRetryCooldown && retryAt > DateTimeOffset.UtcNow)
                {
                    return false;
                }
                _retryNotBefore.Remove(item.WorkKey);
            }

            if (_scheduled.TryGetValue(item.WorkKey, out var existing))
            {
                existing.Item = MergeWorkItems(existing.Item, item);
                existing.RerunRequested |= existing.IsProcessing;
                return true;
            }

            var queued = _requests.Writer.TryWrite(item.WorkKey);
            _scheduled[item.WorkKey] = new SemanticMemoryWorkSlot(item) { IsQueued = queued };
            lock (_statusSync)
            {
                _status = _status with { PendingItemCount = _status.PendingItemCount + 1 };
            }
            added = true;
        }

        if (added)
        {
            StatusChanged?.Invoke();
        }

        return true;
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _requests.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_requests.Reader.TryRead(out var key))
                {
                    await ProcessScheduledAsync(key, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Package shutdown cancels in-flight indexing and leaves active generations intact.
        }
    }

    private async Task ProcessScheduledAsync(string key, CancellationToken cancellationToken)
    {
        while (true)
        {
            SemanticMemoryIndexWorkItem item;
            lock (_queueSync)
            {
                if (!_scheduled.TryGetValue(key, out var slot))
                {
                    return;
                }

                slot.IsProcessing = true;
                slot.IsQueued = false;
                slot.RerunRequested = false;
                item = slot.Item;
            }

            var canceled = false;
            var failed = false;
            try
            {
                await ProcessAsync(item, cancellationToken).ConfigureAwait(false);
                UpdateStatus(status => status with
                {
                    ProcessedItemCount = status.ProcessedItemCount + 1,
                    LastSuccessfulRunAtUtc = DateTimeOffset.UtcNow,
                    LastFailureAtUtc = null,
                    LastFailureMessage = null,
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
            }
            catch (Exception ex)
            {
                failed = true;
                _metricsService.RecordWorkerFailure();
                UpdateStatus(status => status with
                {
                    ProcessedItemCount = status.ProcessedItemCount + 1,
                    LastFailureAtUtc = DateTimeOffset.UtcNow,
                    LastFailureMessage = ex.Message,
                });
            }

            var rerun = false;
            lock (_queueSync)
            {
                if (_scheduled.TryGetValue(key, out var slot))
                {
                    if (failed)
                    {
                        slot.FailureCount++;
                    }
                    else
                    {
                        slot.FailureCount = 0;
                    }

                    var priorityRerunRequested = slot.RerunRequested && slot.Item.BypassesRetryCooldown;
                    rerun = !canceled && (failed
                        ? slot.FailureCount <= MaxRetryCount || priorityRerunRequested
                        : slot.RerunRequested);
                    if (rerun)
                    {
                        slot.IsProcessing = false;
                    }
                    else
                    {
                        _scheduled.Remove(key);
                        if (failed)
                        {
                            _retryNotBefore[key] = DateTimeOffset.UtcNow + RetryCooldown;
                        }
                        else
                        {
                            _retryNotBefore.Remove(key);
                        }
                    }

                    QueuePendingSlotsNoLock();
                }
            }

            if (!rerun)
            {
                UpdateStatus(status => status with { PendingItemCount = Math.Max(0, status.PendingItemCount - 1) });
                if (canceled)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return;
            }

            if (failed)
            {
                SemanticMemoryWorkSlot retrySlot;
                lock (_queueSync)
                {
                    retrySlot = _scheduled[key];
                }

                var retryExponent = Math.Min(retrySlot.FailureCount - 1, MaxRetryCount);
                var delay = TimeSpan.FromMilliseconds(InitialRetryDelay.TotalMilliseconds * Math.Pow(2, retryExponent));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessAsync(SemanticMemoryIndexWorkItem item, CancellationToken cancellationToken)
    {
        switch (item)
        {
            case MemoryIndexWorkItem memoryRequest:
                var memory = _store.GetMemory(memoryRequest.MemoryId);
                if (memory is not null)
                {
                    await _retrievalBackend.IndexMemoryAsync(memory, memoryRequest.ProfileId, cancellationToken).ConfigureAwait(false);
                }
                break;

            case SessionReindexWorkItem sessionRequest:
                var memories = _store.ListMemories(sessionRequest.SessionId, includeInactive: false);
                if (sessionRequest.IsExplicitFullReindex)
                {
                    await _retrievalBackend.ReindexSessionAsync(
                        sessionRequest.SessionId,
                        sessionRequest.ProfileId,
                        memories,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _retrievalBackend.ReconcileSessionAsync(
                        sessionRequest.SessionId,
                        sessionRequest.ProfileId,
                        cancellationToken).ConfigureAwait(false);
                }
                break;
        }
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_monitorInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await QueueSessionsNeedingReconciliationAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _metricsService.RecordWorkerFailure();
                    UpdateStatus(status => status with
                    {
                        LastFailureAtUtc = DateTimeOffset.UtcNow,
                        LastFailureMessage = ex.Message,
                    });
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Package shutdown stops settings monitoring.
        }
    }

    private async Task QueueSessionsNeedingReconciliationAsync(CancellationToken cancellationToken)
    {
        var sessions = _modelRuntimeResolver.InvokeRuntimeCatalog(
            catalog => catalog.ListSessions()
                .Select(session => new SemanticSessionBinding(
                    session.SessionId,
                    catalog.GetSessionModelBinding(session.SessionId, AgentModelCapabilityKinds.Embedding)
                    ?? SemanticModelRuntimeResolver.ResolveLegacyBinding(catalog.GetSessionProfile(session.SessionId))))
                .ToArray(),
            Array.Empty<SemanticSessionBinding>());
        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profileId = session.Binding?.ProfileId;
            if (!string.IsNullOrWhiteSpace(profileId))
            {
                var requirement = await _retrievalBackend.GetReconciliationRequirementAsync(
                    session.SessionId,
                    profileId,
                    cancellationToken).ConfigureAwait(false);
                if (requirement.ShouldQueue)
                {
                    QueueSessionReconciliation(
                        session.SessionId,
                        profileId,
                        isUrgent: requirement.HasRetractions);
                }
            }
        }
    }

    private void ResetQueue()
    {
        lock (_queueSync)
        {
            _scheduled.Clear();
            _requests = CreateQueue(_queueCapacity);
        }
    }

    private void QueuePendingSlotsNoLock()
    {
        foreach (var (key, slot) in _scheduled)
        {
            if (slot.IsProcessing || slot.IsQueued)
            {
                continue;
            }

            if (!_requests.Writer.TryWrite(key))
            {
                return;
            }

            slot.IsQueued = true;
        }
    }

    private void UpdateStatus(Func<SemanticMemoryIndexWorkerStatus, SemanticMemoryIndexWorkerStatus> update)
    {
        lock (_statusSync)
        {
            _status = update(_status);
        }

        StatusChanged?.Invoke();
    }

    private static SemanticMemoryIndexWorkItem MergeWorkItems(
        SemanticMemoryIndexWorkItem existing,
        SemanticMemoryIndexWorkItem incoming)
        => existing is SessionReindexWorkItem existingSession
           && incoming is SessionReindexWorkItem incomingSession
            ? incomingSession with
            {
                IsExplicitFullReindex = existingSession.IsExplicitFullReindex
                                        || incomingSession.IsExplicitFullReindex,
                IsUrgent = existingSession.IsUrgent || incomingSession.IsUrgent,
            }
            : incoming;

    private static Channel<string> CreateQueue(int capacity)
        => Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

}

internal sealed record SemanticSessionBinding(
    Guid SessionId,
    AgentProfileModelBindingRecord? Binding);

internal sealed class SemanticMemoryWorkSlot(SemanticMemoryIndexWorkItem item)
{
    public SemanticMemoryIndexWorkItem Item { get; set; } = item;
    public bool IsProcessing { get; set; }
    public bool IsQueued { get; set; }
    public bool RerunRequested { get; set; }
    public int FailureCount { get; set; }
}

public abstract record SemanticMemoryIndexWorkItem
{
    public abstract string WorkKey { get; }

    public virtual bool BypassesRetryCooldown => false;
}

public sealed record MemoryIndexWorkItem(Guid MemoryId, string ProfileId) : SemanticMemoryIndexWorkItem
{
    public override string WorkKey => $"memory:{MemoryId:N}";
}

public sealed record SessionReindexWorkItem(
    Guid SessionId,
    string ProfileId,
    bool IsExplicitFullReindex = true,
    bool IsUrgent = false) : SemanticMemoryIndexWorkItem
{
    public override string WorkKey => $"session:{SessionId:N}";

    public override bool BypassesRetryCooldown => IsExplicitFullReindex || IsUrgent;
}

public sealed record SemanticMemoryIndexWorkerStatus(
    bool IsRunning,
    int PendingItemCount,
    long ProcessedItemCount,
    DateTimeOffset? LastSuccessfulRunAtUtc,
    DateTimeOffset? LastFailureAtUtc,
    string? LastFailureMessage)
{
    public static SemanticMemoryIndexWorkerStatus Stopped()
        => new(false, 0, 0, null, null, null);
}
