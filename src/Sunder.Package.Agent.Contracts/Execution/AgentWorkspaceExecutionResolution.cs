using Sunder.Package.Agent.Contracts.Contracts;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Captures a resolved workspace's enabled primary binding, current target-visible scope, and live execution-target contribution.
/// </summary>
/// <remarks>
/// Resolution confirms readiness only at the time it is produced; backend availability may change before a later operation. Records and scope
/// are snapshots. <paramref name="ExecutionTarget"/> remains a shared, extension-catalog-owned service and must not be disposed by the consumer.
/// </remarks>
/// <param name="Workspace">The authoritative workspace snapshot used during resolution.</param>
/// <param name="Binding">The enabled primary execution binding selected for the workspace.</param>
/// <param name="Target">The descriptor advertised by the resolved execution target.</param>
/// <param name="Scope">The target-visible workspace roots derived for the selected binding.</param>
/// <param name="ExecutionTarget">The live target service to invoke for this binding; ownership remains with the Runtime extension catalog.</param>
public sealed record AgentWorkspaceExecutionResolution(
    AgentWorkspaceRecord Workspace,
    AgentWorkspaceBindingRecord Binding,
    AgentExecutionTargetDescriptor Target,
    AgentExecutionScopeDescriptor Scope,
    IAgentExecutionTarget ExecutionTarget);
