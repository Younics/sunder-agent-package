using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic.Services;

namespace Sunder.Package.Agent.Memory.Semantic.Runtime;

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
    SemanticMemoryMetricsSnapshot? Metrics = null,
    MemoryGraphPage? Graph = null);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record MemoryGraphPage(IReadOnlyList<StoredMemoryRecord> Memories, bool HasMore);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public enum MemoryChangeKind
{
    [System.Text.Json.Serialization.JsonStringEnumMemberName("session")] Session,
    [System.Text.Json.Serialization.JsonStringEnumMemberName("worker")] Worker,
    [System.Text.Json.Serialization.JsonStringEnumMemberName("reset-required")] ResetRequired,
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record MemoryChanged(MemoryChangeKind Kind, Guid? SessionId = null);
