using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Exposes an execution target's workspace roots in the target's own path namespace.
/// </summary>
/// <remarks>
/// This optional capability is used for user guidance, prompt construction, and integrations such as Builder. Scope discovery does not grant
/// access and does not replace operation-time canonical and physical path containment checks. Returned values are snapshots and own no files,
/// mounts, containers, or other disposable resources.
/// </remarks>
public interface IAgentExecutionScopeProvider
{
    /// <summary>
    /// Derives the target-visible workspace roots and default working directory for a binding.
    /// </summary>
    /// <param name="context">The workspace and binding from which target configuration and path ordering are derived.</param>
    /// <param name="cancellationToken">Signals that configuration loading or backend scope discovery should be canceled.</param>
    /// <returns>A target-visible scope snapshot. An empty root list means no executable workspace scope is exposed.</returns>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    ValueTask<AgentExecutionScopeDescriptor> GetExecutionScopeAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default);
}
