using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Package.Agent.Memory.Semantic.Runtime;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Memory.Semantic;

public sealed class MemoryInspectorService(
    MemoryLocalStore store,
    SemanticMemoryRetrievalBackend retrievalBackend,
    SemanticMemoryIndexingBackgroundService indexingBackgroundService,
    SemanticModelRuntimeResolver modelRuntimeResolver,
    SemanticMemoryMetricsService metricsService
) : IMemoryInspectorGateway, IDisposable
{
    private readonly MemoryLocalStore _store = store;
    private readonly SemanticMemoryRetrievalBackend _retrievalBackend = retrievalBackend;
    private readonly SemanticMemoryIndexingBackgroundService _indexingBackgroundService =
        indexingBackgroundService;
    private readonly SemanticModelRuntimeResolver _modelRuntimeResolver = modelRuntimeResolver;
    private readonly SemanticMemoryMetricsService _metricsService = metricsService;
    private readonly object _sessionChangedSync = new();
    private Action<Guid>? _sessionChanged;
    private IDisposable? _sessionChangedSubscription;
    private bool _disposed;

    public event Action<Guid>? SessionChanged
    {
        add
        {
            lock (_sessionChangedSync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _sessionChanged += value;
                _sessionChangedSubscription ??= _modelRuntimeResolver.SubscribeToSessionChanges(OnRuntimeSessionChanged);
            }
        }
        remove
        {
            lock (_sessionChangedSync)
            {
                _sessionChanged -= value;
                if (_sessionChanged is null)
                {
                    _sessionChangedSubscription?.Dispose();
                    _sessionChangedSubscription = null;
                }
            }
        }
    }

    public event Action? SemanticWorkerStatusChanged
    {
        add => _indexingBackgroundService.StatusChanged += value;
        remove => _indexingBackgroundService.StatusChanged -= value;
    }

    Task<IReadOnlyList<AgentSessionRecord>> IMemoryInspectorGateway.ListSessionsAsync(
        CancellationToken cancellationToken)
        => FromResult(ListSessions(), cancellationToken);

    Task<AgentSessionContextCheckpointRecord?> IMemoryInspectorGateway.GetSessionContextCheckpointAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
        => FromResult(GetSessionContextCheckpoint(sessionId), cancellationToken);

    Task<AgentWorkingSummaryRecord?> IMemoryInspectorGateway.GetWorkingSummaryAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
        => FromResult(GetWorkingSummary(sessionId), cancellationToken);

    Task<IReadOnlyList<StoredMemoryRecord>> IMemoryInspectorGateway.ListMemoriesAsync(
        Guid sessionId,
        string? searchText,
        bool includeInactive,
        CancellationToken cancellationToken)
        => FromResult(ListMemories(sessionId, searchText, includeInactive), cancellationToken);

    Task<IReadOnlyList<StoredMemoryEvidenceRecord>> IMemoryInspectorGateway.ListEvidenceAsync(
        Guid memoryId,
        CancellationToken cancellationToken)
        => FromResult(ListEvidence(memoryId), cancellationToken);

    Task<StoredMemoryRecord?> IMemoryInspectorGateway.GetSupersedingMemoryAsync(
        Guid memoryId,
        CancellationToken cancellationToken)
        => FromResult(GetSupersedingMemory(memoryId), cancellationToken);

    Task<IReadOnlyList<StoredMemoryRecord>> IMemoryInspectorGateway.ListSupersededMemoriesAsync(
        Guid memoryId,
        CancellationToken cancellationToken)
        => FromResult(ListSupersededMemories(memoryId), cancellationToken);

    Task<IReadOnlyList<StoredMemoryRecord>> IMemoryInspectorGateway.ListCorrectionLineageAsync(
        Guid memoryId,
        CancellationToken cancellationToken)
        => FromResult(ListCorrectionLineage(memoryId), cancellationToken);

    Task<StoredMemoryRecord> IMemoryInspectorGateway.UpdateMemoryAsync(
        Guid memoryId,
        string category,
        string content,
        string? note,
        CancellationToken cancellationToken)
        => FromResult(UpdateMemory(memoryId, category, content, note), cancellationToken);

    Task<StoredMemoryRecord> IMemoryInspectorGateway.SetPinnedAsync(
        Guid memoryId,
        bool isPinned,
        CancellationToken cancellationToken)
        => FromResult(SetPinned(memoryId, isPinned), cancellationToken);

    Task<StoredMemoryRecord> IMemoryInspectorGateway.ContestMemoryAsync(
        Guid memoryId,
        CancellationToken cancellationToken)
        => FromResult(ContestMemory(memoryId), cancellationToken);

    Task<StoredMemoryRecord> IMemoryInspectorGateway.ForgetMemoryAsync(
        Guid memoryId,
        CancellationToken cancellationToken)
        => FromResult(ForgetMemory(memoryId), cancellationToken);

    Task<StoredMemoryRecord> IMemoryInspectorGateway.SupersedeMemoryAsync(
        Guid memoryId,
        CancellationToken cancellationToken)
        => FromResult(SupersedeMemory(memoryId), cancellationToken);

    Task<MemoryCorrectionResult> IMemoryInspectorGateway.CreateCorrectedMemoryAsync(
        Guid sourceMemoryId,
        string category,
        string content,
        CancellationToken cancellationToken)
        => FromResult(CreateCorrectedMemory(sourceMemoryId, category, content), cancellationToken);

    Task<MemorySemanticIndexStatusRecord> IMemoryInspectorGateway.GetSemanticIndexStatusAsync(
        StoredMemoryRecord memory,
        SemanticEmbeddingContext? context,
        CancellationToken cancellationToken)
        => FromResult(GetSemanticIndexStatus(memory, context), cancellationToken);

    Task<SemanticMemoryWorkerStatusRecord> IMemoryInspectorGateway.GetSemanticWorkerStatusAsync(
        CancellationToken cancellationToken)
        => FromResult(GetSemanticWorkerStatus(), cancellationToken);

    Task<SemanticMemoryMetricsSnapshot> IMemoryInspectorGateway.GetMetricsSnapshotAsync(
        CancellationToken cancellationToken)
        => FromResult(GetMetricsSnapshot(), cancellationToken);

    public IReadOnlyList<AgentSessionRecord> ListSessions()
        => _modelRuntimeResolver.InvokeRuntimeCatalog(
            static catalog => catalog.ListSessions().ToArray(),
            Array.Empty<AgentSessionRecord>());

    public AgentSessionContextCheckpointRecord? GetSessionContextCheckpoint(Guid sessionId)
        => _modelRuntimeResolver.InvokeRuntimeCatalog(
            catalog => catalog.GetLatestSessionContextCheckpoint(sessionId),
            fallback: null as AgentSessionContextCheckpointRecord);

    public AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId)
        => _modelRuntimeResolver.InvokeRuntimeCatalog(
            catalog => catalog.GetWorkingSummary(sessionId),
            fallback: null as AgentWorkingSummaryRecord);

    public IReadOnlyList<StoredMemoryRecord> ListMemories(
        Guid sessionId,
        string? searchText = null,
        bool includeInactive = false
    ) => _store.ListMemories(sessionId, searchText, includeInactive);

    public IReadOnlyList<StoredMemoryEvidenceRecord> ListEvidence(Guid memoryId) =>
        _store.ListEvidence(memoryId);

    public StoredMemoryRecord? GetMemory(Guid memoryId) => _store.GetMemory(memoryId);

    public StoredMemoryRecord? GetSupersedingMemory(Guid memoryId) =>
        _store.GetSupersedingMemory(memoryId);

    public IReadOnlyList<StoredMemoryRecord> ListSupersededMemories(Guid memoryId) =>
        _store.ListSupersededMemories(memoryId);

    public IReadOnlyList<StoredMemoryRecord> ListCorrectionLineage(Guid memoryId) =>
        _store.ListCorrectionLineage(memoryId);

    public StoredMemoryRecord UpdateMemory(
        Guid memoryId,
        string category,
        string content,
        string? note
    )
    {
        var memory = _store.UpdateMemory(memoryId, category, content, note);
        QueueMutationReindex(memory);
        return memory;
    }

    public StoredMemoryRecord SetPinned(Guid memoryId, bool isPinned)
    {
        var memory = _store.SetPinned(memoryId, isPinned);
        QueueMutationReindex(memory);
        return memory;
    }

    public StoredMemoryRecord ContestMemory(Guid memoryId)
    {
        var memory = _store.SetContested(memoryId);
        QueueMutationReindex(memory);
        return memory;
    }

    public StoredMemoryRecord ForgetMemory(Guid memoryId)
    {
        var memory = _store.SetState(
            memoryId,
            MemoryLocalStore.ForgottenState,
            "Forgotten in memory inspector."
        );
        QueueMutationReindex(memory);
        return memory;
    }

    public StoredMemoryRecord SupersedeMemory(Guid memoryId)
    {
        var memory = _store.SetState(
            memoryId,
            MemoryLocalStore.SupersededState,
            "Superseded in memory inspector."
        );
        QueueMutationReindex(memory);
        return memory;
    }

    public MemoryCorrectionResult CreateCorrectedMemory(
        Guid sourceMemoryId,
        string category,
        string content
    )
    {
        var result = _store.CreateCorrectedMemory(
            sourceMemoryId,
            category,
            content,
            "Corrected in memory inspector."
        );
        _metricsService.RecordCorrection();
        QueueMutationReindex(result.CorrectedMemory);
        return result;
    }

    public MemorySemanticIndexStatusRecord GetSemanticIndexStatus(
        StoredMemoryRecord memory,
        SemanticEmbeddingContext? context
    )
    {
        if (context is null)
        {
            return new MemorySemanticIndexStatusRecord(
                "Loading",
                "Semantic index status is loading."
            );
        }

        if (!context.IsReady || context.ProviderId is null || context.ModelId is null)
        {
            return new MemorySemanticIndexStatusRecord(context.StatusLabel, context.StatusText);
        }

        var indexState = _retrievalBackend.GetIndexState(
            memory,
            context.ProviderId,
            context.ModelId,
            context.ConfigurationFingerprint
        );
        var label = indexState switch
        {
            SemanticMemoryEntryIndexState.Indexed => "Indexed",
            SemanticMemoryEntryIndexState.Stale => "Stale",
            _ => "Missing",
        };

        return new MemorySemanticIndexStatusRecord(
            label,
            $"Semantic index is {label.ToLowerInvariant()} for {context.ProviderDisplayName} / {context.ModelId}.",
            indexState,
            context.ProviderDisplayName,
            context.ModelId
        );
    }

    public async Task<SemanticMemorySessionStateRecord> GetSemanticSessionStateAsync(
        Guid sessionId,
        string? profileId = null,
        CancellationToken cancellationToken = default
    )
    {
        var context = await _modelRuntimeResolver
            .ResolveForSessionAsync(sessionId, profileId, cancellationToken)
            .ConfigureAwait(false);
        if (!context.IsReady || context.ProviderId is null || context.ModelId is null)
        {
            return new SemanticMemorySessionStateRecord(
                context,
                new SemanticMemoryStatusRecord(context.StatusText, CanReindex: false)
            );
        }

        var indexedCount = _store
            .ListEmbeddings(sessionId, context.ProviderId, context.ModelId)
            .Count;
        return new SemanticMemorySessionStateRecord(
            context,
            new SemanticMemoryStatusRecord(
                $"Semantic retrieval is active via {context.ProviderDisplayName} / {context.ModelId}. Indexed memories: {indexedCount}.",
                CanReindex: true
            )
        );
    }

    public async Task<SemanticMemoryStatusRecord?> GetSemanticStatusAsync(
        Guid sessionId,
        string? profileId = null,
        CancellationToken cancellationToken = default
    ) => (await GetSemanticSessionStateAsync(sessionId, profileId, cancellationToken)).Status;

    public async Task<SemanticMemoryReindexResult> ReindexSessionAsync(
        Guid sessionId,
        string? profileId = null,
        CancellationToken cancellationToken = default
    )
    {
        var sessionProfile = _modelRuntimeResolver.InvokeRuntimeCatalog(
            catalog => catalog.GetSession(sessionId) is null
                ? new SessionProfileResolution(false, null)
                : new SessionProfileResolution(
                    true,
                    string.IsNullOrWhiteSpace(profileId)
                        ? catalog.GetSessionProfile(sessionId)?.ProfileId
                        : catalog.GetProfile(profileId)?.ProfileId),
            new SessionProfileResolution(false, null));
        if (!sessionProfile.SessionExists)
        {
            return new SemanticMemoryReindexResult("Session not found.", IndexedMemoryCount: 0);
        }
        if (sessionProfile.ProfileId is null)
        {
            return new SemanticMemoryReindexResult("Agent not found.", IndexedMemoryCount: 0);
        }

        var activeMemories = _store.ListMemories(sessionId, includeInactive: false);
        var indexedCount = await _retrievalBackend.ReindexSessionAsync(
            sessionId,
            sessionProfile.ProfileId,
            activeMemories,
            cancellationToken
        );
        return indexedCount == 0
            ? new SemanticMemoryReindexResult(
                "No embeddings were indexed. Check semantic settings and the embedding provider configuration on the agent.",
                0
            )
            : new SemanticMemoryReindexResult(
                $"Reindexed {indexedCount} memory item(s) for the current session.",
                indexedCount
            );
    }

    public SemanticMemoryWorkerStatusRecord GetSemanticWorkerStatus()
    {
        var status = _indexingBackgroundService.GetStatus();
        var summary =
            status.LastFailureMessage is not null
                ? $"Worker failed at {status.LastFailureAtUtc:O}: {status.LastFailureMessage}"
            : status.PendingItemCount > 0
                ? $"Worker is processing semantic indexing jobs. Pending items: {status.PendingItemCount}."
            : status.LastSuccessfulRunAtUtc is { } lastSuccessfulRunAtUtc
                ? $"Worker is {(status.IsRunning ? "running" : "stopped")}. Last successful indexing run: {lastSuccessfulRunAtUtc:O}."
            : status.IsRunning ? "Worker is running and waiting for indexing work."
            : "Worker is stopped.";

        return new SemanticMemoryWorkerStatusRecord(
            summary,
            status.IsRunning,
            status.PendingItemCount,
            status.ProcessedItemCount,
            status.LastSuccessfulRunAtUtc,
            status.LastFailureAtUtc,
            status.LastFailureMessage,
            HasFailure: !string.IsNullOrWhiteSpace(status.LastFailureMessage)
        );
    }

    public SemanticMemoryMetricsSnapshot GetMetricsSnapshot() => _metricsService.GetSnapshot();

    public void Dispose()
    {
        lock (_sessionChangedSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _sessionChanged = null;
            _sessionChangedSubscription?.Dispose();
            _sessionChangedSubscription = null;
        }
        GC.SuppressFinalize(this);
    }

    private void QueueMutationReindex(StoredMemoryRecord memory)
    {
        var profileId = _modelRuntimeResolver.InvokeRuntimeCatalog(
            catalog => catalog.GetSessionProfile(memory.SessionId)?.ProfileId,
            fallback: null as string);
        if (profileId is not null)
        {
            _indexingBackgroundService.QueueSessionReconciliation(memory.SessionId, profileId);
        }
    }

    private static Task<T> FromResult<T>(T result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(result);
    }

    private void OnRuntimeSessionChanged(Guid sessionId)
    {
        Action<Guid>? handlers;
        lock (_sessionChangedSync)
        {
            handlers = _sessionChanged;
        }
        handlers?.Invoke(sessionId);
    }

    private sealed record SessionProfileResolution(bool SessionExists, string? ProfileId);

}

public sealed record SemanticMemoryStatusRecord(string StatusText, bool CanReindex);

public sealed record SemanticMemorySessionStateRecord(
    SemanticEmbeddingContext Context,
    SemanticMemoryStatusRecord Status
);

public sealed record SemanticMemoryReindexResult(string Message, int IndexedMemoryCount);

public sealed record SemanticMemoryWorkerStatusRecord(
    string StatusText,
    bool IsRunning,
    int PendingItemCount,
    long ProcessedItemCount,
    DateTimeOffset? LastSuccessfulRunAtUtc,
    DateTimeOffset? LastFailureAtUtc,
    string? LastFailureMessage,
    bool HasFailure
);

public sealed record MemorySemanticIndexStatusRecord(
    string StatusLabel,
    string StatusText,
    SemanticMemoryEntryIndexState? IndexState = null,
    string? ProviderDisplayName = null,
    string? ModelId = null
);
