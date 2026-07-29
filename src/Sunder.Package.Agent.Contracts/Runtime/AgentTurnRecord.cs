namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Represents an immutable persisted snapshot of one transcript turn.
/// </summary>
/// <remarks>
/// The record retains <paramref name="Items"/> without cloning it. Items are interpreted in ascending
/// <see cref="AgentTurnItemRecord.SequenceNumber"/> order and the collection must be treated as immutable. Streaming
/// updates produce newer snapshots rather than mutating an existing record instance.
/// </remarks>
/// <param name="TurnId">The stable identifier retained across all content revisions of this turn.</param>
/// <param name="SessionId">The identifier of the session that owns and persists the turn.</param>
/// <param name="Role">The author or channel represented by the turn.</param>
/// <param name="Kind">The turn's message, tool-call, or tool-result shape.</param>
/// <param name="Items">The persisted content-item snapshots. The supplied collection is retained and must not be mutated.</param>
/// <param name="CreatedAtUtc">The UTC time at which the turn was first persisted.</param>
/// <param name="UpdatedAtUtc">The UTC time of the content revision represented by this snapshot.</param>
public sealed record AgentTurnRecord(
    Guid TurnId,
    Guid SessionId,
    AgentMessageRole Role,
    AgentTurnKind Kind,
    IReadOnlyList<AgentTurnItemRecord> Items,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    /// <summary>
    /// Gets the monotonically increasing, turn-local content revision. Each append, replacement, or streaming
    /// completion advances it; gaps require consumers to reload a full turn snapshot.
    /// </summary>
    public long ContentRevision { get; init; } = 1;

    /// <summary>
    /// Gets whether this persisted snapshot is still open for streamed content. A completion mutation clears this
    /// flag and advances <see cref="ContentRevision"/> even when no text changes.
    /// </summary>
    public bool IsStreaming { get; init; }

    /// <summary>Gets the durable run that owns this turn, when the turn was written by a run.</summary>
    public Guid? RunId { get; init; }

    /// <summary>Gets the owning run revision used to scope provider call identifiers.</summary>
    public long? RunRevision { get; init; }
}
