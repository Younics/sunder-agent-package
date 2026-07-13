namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentWorkingSummaryRecord(
    Guid SessionId,
    string SummaryText,
    DateTimeOffset UpdatedAtUtc);
