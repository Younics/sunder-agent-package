namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentExecutionTargetContext(
    Guid? SessionId,
    string? ProfileId,
    AgentWorkspaceRecord Workspace,
    AgentWorkspaceBindingRecord Binding,
    bool AllowOutsideConfiguredScope = false);
