namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentToolSourceContext(
    Guid? SessionId,
    AgentProfileRecord? Profile,
    AgentWorkspaceRecord? Workspace = null,
    AgentWorkspaceBindingRecord? ExecutionBinding = null);
