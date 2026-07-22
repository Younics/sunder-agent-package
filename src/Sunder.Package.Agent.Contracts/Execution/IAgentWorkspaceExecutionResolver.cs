using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Provides Runtime-owned access to persisted workspaces and resolves a workspace's primary execution binding to an installed, ready target.
/// </summary>
/// <remarks>
/// Implementations bridge consumers such as Builder to the authoritative Agent Runtime. Returned records are snapshots, and a resolved
/// <see cref="IAgentExecutionTarget"/> remains owned by its extension catalog; callers do not acquire disposal or lifetime responsibility.
/// Calls may arrive from non-UI threads and may overlap, so implementations must coordinate access to mutable workspace state.
/// </remarks>
public interface IAgentWorkspaceExecutionResolver
{
    /// <summary>
    /// Lists the workspaces currently known to the authoritative workspace store.
    /// </summary>
    /// <returns>A read-only snapshot of workspace records. The order is implementation-defined.</returns>
    IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces();

    /// <summary>
    /// Resolves the enabled primary execution binding for a workspace and verifies that its target is currently usable.
    /// </summary>
    /// <param name="workspaceId">The opaque, persisted identity of the workspace to resolve.</param>
    /// <param name="cancellationToken">Signals that target readiness checks or scope discovery should be canceled.</param>
    /// <returns>The workspace, selected binding, target metadata, target-visible scope, and live target contribution.</returns>
    /// <exception cref="InvalidOperationException">
    /// The workspace, enabled primary binding, target contribution, or target-visible workspace scope is unavailable, or the target is not ready.
    /// </exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    ValueTask<AgentWorkspaceExecutionResolution> ResolveAsync(
        string workspaceId,
        CancellationToken cancellationToken = default);
}
