using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Memory.Semantic;

public sealed class MemoryInspectorService(
    IPackageExtensionCatalog extensionCatalog,
    MemoryLocalStore store,
    SemanticMemoryRetrievalBackend retrievalBackend,
    MemorySemanticSettingsService settingsService,
    SemanticMemoryIndexingBackgroundService indexingBackgroundService,
    SemanticEmbeddingContextResolver semanticEmbeddingContextResolver,
    SemanticMemoryMetricsService metricsService
)
{
    private readonly IPackageExtensionCatalog _extensionCatalog = extensionCatalog;
    private readonly MemoryLocalStore _store = store;
    private readonly SemanticMemoryRetrievalBackend _retrievalBackend = retrievalBackend;
    private readonly MemorySemanticSettingsService _settingsService = settingsService;
    private readonly SemanticMemoryIndexingBackgroundService _indexingBackgroundService =
        indexingBackgroundService;
    private readonly SemanticEmbeddingContextResolver _semanticEmbeddingContextResolver =
        semanticEmbeddingContextResolver;
    private readonly SemanticMemoryMetricsService _metricsService = metricsService;

    public event Action<Guid>? SessionChanged
    {
        add
        {
            var catalog = GetRuntimeCatalog();
            if (catalog is not null)
            {
                catalog.SessionChanged += value;
            }
        }
        remove
        {
            var catalog = GetRuntimeCatalog();
            if (catalog is not null)
            {
                catalog.SessionChanged -= value;
            }
        }
    }

    public event Action? SemanticWorkerStatusChanged
    {
        add => _indexingBackgroundService.StatusChanged += value;
        remove => _indexingBackgroundService.StatusChanged -= value;
    }

    public IReadOnlyList<AgentSessionRecord> ListSessions() =>
        GetRuntimeCatalog()?.ListSessions() ?? [];

    public AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId) =>
        GetRuntimeCatalog()?.GetWorkingSummary(sessionId);

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
    ) => _store.UpdateMemory(memoryId, category, content, note);

    public StoredMemoryRecord SetPinned(Guid memoryId, bool isPinned) =>
        _store.SetPinned(memoryId, isPinned);

    public StoredMemoryRecord ContestMemory(Guid memoryId) => _store.SetContested(memoryId);

    public StoredMemoryRecord ForgetMemory(Guid memoryId) =>
        _store.SetState(
            memoryId,
            MemoryLocalStore.ForgottenState,
            "Forgotten in memory inspector."
        );

    public StoredMemoryRecord SupersedeMemory(Guid memoryId) =>
        _store.SetState(
            memoryId,
            MemoryLocalStore.SupersededState,
            "Superseded in memory inspector."
        );

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
            context.ModelId
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
        var context = await _semanticEmbeddingContextResolver
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
        var session = GetRuntimeCatalog()?.GetSession(sessionId);
        if (session is null)
        {
            return new SemanticMemoryReindexResult("Session not found.", IndexedMemoryCount: 0);
        }

        var profile = string.IsNullOrWhiteSpace(profileId)
            ? GetRuntimeCatalog()?.GetSessionProfile(sessionId)
            : GetRuntimeCatalog()?.GetProfile(profileId);
        if (profile is null)
        {
            return new SemanticMemoryReindexResult("Agent not found.", IndexedMemoryCount: 0);
        }

        var activeMemories = _store.ListMemories(sessionId, includeInactive: false);
        var indexedCount = await _retrievalBackend.ReindexSessionAsync(
            sessionId,
            profile.ProfileId,
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

    private IAgentRuntimeCatalog? GetRuntimeCatalog() =>
        _extensionCatalog.GetExtensions(PackageExtensionPoints.RuntimeCatalogs).FirstOrDefault();
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
