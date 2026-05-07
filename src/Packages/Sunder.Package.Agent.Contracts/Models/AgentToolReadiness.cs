namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentToolReadiness(
    string ToolId,
    AgentToolReadinessStatus Status,
    string Message);
