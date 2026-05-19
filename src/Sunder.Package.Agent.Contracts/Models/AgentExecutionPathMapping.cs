namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentExecutionPathMapping(
    string ExecutionPath,
    string HostPath,
    bool IsInsideAllowedRoot);
