using Sunder.Package.Agent.Contracts.Contracts;
using System.Text.Json.Serialization;
using Sunder.Package.Agent.Protocol;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Captures a resolved workspace's enabled primary binding, current target-visible scope, and exact target-activation reference.
/// </summary>
/// <remarks>
/// Resolution confirms readiness only at the time it is produced; backend availability may change before a later operation. Records and scope
/// are snapshots. <see cref="ExecutionTargetReference"/> is opaque and can acquire only the exact owner activation used during resolution.
/// </remarks>
/// <param name="Workspace">The authoritative workspace snapshot used during resolution.</param>
/// <param name="Binding">The enabled primary execution binding selected for the workspace.</param>
/// <param name="Target">The descriptor advertised by the resolved execution target.</param>
/// <param name="Scope">The target-visible workspace roots derived for the selected binding.</param>
/// <param name="ExecutionTarget">A compatibility facade for target invocation. New consumers should prefer <see cref="ExecutionTargetReference"/>.</param>
public sealed record AgentWorkspaceExecutionResolution(
    AgentWorkspaceRecord Workspace,
    AgentWorkspaceBindingRecord Binding,
    AgentExecutionTargetDescriptor Target,
    AgentExecutionScopeDescriptor Scope,
    [property: JsonIgnore] IAgentExecutionTarget ExecutionTarget)
{
    /// <summary>Gets an opaque reference that acquires only the exact target-owner activation used during resolution.</summary>
    [JsonIgnore]
    public AgentRpcReference<IAgentExecutionTarget>? ExecutionTargetReference { get; init; }

    /// <summary>Gets the serializable host-stamped handle for the exact execution target activation.</summary>
    public AgentRpcProviderHandle? ExecutionTargetHandle { get; init; }
}
