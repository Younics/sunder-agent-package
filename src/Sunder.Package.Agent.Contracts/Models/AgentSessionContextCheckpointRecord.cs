namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentSessionContextCheckpointRecord(
    Guid ContextCheckpointId,
    Guid SessionId,
    Guid? FirstOmittedTurnId,
    Guid? LastOmittedTurnId,
    int OmittedTurnCount,
    string SummaryText,
    string? DetailsJson,
    DateTimeOffset CreatedAtUtc);
