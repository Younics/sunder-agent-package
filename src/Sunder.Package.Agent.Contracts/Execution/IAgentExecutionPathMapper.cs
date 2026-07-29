using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Produces advisory mappings from an execution target's namespace to host paths within configured workspace roots.
/// </summary>
/// <remarks>
/// This optional capability supports host-side display and separately authorized process consumers. Mapping does not transfer filesystem
/// authority, retain a resource, or make later host path APIs race-safe. A successful mapping is not a permission grant and must not be used as
/// the basis for a structured host-side mutation. Implementations may be called concurrently and must not rely on UI-thread affinity.
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
