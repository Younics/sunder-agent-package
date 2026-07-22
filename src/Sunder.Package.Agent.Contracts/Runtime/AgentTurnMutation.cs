namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Identifies how an ordered turn update changes a consumer's current projection.
/// </summary>
public enum AgentTurnMutationKind
{
    /// <summary>Seeds or refreshes a turn from the full snapshot carried in <see cref="AgentTurnMutation.Turn"/>.</summary>
    Add,

    /// <summary>Appends <see cref="AgentTurnMutation.Text"/> after verifying the prior revision and base content length.</summary>
    Append,

    /// <summary>Replaces the projection from the full snapshot carried in <see cref="AgentTurnMutation.Turn"/>.</summary>
    Replace,

    /// <summary>Closes a streamed turn without changing its text, while still advancing its content revision.</summary>
    Complete,
}

/// <summary>
/// Describes an ordered mutation committed to a projected transcript turn.
/// </summary>
/// <remarks>
/// Incremental consumers should apply a mutation only when its content revision immediately follows the cached turn
/// and its base length matches. A gap, mismatch, unknown turn, or runtime revision gap requires reloading a full
/// snapshot. The three revisions are independent: content revision orders one turn, runtime revision orders the
/// runtime change feed, and run revision identifies an execution generation.
/// </remarks>
/// <param name="SessionId">The identifier of the session that owns the affected turn.</param>
/// <param name="TurnId">The stable identifier of the affected turn.</param>
/// <param name="ContentRevision">The resulting turn-local revision after this mutation committed.</param>
/// <param name="Kind">The operation needed to reach the resulting projection.</param>
/// <param name="BaseContentLength">The expected text length before an append or completion; consumers use it to detect divergence.</param>
/// <param name="Text">The appended suffix or replacement text when applicable; <see langword="null"/> for add and completion events.</param>
/// <param name="UpdatedAtUtc">The UTC persistence timestamp of the resulting turn revision.</param>
/// <param name="Turn">The complete resulting snapshot for add and replacement operations, or <see langword="null"/> for compact incremental mutations.</param>
/// <param name="RuntimeRevision">The monotonically increasing runtime change-feed position, or 0 when the mutation was raised without a transport ordering stamp.</param>
public sealed record AgentTurnMutation(
    Guid SessionId,
    Guid TurnId,
    long ContentRevision,
    AgentTurnMutationKind Kind,
    int BaseContentLength,
    string? Text,
    DateTimeOffset UpdatedAtUtc,
    AgentTurnRecord? Turn = null,
    long RuntimeRevision = 0);
