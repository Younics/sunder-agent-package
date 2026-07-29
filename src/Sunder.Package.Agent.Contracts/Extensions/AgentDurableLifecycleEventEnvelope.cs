using Sunder.Package.Agent.Contracts;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>Contains a bounded source snapshot or a canonical content-erasure receipt for a durable lifecycle transition.</summary>
public sealed record AgentDurableLifecycleEventPayload
{
    /// <summary>Gets whether sensitive source content was erased after its owning session or workspace was deleted.</summary>
    /// <remarks>An observer must acknowledge an erased payload as a no-op before processing later events in the same ordering scope.</remarks>
    public bool ContentErased { get; init; }

    /// <summary>Gets the session/profile context for run-scoped events.</summary>
    public AgentSessionContextRecord? Session { get; init; }

    /// <summary>Gets the run context for run-scoped events.</summary>
    public AgentRunContextRecord? Run { get; init; }

    /// <summary>Gets the originating user message for run-scoped events.</summary>
    public string? UserMessage { get; init; }

    /// <summary>Gets the bounded continuity summary captured with the event.</summary>
    public string? WorkingSummary { get; init; }

    /// <summary>Gets the bounded recent transcript snapshot in transcript order.</summary>
    public IReadOnlyList<AgentTurnRecord> Turns { get; init; } = [];

    /// <summary>Gets the bounded recent live-buffer subset in transcript order.</summary>
    public IReadOnlyList<AgentTurnRecord> RecentLiveBufferTurns { get; init; } = [];

    /// <summary>Gets the turn directly associated with the transition, when one exists.</summary>
    public AgentTurnRecord? TriggerTurn { get; init; }

    /// <summary>Gets the checkpoint committed with the transition, when one exists.</summary>
    public AgentRunCheckpointRecord? Checkpoint { get; init; }

    /// <summary>Gets the primary session identifier for the event.</summary>
    public Guid? SessionId { get; init; }

    /// <summary>Gets the root session identifier used for ordered delivery, when applicable.</summary>
    public Guid? RootSessionId { get; init; }

    /// <summary>Gets the workspace identifier captured before any source deletion.</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>Gets the durable identity of the workspace incarnation captured with the event.</summary>
    public string? WorkspaceIncarnationId { get; init; }

    /// <summary>Gets the exact turn identifiers removed by a transcript rollback.</summary>
    public IReadOnlyList<Guid> DeletedTurnIds { get; init; } = [];

    /// <summary>Gets the exact descendant session identifiers removed by a transcript rollback or session deletion.</summary>
    public IReadOnlyList<Guid> DeletedChildSessionIds { get; init; } = [];

    /// <summary>Gets all session identifiers removed by a session-tree or workspace deletion.</summary>
    public IReadOnlyList<Guid> DeletedSessionIds { get; init; } = [];

    /// <summary>Gets the stable identity shared by chunks of one large deletion, when chunking was required.</summary>
    public string? DeletionBatchId { get; init; }

    /// <summary>Gets the zero-based chunk index, or <see langword="null"/> for an unchunked event or final manifest.</summary>
    public int? DeletionChunkIndex { get; init; }

    /// <summary>Gets the number of data chunks in a large deletion.</summary>
    public int? DeletionChunkCount { get; init; }

    /// <summary>Gets the exact number of deleted turn identifiers represented by the deletion batch.</summary>
    public int DeletedTurnCount { get; init; }

    /// <summary>Gets the exact number of deleted child-session identifiers represented by the deletion batch.</summary>
    public int DeletedChildSessionCount { get; init; }

    /// <summary>Gets the exact number of deleted session identifiers represented by the deletion batch.</summary>
    public int DeletedSessionCount { get; init; }

    /// <summary>Gets the immutable chunk receipts required before a final deletion manifest can be acknowledged.</summary>
    public IReadOnlyList<AgentMemoryConsistencyBarrier> DeletionChunkReceipts { get; init; } = [];
}

/// <summary>Represents one ordered, at-least-once durable Agent lifecycle delivery.</summary>
public sealed record AgentDurableLifecycleEventEnvelope
{
    /// <summary>Gets the deterministic event identifier derived from the source key.</summary>
    public string EventId { get; init; } = string.Empty;

    /// <summary>Gets the SQLite-assigned monotonic outbox sequence.</summary>
    public long Sequence { get; init; }

    /// <summary>Gets the lifecycle transition kind.</summary>
    public AgentLifecycleEventKind Kind { get; init; }

    /// <summary>Gets the stable source identity from which <see cref="EventId"/> was derived.</summary>
    public string SourceKey { get; init; } = string.Empty;

    /// <summary>Gets the per-workspace/root ordering key.</summary>
    public string OrderingKey { get; init; } = string.Empty;

    /// <summary>Gets the lowercase SHA-256 hash of the currently delivered <see cref="PayloadJson"/>.</summary>
    public string PayloadHash { get; init; } = string.Empty;

    /// <summary>
    /// Gets the original payload hash when the source content was replaced by a canonical erasure receipt; otherwise
    /// <see langword="null"/>.
    /// </summary>
    public string? OriginalPayloadHash { get; init; }

    /// <summary>Gets the bounded source JSON or canonical content-erasure receipt stored by the Agent Runtime.</summary>
    public string PayloadJson { get; init; } = string.Empty;

    /// <summary>Gets the UTC time at which the outbox event was committed.</summary>
    public DateTimeOffset OccurredAtUtc { get; init; }

    /// <summary>Gets the parsed source payload or content-erasure receipt.</summary>
    public AgentDurableLifecycleEventPayload Payload { get; init; } = new();
}
