using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Produces optional, tool-specific transcript presentation without affecting execution semantics.
/// </summary>
/// <remarks>
/// Every request field is persisted or external data and must be treated as untrusted. Resolvers
/// should be deterministic, side-effect free, bounded, and must not reveal secrets. The host isolates
/// resolver failures and falls back to its generic presentation.
/// </remarks>
public interface IAgentToolPresentationResolver
{
    /// <summary>Resolves presentation content for a completed or pending tool call.</summary>
    /// <param name="request">The untrusted call arguments and persisted result fields.</param>
    /// <returns>A partial presentation, or <see langword="null" /> when the resolver does not own the tool.</returns>
    AgentToolPresentation? ResolveToolPresentation(AgentToolPresentationRequest request);
}
