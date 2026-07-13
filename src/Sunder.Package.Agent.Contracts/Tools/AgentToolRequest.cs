namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentToolRequest(
    string ToolId,
    string ArgumentsJson);
