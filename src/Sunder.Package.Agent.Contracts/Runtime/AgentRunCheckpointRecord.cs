namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentRunCheckpointRecord(
    Guid CheckpointId,
    Guid SessionId,
    long RunRevision,
    AgentRunStatus Status,
    string? Summary,
    DateTimeOffset CreatedAtUtc);
