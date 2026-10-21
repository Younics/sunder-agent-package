using Sunder.Generated.Packages.Younics_Ai_Agent_Services;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Captures a resolved workspace's enabled primary binding, current target-visible scope, and exact target-activation reference.
/// </summary>
/// <remarks>
/// Resolution confirms readiness only at the time it is produced; backend availability may change before a later operation. Records and scope
/// are snapshots. <see cref="ExecutionTarget"/> binds only the exact owner activation used during resolution and conveys no permission.
/// </remarks>
/// <param name="Workspace">The authoritative workspace snapshot used during resolution.</param>
/// <param name="Binding">The enabled primary execution binding selected for the workspace.</param>
/// <param name="Target">The descriptor advertised by the resolved execution target.</param>
/// <param name="Scope">The target-visible workspace roots derived for the selected binding.</param>
/// <param name="ExecutionTarget">The typed reference to the exact execution-target activation.</param>
public sealed record AgentWorkspaceExecutionResolution(
    AgentWorkspaceRecord Workspace,
    AgentWorkspaceBindingRecord Binding,
    AgentExecutionTargetDescriptor Target,
    AgentExecutionScopeDescriptor Scope,
    ServiceReference<IExecutionTargetService> ExecutionTarget);
