namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Separates host-authorized system instructions from provenance-labeled, lower-trust reference context.
/// </summary>
/// <remarks>
/// A behavior loop must preserve this trust boundary when constructing provider messages. The record retains the
/// supplied block collection without cloning it. <paramref name="HasSupplementaryContext"/> is an explicit host
/// signal and is not guaranteed to equal a simple non-empty check on <paramref name="SupplementaryContextBlocks"/>.
/// </remarks>
/// <param name="SystemInstructions">Trusted package-controlled instructions authorized for the provider system channel, or <see langword="null"/>. User-authored profile instructions belong in <paramref name="SupplementaryContextBlocks"/>.</param>
/// <param name="HasSupplementaryContext">Whether the host found lower-trust reference context for this request.</param>
/// <param name="SupplementaryContextBlocks">Provenance- and trust-labeled reference blocks that must not be promoted to the system channel.</param>
public sealed record AgentBehaviorInstructionContext(
    string? SystemInstructions,
    bool HasSupplementaryContext,
    IReadOnlyList<AgentPromptContextBlock>? SupplementaryContextBlocks = null);
