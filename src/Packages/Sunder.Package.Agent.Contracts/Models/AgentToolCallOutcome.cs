namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentToolCallOutcome(
    AgentToolCallOutcomeKind Kind,
    AgentRunCheckpointRecord? Checkpoint = null,
    AgentToolResult? Result = null);
