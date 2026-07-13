namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentToolCallRequest(
    string CallId,
    string ToolId,
    string ArgumentsJson);
