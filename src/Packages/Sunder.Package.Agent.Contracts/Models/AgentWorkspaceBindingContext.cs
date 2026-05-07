namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentWorkspaceBindingContext(
    AgentWorkspaceRecord Workspace,
    AgentWorkspaceBindingRecord Binding);
