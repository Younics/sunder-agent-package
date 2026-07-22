namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Represents a legacy persisted working-summary snapshot for short-term session continuity.
/// </summary>
/// <remarks>
/// Working summaries are derived, untrusted reference text rather than durable semantic memory or privileged
/// instructions. New integrations should prefer <see cref="AgentSessionContextCheckpointRecord"/>.
/// </remarks>
/// <param name="SessionId">The session summarized by this record.</param>
/// <param name="SummaryText">The derived continuity text.</param>
/// <param name="UpdatedAtUtc">The UTC time at which this summary snapshot was last persisted.</param>
public sealed record AgentWorkingSummaryRecord(
    Guid SessionId,
    string SummaryText,
    DateTimeOffset UpdatedAtUtc);
