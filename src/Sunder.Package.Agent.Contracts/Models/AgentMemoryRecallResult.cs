namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Contains the bounded, ranked memory entries selected for one recall operation.
/// </summary>
/// <remarks>
/// The result is a point-in-time snapshot and retains the supplied collection without cloning it. Producers must
/// return entries in their intended consumption order, and all parties must treat the collection as immutable.
/// An implementation may return <see langword="null"/> instead of an empty result from its recall API.
/// </remarks>
/// <param name="Entries">The selected memory entries, normally ordered from highest to lowest relevance.</param>
public sealed record AgentMemoryRecallResult(
    IReadOnlyList<AgentMemoryRecallEntry> Entries);
