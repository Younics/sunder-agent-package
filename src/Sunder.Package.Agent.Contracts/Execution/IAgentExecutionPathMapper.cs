using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Maps paths from an execution target's namespace back to physical host paths within configured workspace roots.
/// </summary>
/// <remarks>
/// This optional, security-sensitive capability supports host-side consumers of target output. Implementations must normalize relative and
/// absolute target paths, resolve symbolic links or equivalent indirections on the host side, and prevent traversal across configured mount
/// boundaries. Mapping must not open the resource or transfer ownership of it, and a successful mapping is not a permission grant.
/// Implementations may be called concurrently and must not rely on UI-thread affinity.
/// </remarks>
public interface IAgentExecutionPathMapper
{
    /// <summary>
    /// Resolves a target-visible path to its canonical target and host representations.
    /// </summary>
    /// <param name="context">The workspace binding that defines allowed roots and target-to-host mount relationships.</param>
    /// <param name="executionPath">
    /// A path in the target's syntax. Relative paths are resolved from the target's configured default working directory.
    /// </param>
    /// <param name="cancellationToken">Signals that configuration loading or path canonicalization should be canceled.</param>
    /// <returns>A mapping whose containment flag must be checked before the host path is used.</returns>
    /// <exception cref="InvalidOperationException">The path is outside configured roots, cannot be mapped, or resolves across a containment boundary.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    ValueTask<AgentExecutionPathMapping> MapToHostPathAsync(
        AgentExecutionTargetContext context,
        string executionPath,
        CancellationToken cancellationToken = default);
}
