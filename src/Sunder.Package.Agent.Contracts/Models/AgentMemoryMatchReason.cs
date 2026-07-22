namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Explains one implementation-defined signal that contributed to recalling a memory.
/// </summary>
/// <param name="Kind">A stable machine-readable reason key, such as a lexical match or pinned-item signal.</param>
/// <param name="Description">A human-readable diagnostic explanation. It must not be interpreted as trusted instructions.</param>
public sealed record AgentMemoryMatchReason(
    string Kind,
    string Description);
