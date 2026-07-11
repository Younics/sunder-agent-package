using System.Threading.Channels;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Memory.Semantic.Services;

public sealed class SemanticMemoryIndexingBackgroundService : IPackageBackgroundService, IAsyncDisposable
{
    private const int DefaultQueueCapacity = 256;

    private readonly MemoryLocalStore _store;
    private readonly SemanticModelRuntimeResolver _modelRuntimeResolver;
    private readonly MemorySemanticSettingsService _settingsService;
    private readonly SemanticMemoryRetrievalBackend _retrievalBackend;
    private readonly SemanticMemoryMetricsService _metricsService;
    private readonly int _queueCapacity;
    private readonly object _queueSync = new();
    private readonly object _statusSync = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Dictionary<string, SemanticMemoryWorkSlot> _scheduled = new(StringComparer.Ordinal);

    private Channel<string> _requests;
    private CancellationTokenSource? _stoppingCts;
    private Task? _processingTask;
    private Task? _monitoringTask;
    private IAgentRuntimeCatalog? _subscribedRuntimeCatalog;
    private string _lastSettingsFingerprint = string.Empty;
    private SemanticMemoryIndexWorkerStatus _status = SemanticMemoryIndexWorkerStatus.Stopped();
    private bool _disposed;

    public SemanticMemoryIndexingBackgroundService(
        MemoryLocalStore store,
        SemanticModelRuntimeResolver modelRuntimeResolver,
        MemorySemanticSettingsService settingsService,
        SemanticMemoryRetrievalBackend retrievalBackend,
        SemanticMemoryMetricsService metricsService,
        int queueCapacity = DefaultQueueCapacity)
    {
        if (queueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        }

        _store = store;
        _modelRuntimeResolver = modelRuntimeResolver;
        _settingsService = settingsService;
        _retrievalBackend = retrievalBackend;
        _metricsService = metricsService;
        _queueCapacity = queueCapacity;
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
           && Queue(new SessionReindexWorkItem(sessionId, profileId));

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_processingTask is not null || _monitoringTask is not null)
            {
                return;
            }

            _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _subscribedRuntimeCatalog = _modelRuntimeResolver.RuntimeCatalog;
            if (_subscribedRuntimeCatalog is not null)
            {
                _subscribedRuntimeCatalog.ProfileChanged += OnProfileChanged;
            }

            _lastSettingsFingerprint = await BuildSettingsFingerprintAsync(cancellationToken);
            UpdateStatus(status => status with { IsRunning = true, LastFailureAtUtc = null, LastFailureMessage = null });
            _processingTask = ProcessQueueAsync(_stoppingCts.Token);
            _monitoringTask = MonitorAsync(_stoppingCts.Token);
            QueueAllEligibleSessions();
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
            if (_processingTask is null && _monitoringTask is null)
            {
                return;
            }

            UnsubscribeRuntimeCatalog();
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
            ResetQueue();
            UpdateStatus(status => status with { IsRunning = false, PendingItemCount = 0 });
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
        UnsubscribeRuntimeCatalog();
        lock (_queueSync)
        {
            _requests.Writer.TryComplete();
            _scheduled.Clear();
        }

        _lifecycleGate.Dispose();
        GC.SuppressFinalize(this);
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

            if (_scheduled.TryGetValue(item.WorkKey, out var existing))
            {
                existing.Item = item;
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
                    rerun = !canceled && slot.RerunRequested;
                    if (rerun)
                    {
                        slot.IsProcessing = false;
                    }
                    else
                    {
                        _scheduled.Remove(key);
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
        }
    }

    private async Task ProcessAsync(SemanticMemoryIndexWorkItem item, CancellationToken cancellationToken)
    {
        switch (item)
        {
            case MemoryIndexWorkItem memoryRequest:
                var memory = _store.GetMemory(memoryRequest.MemoryId);
                if (memory is not null && string.Equals(memory.State, MemoryLocalStore.ActiveState, StringComparison.OrdinalIgnoreCase))
                {
                    await _retrievalBackend.IndexMemoryAsync(memory, memoryRequest.ProfileId, cancellationToken).ConfigureAwait(false);
                }
                break;

            case SessionReindexWorkItem sessionRequest:
                var memories = _store.ListMemories(sessionRequest.SessionId, includeInactive: false);
                await _retrievalBackend.ReindexSessionAsync(
                    sessionRequest.SessionId,
                    sessionRequest.ProfileId,
                    memories,
                    cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var currentFingerprint = await BuildSettingsFingerprintAsync(cancellationToken);
                if (!string.Equals(currentFingerprint, _lastSettingsFingerprint, StringComparison.Ordinal))
                {
                    _lastSettingsFingerprint = currentFingerprint;
                    QueueAllEligibleSessions();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Package shutdown stops settings monitoring.
        }
    }

    private void OnProfileChanged(string profileId)
    {
        foreach (var session in _modelRuntimeResolver.RuntimeCatalog?.ListSessionsForProfile(profileId) ?? [])
        {
            QueueSessionReindex(session.SessionId, profileId);
        }
    }

    private void QueueAllEligibleSessions()
    {
        foreach (var session in _modelRuntimeResolver.RuntimeCatalog?.ListSessions() ?? [])
        {
            var binding = _modelRuntimeResolver.ResolveSessionBinding(session.SessionId);
            if (binding is not null && !string.IsNullOrWhiteSpace(binding.ProviderId) && !string.IsNullOrWhiteSpace(binding.ModelId))
            {
                QueueSessionReindex(session.SessionId, binding.ProfileId);
            }
        }
    }

    private async Task<string> BuildSettingsFingerprintAsync(CancellationToken cancellationToken)
        => string.Join('|',
            await _settingsService.IsSemanticRetrievalEnabledAsync(cancellationToken),
            await _settingsService.GetEmbeddingBatchSizeAsync(cancellationToken),
            await _settingsService.GetMaxCanonicalTextCharsAsync(cancellationToken),
            await _settingsService.GetReindexModeAsync(cancellationToken));

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

    private void UnsubscribeRuntimeCatalog()
    {
        if (_subscribedRuntimeCatalog is not null)
        {
            _subscribedRuntimeCatalog.ProfileChanged -= OnProfileChanged;
            _subscribedRuntimeCatalog = null;
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

    private static Channel<string> CreateQueue(int capacity)
        => Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

}

internal sealed class SemanticMemoryWorkSlot(SemanticMemoryIndexWorkItem item)
{
    public SemanticMemoryIndexWorkItem Item { get; set; } = item;
    public bool IsProcessing { get; set; }
    public bool IsQueued { get; set; }
    public bool RerunRequested { get; set; }
}

public abstract record SemanticMemoryIndexWorkItem
{
    public abstract string WorkKey { get; }
}

public sealed record MemoryIndexWorkItem(Guid MemoryId, string ProfileId) : SemanticMemoryIndexWorkItem
{
    public override string WorkKey => $"memory:{MemoryId:N}";
}

public sealed record SessionReindexWorkItem(Guid SessionId, string ProfileId) : SemanticMemoryIndexWorkItem
{
    public override string WorkKey => $"session:{SessionId:N}";
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
