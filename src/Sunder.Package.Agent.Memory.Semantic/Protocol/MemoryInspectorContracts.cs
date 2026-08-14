using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Memory.Semantic.Runtime;

public interface IMemoryInspectorGateway
{
    Task InitializeAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    event Action<Guid>? SessionChanged;
    event Action? SemanticWorkerStatusChanged;
    Task<IReadOnlyList<AgentSessionRecord>> ListSessionsAsync(CancellationToken cancellationToken = default);
    Task<AgentSessionContextCheckpointRecord?> GetSessionContextCheckpointAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<AgentWorkingSummaryRecord?> GetWorkingSummaryAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredMemoryRecord>> ListMemoriesAsync(Guid sessionId, string? searchText = null, bool includeInactive = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredMemoryEvidenceRecord>> ListEvidenceAsync(Guid memoryId, CancellationToken cancellationToken = default);
    Task<StoredMemoryRecord?> GetSupersedingMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredMemoryRecord>> ListSupersededMemoriesAsync(Guid memoryId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredMemoryRecord>> ListCorrectionLineageAsync(Guid memoryId, CancellationToken cancellationToken = default);
    Task<StoredMemoryRecord> UpdateMemoryAsync(Guid memoryId, string category, string content, string? note, CancellationToken cancellationToken = default);
    Task<StoredMemoryRecord> SetPinnedAsync(Guid memoryId, bool isPinned, CancellationToken cancellationToken = default);
    Task<StoredMemoryRecord> ContestMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default);
    Task<StoredMemoryRecord> ForgetMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default);
    Task<StoredMemoryRecord> SupersedeMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default);
    Task<MemoryCorrectionResult> CreateCorrectedMemoryAsync(Guid sourceMemoryId, string category, string content, CancellationToken cancellationToken = default);
    Task<MemorySemanticIndexStatusRecord> GetSemanticIndexStatusAsync(StoredMemoryRecord memory, SemanticEmbeddingContext? context, CancellationToken cancellationToken = default);
    Task<SemanticMemorySessionStateRecord> GetSemanticSessionStateAsync(Guid sessionId, string? profileId = null, CancellationToken cancellationToken = default);
    Task<SemanticMemoryReindexResult> ReindexSessionAsync(Guid sessionId, string? profileId = null, CancellationToken cancellationToken = default);
    Task<SemanticMemoryWorkerStatusRecord> GetSemanticWorkerStatusAsync(CancellationToken cancellationToken = default);
    Task<SemanticMemoryMetricsSnapshot> GetMetricsSnapshotAsync(CancellationToken cancellationToken = default);
}

public interface IMemoryInspectorRpcService
{
    ValueTask<MemoryProjection> QueryAsync(
        SunderRpcInvocationContext context,
        MemoryQuery request,
        CancellationToken cancellationToken = default);

    ValueTask<MemoryProjection> CommandAsync(
        SunderRpcInvocationContext context,
        MemoryCommand request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<MemoryChanged> ChangesAsync(
        SunderRpcInvocationContext context,
        MemoryChangeSubscription request,
        CancellationToken cancellationToken = default);
}

internal sealed class MemoryInspectorOperationException(
    string message,
    Exception? innerException = null) : InvalidOperationException(message, innerException);

public enum MemoryQueryKind
{
    Sessions,
    SessionCheckpoint,
    WorkingSummary,
    Memories,
    Details,
    SemanticState,
    SemanticIndexStatus,
    WorkerStatus,
    Metrics,
}

public sealed record MemoryQuery(
    MemoryQueryKind Kind,
    Guid? SessionId = null,
    Guid? MemoryId = null,
    string? SearchText = null,
    bool IncludeInactive = false,
    string? ProfileId = null,
    StoredMemoryRecord? Memory = null,
    SemanticEmbeddingContext? SemanticContext = null);

public enum MemoryCommandKind
{
    Update,
    Pin,
    Contest,
    Forget,
    Supersede,
    Correct,
    Reindex,
}

public sealed record MemoryCommand(
    MemoryCommandKind Kind,
    Guid MemoryId,
    string? Category = null,
    string? Content = null,
    string? Note = null,
    bool IsPinned = false,
    string? ProfileId = null);

public sealed record MemoryProjection(
    IReadOnlyList<AgentSessionRecord>? Sessions = null,
    AgentSessionContextCheckpointRecord? Checkpoint = null,
    AgentWorkingSummaryRecord? WorkingSummary = null,
    IReadOnlyList<StoredMemoryRecord>? Memories = null,
    IReadOnlyList<StoredMemoryEvidenceRecord>? Evidence = null,
    StoredMemoryRecord? Memory = null,
    StoredMemoryRecord? SupersedingMemory = null,
    IReadOnlyList<StoredMemoryRecord>? SupersededMemories = null,
    IReadOnlyList<StoredMemoryRecord>? CorrectionLineage = null,
    MemoryCorrectionResult? Correction = null,
    MemorySemanticIndexStatusRecord? IndexStatus = null,
    SemanticMemorySessionStateRecord? SemanticState = null,
    SemanticMemoryReindexResult? ReindexResult = null,
    SemanticMemoryWorkerStatusRecord? WorkerStatus = null,
    SemanticMemoryMetricsSnapshot? Metrics = null);

public sealed record MemoryChangeSubscription;

public enum MemoryChangeKind
{
    Session,
    Worker,
}

public sealed record MemoryChanged(MemoryChangeKind Kind, Guid? SessionId = null);
