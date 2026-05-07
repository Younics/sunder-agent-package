using System.Threading.Channels;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Memory.Semantic.Services;

public sealed class SemanticMemoryIndexingBackgroundService(
    MemoryLocalStore store,
    IPackageExtensionCatalog extensionCatalog,
    MemorySemanticSettingsService settingsService,
    SemanticMemoryRetrievalBackend retrievalBackend,
    SemanticMemoryMetricsService metricsService) : IPackageBackgroundService, IDisposable
{
    private readonly MemoryLocalStore _store = store;
    private readonly IPackageExtensionCatalog _extensionCatalog = extensionCatalog;
    private readonly MemorySemanticSettingsService _settingsService = settingsService;
    private readonly SemanticMemoryRetrievalBackend _retrievalBackend = retrievalBackend;
    private readonly SemanticMemoryMetricsService _metricsService = metricsService;
    private readonly Channel<SemanticMemoryIndexWorkItem> _requests = Channel.CreateUnbounded<SemanticMemoryIndexWorkItem>();

    private CancellationTokenSource? _stoppingCts;
    private Task? _processingTask;
    private Task? _monitoringTask;
    private string _lastSettingsFingerprint = string.Empty;
    private IAgentRuntimeCatalog? _runtimeCatalog;
    private readonly object _statusSync = new();
    private SemanticMemoryIndexWorkerStatus _status = SemanticMemoryIndexWorkerStatus.Stopped();

    public event Action? StatusChanged;

    public SemanticMemoryIndexWorkerStatus GetStatus()
    {
        lock (_statusSync)
        {
            return _status;
        }
    }

    public bool QueueMemoryIndex(Guid memoryId, string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return false;
        }

        if (!_requests.Writer.TryWrite(new MemoryIndexWorkItem(memoryId, profileId)))
        {
            return false;
        }

        UpdateStatus(status => status with { PendingItemCount = status.PendingItemCount + 1 });
        return true;
    }

    public bool QueueSessionReindex(Guid sessionId, string profileId)
    {
        if (sessionId == Guid.Empty || string.IsNullOrWhiteSpace(profileId))
        {
            return false;
        }

        if (!_requests.Writer.TryWrite(new SessionReindexWorkItem(sessionId, profileId)))
        {
            return false;
        }

        UpdateStatus(status => status with { PendingItemCount = status.PendingItemCount + 1 });
        return true;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_processingTask is not null || _monitoringTask is not null)
        {
            return Task.CompletedTask;
        }

        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runtimeCatalog = GetRuntimeCatalog();
        if (_runtimeCatalog is not null)
        {
            _runtimeCatalog.ProfileChanged += OnProfileChanged;
        }

        UpdateStatus(_ => SemanticMemoryIndexWorkerStatus.Running());
        _lastSettingsFingerprint = BuildSettingsFingerprint();
        QueueAllEligibleSessions();
        _processingTask = ProcessQueueAsync(_stoppingCts.Token);
        _monitoringTask = MonitorAsync(_stoppingCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_processingTask is null && _monitoringTask is null)
        {
            return;
        }

        if (_runtimeCatalog is not null)
        {
            _runtimeCatalog.ProfileChanged -= OnProfileChanged;
            _runtimeCatalog = null;
        }

        _requests.Writer.TryComplete();
        _stoppingCts?.Cancel();

        var tasks = new[] { _processingTask, _monitoringTask }
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        _processingTask = null;
        _monitoringTask = null;

        try
        {
            await Task.WhenAll(tasks).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
        finally
        {
            UpdateStatus(_ => SemanticMemoryIndexWorkerStatus.Stopped());
        }
    }

    public void Dispose()
    {
        if (_runtimeCatalog is not null)
        {
            _runtimeCatalog.ProfileChanged -= OnProfileChanged;
            _runtimeCatalog = null;
        }

        _stoppingCts?.Cancel();
        _stoppingCts?.Dispose();
        UpdateStatus(_ => SemanticMemoryIndexWorkerStatus.Stopped());
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _requests.Reader.WaitToReadAsync(cancellationToken))
            {
                while (_requests.Reader.TryRead(out var request))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        switch (request)
                        {
                            case MemoryIndexWorkItem memoryRequest:
                                var memory = _store.GetMemory(memoryRequest.MemoryId);
                                if (memory is not null && !string.Equals(memory.State, MemoryLocalStore.ActiveState, StringComparison.OrdinalIgnoreCase))
                                {
                                    break;
                                }

                                if (memory is not null)
                                {
                                    await _retrievalBackend.IndexMemoryAsync(memory, memoryRequest.ProfileId, cancellationToken);
                                }
                                break;

                            case SessionReindexWorkItem sessionRequest:
                                var memories = _store.ListMemories(sessionRequest.SessionId, includeInactive: false);
                                await _retrievalBackend.ReindexSessionAsync(sessionRequest.SessionId, sessionRequest.ProfileId, memories, cancellationToken);
                                break;
                        }

                        UpdateStatus(status => status with
                        {
                            PendingItemCount = Math.Max(0, status.PendingItemCount - 1),
                            ProcessedItemCount = status.ProcessedItemCount + 1,
                            LastSuccessfulRunAtUtc = DateTimeOffset.UtcNow,
                            LastFailureAtUtc = null,
                            LastFailureMessage = null,
                        });
                    }
                    catch (Exception ex)
                    {
                        _metricsService.RecordWorkerFailure();
                        UpdateStatus(status => status with
                        {
                            PendingItemCount = Math.Max(0, status.PendingItemCount - 1),
                            ProcessedItemCount = status.ProcessedItemCount + 1,
                            LastFailureAtUtc = DateTimeOffset.UtcNow,
                            LastFailureMessage = ex.Message,
                        });
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Package shutdown stops the queue.
        }
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var currentFingerprint = BuildSettingsFingerprint();
                if (!string.Equals(currentFingerprint, _lastSettingsFingerprint, StringComparison.Ordinal))
                {
                    _lastSettingsFingerprint = currentFingerprint;
                    QueueAllEligibleSessions();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Package shutdown stops the monitor.
        }
    }

    private void OnProfileChanged(string profileId)
    {
        foreach (var session in GetRuntimeCatalog()?.ListSessionsForProfile(profileId) ?? [])
        {
            QueueSessionReindex(session.SessionId, profileId);
        }
    }

    private void QueueAllEligibleSessions()
    {
        foreach (var session in GetRuntimeCatalog()?.ListSessions() ?? [])
        {
            var profile = GetRuntimeCatalog()?.GetSessionProfile(session.SessionId);
            var binding = GetRuntimeCatalog()?.GetSessionModelBinding(session.SessionId, AgentModelCapabilityKinds.Embedding)
                          ?? (profile is null ? null : ResolveLegacyEmbeddingBinding(profile.ProfileId));
            if (binding is null || string.IsNullOrWhiteSpace(binding.ProviderId) || string.IsNullOrWhiteSpace(binding.ModelId))
            {
                continue;
            }

            QueueSessionReindex(session.SessionId, binding.ProfileId);
        }
    }

    private string BuildSettingsFingerprint()
        => string.Join('|',
            _settingsService.IsSemanticRetrievalEnabled(),
            _settingsService.GetEmbeddingBatchSize(),
            _settingsService.GetMaxCanonicalTextChars(),
            _settingsService.GetReindexMode());

    private IAgentRuntimeCatalog? GetRuntimeCatalog()
        => _extensionCatalog.GetExtensions(PackageExtensionPoints.RuntimeCatalogs).FirstOrDefault();

    private AgentProfileModelBindingRecord? ResolveLegacyEmbeddingBinding(string profileId)
    {
        var profile = GetRuntimeCatalog()?.GetProfile(profileId);
        return profile is null
               || string.IsNullOrWhiteSpace(profile.EmbeddingProviderId)
               || string.IsNullOrWhiteSpace(profile.EmbeddingModelId)
            ? null
            : new AgentProfileModelBindingRecord(
                profile.ProfileId,
                AgentModelCapabilityKinds.Embedding,
                profile.EmbeddingProviderId,
                profile.EmbeddingModelId,
                SettingsJson: null,
                profile.UpdatedAtUtc);
    }

    private void UpdateStatus(Func<SemanticMemoryIndexWorkerStatus, SemanticMemoryIndexWorkerStatus> update)
    {
        lock (_statusSync)
        {
            _status = update(_status);
        }

        StatusChanged?.Invoke();
    }
}

public abstract record SemanticMemoryIndexWorkItem;

public sealed record MemoryIndexWorkItem(Guid MemoryId, string ProfileId) : SemanticMemoryIndexWorkItem;

public sealed record SessionReindexWorkItem(Guid SessionId, string ProfileId) : SemanticMemoryIndexWorkItem;

public sealed record SemanticMemoryIndexWorkerStatus(
    bool IsRunning,
    int PendingItemCount,
    long ProcessedItemCount,
    DateTimeOffset? LastSuccessfulRunAtUtc,
    DateTimeOffset? LastFailureAtUtc,
    string? LastFailureMessage)
{
    public static SemanticMemoryIndexWorkerStatus Running()
        => new(true, PendingItemCount: 0, ProcessedItemCount: 0, LastSuccessfulRunAtUtc: null, LastFailureAtUtc: null, LastFailureMessage: null);

    public static SemanticMemoryIndexWorkerStatus Stopped()
        => new(false, PendingItemCount: 0, ProcessedItemCount: 0, LastSuccessfulRunAtUtc: null, LastFailureAtUtc: null, LastFailureMessage: null);
}
