using Sunder.Package.Agent.Contracts.Contracts;

namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentWorkspaceExecutionResolution(
    AgentWorkspaceRecord Workspace,
    AgentWorkspaceBindingRecord Binding,
    AgentExecutionTargetDescriptor Target,
    AgentExecutionScopeDescriptor Scope,
    IAgentExecutionTarget ExecutionTarget);
