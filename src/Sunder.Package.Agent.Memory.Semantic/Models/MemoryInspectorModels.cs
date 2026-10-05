using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic.Services;

namespace Sunder.Package.Agent.Memory.Semantic.Runtime;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
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

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record MemoryQuery(
    MemoryQueryKind Kind,
    Guid? SessionId = null,
    Guid? MemoryId = null,
    string? SearchText = null,
    bool IncludeInactive = false,
    string? ProfileId = null,
    StoredMemoryRecord? Memory = null,
    SemanticEmbeddingContext? SemanticContext = null);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
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

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record MemoryCommand(
    MemoryCommandKind Kind,
    Guid MemoryId,
    string? Category = null,
    string? Content = null,
    string? Note = null,
    bool IsPinned = false,
    string? ProfileId = null);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
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

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record MemoryChangeSubscription;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public enum MemoryChangeKind
{
    Session,
    Worker,
    ResetRequired,
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record MemoryChanged(MemoryChangeKind Kind, Guid? SessionId = null);
