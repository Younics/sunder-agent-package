using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

// Regression-only local collaborator; production uses the natural service contract.
/// <summary>
/// Provides Runtime-owned access to persisted workspaces and resolves a workspace's primary execution binding to an installed, ready target.
/// </summary>
/// <remarks>
/// Implementations bridge consumers such as Builder to the authoritative Agent Runtime. Returned records are snapshots and retain only an
/// opaque exact-activation reference. Consumers acquire and dispose a lease for each target callback.
/// Calls may arrive from non-UI threads and may overlap, so implementations must coordinate access to mutable workspace state.
/// </remarks>
public interface IAgentWorkspaceExecutionResolver
{
    /// <summary>
    /// Lists the workspaces currently known to the authoritative workspace store.
    /// </summary>
    /// <param name="cancellationToken">Signals that the workspace query should be canceled.</param>
    /// <returns>A read-only snapshot of workspace records. The order is implementation-defined.</returns>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    ValueTask<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the enabled primary execution binding for a workspace and verifies that its target is currently usable.
    /// </summary>
    /// <param name="workspaceId">The opaque, persisted identity of the workspace to resolve.</param>
    /// <param name="cancellationToken">Signals that target readiness checks or scope discovery should be canceled.</param>
    /// <returns>The workspace, selected binding, target metadata, target-visible scope, and exact target-activation reference.</returns>
    /// <exception cref="InvalidOperationException">
    /// The workspace, enabled primary binding, target contribution, or target-visible workspace scope is unavailable, or the target is not ready.
    /// </exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    ValueTask<AgentWorkspaceExecutionResolution> ResolveAsync(
        string workspaceId,
        CancellationToken cancellationToken = default);
}
