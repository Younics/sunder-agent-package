using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Provides optional session and workspace snapshots for tool discovery and readiness checks.
/// </summary>
/// <remarks>
/// Null values represent context-free catalog discovery. The supplied records are host-owned
/// snapshots for the duration of the call and should not be retained or modified by a source.
/// </remarks>
/// <param name="SessionId">The active session identifier, or <see langword="null" /> during global catalog discovery.</param>
/// <param name="Profile">The selected agent profile, or <see langword="null" /> when no profile is in scope.</param>
/// <param name="Workspace">The selected workspace, or <see langword="null" /> when discovery is not workspace-bound.</param>
/// <param name="ExecutionBinding">The workspace's selected execution-target binding, or <see langword="null" /> when none is available.</param>
public sealed record AgentToolSourceContext(
    Guid? SessionId,
    AgentProfileRecord? Profile,
    AgentWorkspaceRecord? Workspace = null,
    AgentWorkspaceBindingRecord? ExecutionBinding = null)
{
    /// <summary>Gets an opaque reference to the exact execution-target activation selected for this discovery callback.</summary>
    public IPackageExtensionReference<IAgentExecutionTarget>? ExecutionTargetReference { get; init; }
}
