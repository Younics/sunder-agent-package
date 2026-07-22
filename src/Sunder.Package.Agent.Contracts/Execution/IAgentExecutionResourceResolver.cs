using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Exposes host-owned package resources at paths that can be consumed inside a selected execution target.
/// </summary>
/// <remarks>
/// A resolver is an optional execution-target capability. It owns any mounting, copying, or other target-side materialization it performs;
/// returned descriptors are plain data and require no disposal. Resolution does not transfer ownership of the host files and must not grant
/// report broader access than requested by <see cref="AgentExecutionResourceDescriptor.AccessMode"/>; it should enforce that mode when the
/// backend supports it. Callers must treat the returned collection as the actual exposed subset because a resolver may omit resources it
/// cannot expose safely.
/// </remarks>
public interface IAgentExecutionResourceResolver
{
    /// <summary>
    /// Resolves candidate host resources into target-visible resource roots for one workspace execution context.
    /// </summary>
    /// <param name="context">The workspace, binding, and optional session/profile identities selecting the target environment.</param>
    /// <param name="resources">
    /// Host resource descriptors to consider. Implementations must not mutate this collection and must canonicalize untrusted host and preferred paths.
    /// </param>
    /// <param name="cancellationToken">Signals that resource validation or target-side materialization should be canceled.</param>
    /// <returns>The resources actually exposed to the target, with their canonical target-visible paths.</returns>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    ValueTask<IReadOnlyList<AgentResolvedExecutionResource>> ResolveResourcesAsync(
        AgentExecutionTargetContext context,
        IReadOnlyList<AgentExecutionResourceDescriptor> resources,
        CancellationToken cancellationToken = default);
}
