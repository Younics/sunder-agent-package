namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Contains the supplementary reference blocks produced by one context contributor.
/// </summary>
/// <remarks>
/// This is a per-call snapshot, not an ownership transfer. The Runtime can filter, reorder, truncate, or
/// omit blocks to satisfy prompt limits and does not deduplicate them. Producers must not mutate the list
/// or its blocks after return. Every block remains user-role reference data and must carry accurate trust
/// and provenance labels; the collection itself conveys no additional trust or authorization.
/// </remarks>
/// <param name="Blocks">
/// A read-only list containing zero or more provenance-labeled blocks. Use an empty list, or return
/// <see langword="null"/> from the contributor, when no context applies.
/// </param>
public sealed record AgentPromptContextContribution(
    IReadOnlyList<AgentPromptContextBlock> Blocks);
