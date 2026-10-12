using Sunder.Package.Agent.Contracts;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>Contains a bounded source snapshot or canonical content-erasure receipt for a durable lifecycle transition.</summary>
/// <remarks>
/// Erased payloads must be acknowledged as a no-op before later events in the same
/// ordering scope. Deletion chunks and their exact receipts are durable data; no
/// invocation capabilities or process-local handles belong in this payload.
/// </remarks>
public sealed record AgentDurableLifecycleEventPayload(
    bool ContentErased = false,
    AgentSessionContextRecord? Session = null,
    AgentRunContextRecord? Run = null,
    string? UserMessage = null,
    string? WorkingSummary = null,
    IReadOnlyList<AgentTurnRecord>? Turns = null,
    IReadOnlyList<AgentTurnRecord>? RecentLiveBufferTurns = null,
    AgentTurnRecord? TriggerTurn = null,
    AgentRunCheckpointRecord? Checkpoint = null,
    Guid? SessionId = null,
    Guid? RootSessionId = null,
    string? WorkspaceId = null,
    string? WorkspaceIncarnationId = null,
    IReadOnlyList<Guid>? DeletedTurnIds = null,
    IReadOnlyList<Guid>? DeletedChildSessionIds = null,
    IReadOnlyList<Guid>? DeletedSessionIds = null,
    string? DeletionBatchId = null,
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")] int? DeletionChunkIndex = null,
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")] int? DeletionChunkCount = null,
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")] int DeletedTurnCount = 0,
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")] int DeletedChildSessionCount = 0,
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")] int DeletedSessionCount = 0,
    IReadOnlyList<AgentMemoryConsistencyBarrier>? DeletionChunkReceipts = null)
{
    public IReadOnlyList<AgentTurnRecord> Turns { get; init; } = Turns ?? [];
    public IReadOnlyList<AgentTurnRecord> RecentLiveBufferTurns { get; init; } = RecentLiveBufferTurns ?? [];
    public IReadOnlyList<Guid> DeletedTurnIds { get; init; } = DeletedTurnIds ?? [];
    public IReadOnlyList<Guid> DeletedChildSessionIds { get; init; } = DeletedChildSessionIds ?? [];
    public IReadOnlyList<Guid> DeletedSessionIds { get; init; } = DeletedSessionIds ?? [];
    public IReadOnlyList<AgentMemoryConsistencyBarrier> DeletionChunkReceipts { get; init; } = DeletionChunkReceipts ?? [];
}

/// <summary>Represents one ordered, at-least-once durable Agent lifecycle delivery.</summary>
/// <remarks>
/// PayloadJson is the exact stored source JSON or erasure receipt. PayloadHash
/// identifies those bytes; OriginalPayloadHash preserves pre-erasure identity.
/// Payload is the parsed value. Their integrity relationship is checked by Agent,
/// not inferred or rewritten by transport serialization.
/// </remarks>
public sealed record AgentDurableLifecycleEventEnvelope(
    string EventId = "",
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")] long Sequence = 0,
    AgentLifecycleEventKind Kind = default,
    string SourceKey = "",
    string OrderingKey = "",
    [property: Sunder.Sdk.Services.SunderValueConstraints(MinLength = 64, MaxLength = 64)] string PayloadHash = "",
    [property: Sunder.Sdk.Services.SunderValueConstraints(MinLength = 64, MaxLength = 64)] string? OriginalPayloadHash = null,
    string PayloadJson = "",
    DateTimeOffset OccurredAtUtc = default,
    AgentDurableLifecycleEventPayload? Payload = null)
{
    public AgentDurableLifecycleEventPayload Payload { get; init; } = Payload ?? new();
}
