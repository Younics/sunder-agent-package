using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Optionally discovers exact scoped instruction files through an execution target's own filesystem namespace.
/// </summary>
/// <remarks>
/// Implementations must inspect only the configured workspace root-to-target ancestor chains requested by the caller.
/// Discovery is not an authorization grant and must return no scopes for targets outside configured workspace roots,
/// even when the enclosing operation has separate outside-scope approval. Implementations must bound each chain to
/// 64 probes per request, 64 directories per chain, and each document to 12,000 complete characters. A target
/// must fail rather than return a partial ancestor chain or document.
/// </remarks>
public interface IAgentScopedInstructionDiscoveryTarget : IAgentExecutionTarget
{
    /// <summary>Discovers exact <c>AGENTS.md</c> files applicable to the requested target paths.</summary>
    /// <param name="context">The selected workspace and execution binding. Outside-scope authorization is ignored.</param>
    /// <param name="request">The bounded set of target-visible paths to probe.</param>
    /// <param name="cancellationToken">Signals that target probing should be canceled.</param>
    /// <returns>Canonical target-namespace scopes, documents, and fingerprints for this discovery batch.</returns>
    ValueTask<AgentScopedInstructionDiscoveryResult> DiscoverScopedInstructionsAsync(
        AgentExecutionTargetContext context,
        AgentScopedInstructionDiscoveryRequest request,
        CancellationToken cancellationToken = default);
}
