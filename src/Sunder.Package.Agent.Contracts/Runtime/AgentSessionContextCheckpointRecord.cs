namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Captures a durable continuity summary for historical transcript turns omitted from a bounded model prompt.
/// </summary>
/// <remarks>
/// Creating this checkpoint does not delete or replace the underlying turns. The summary and details are derived
/// reference data and must not be promoted to trusted instructions. Checkpoints are append-only snapshots; a later
/// checkpoint can cover a different omitted range.
/// </remarks>
/// <param name="ContextCheckpointId">The unique identifier of this persisted context snapshot.</param>
/// <param name="SessionId">The session whose omitted transcript range was summarized.</param>
/// <param name="FirstOmittedTurnId">The first omitted turn in transcript order, or <see langword="null"/> when no boundary was recorded.</param>
/// <param name="LastOmittedTurnId">The last omitted turn in transcript order, inclusive, or <see langword="null"/> when no boundary was recorded.</param>
/// <param name="OmittedTurnCount">The number of persisted turns represented by the summary.</param>
/// <param name="SummaryText">The bounded, human-readable continuity projection.</param>
/// <param name="DetailsJson">Optional host-defined structured summary details. Consumers must tolerate absent or unrecognized schemas.</param>
/// <param name="CreatedAtUtc">The UTC time at which this checkpoint was persisted.</param>
public sealed record AgentSessionContextCheckpointRecord(
    Guid ContextCheckpointId,
    Guid SessionId,
    Guid? FirstOmittedTurnId,
    Guid? LastOmittedTurnId,
    int OmittedTurnCount,
    string SummaryText,
    string? DetailsJson,
    DateTimeOffset CreatedAtUtc);
