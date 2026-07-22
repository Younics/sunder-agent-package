namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Describes the durable records removed by an inclusive transcript rollback.
/// </summary>
/// <remarks>
/// The base store permits a user-message anchor and deletes that turn and all later turns, affected checkpoints and
/// continuity state, plus child-session trees launched by removed tool calls. The supplied identifier collections
/// are retained without cloning and must be treated as immutable. External session-data cleanup occurs after the
/// database transaction; cleanup failures can be reported even though the listed records are already deleted.
/// </remarks>
/// <param name="SessionId">The session whose transcript was rolled back.</param>
/// <param name="AnchorTurnId">The user-message turn at the inclusive rollback boundary.</param>
/// <param name="DeletedTurnIds">The removed turn identifiers in transcript order, including the anchor.</param>
/// <param name="DeletedSessionIds">The removed descendant session identifiers associated with rolled-back tool calls.</param>
public sealed record AgentTranscriptRollbackResult(
    Guid SessionId,
    Guid AnchorTurnId,
    IReadOnlyList<Guid> DeletedTurnIds,
    IReadOnlyList<Guid> DeletedSessionIds);
