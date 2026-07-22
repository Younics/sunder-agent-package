namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Defines a bounded durable-memory lookup for one turn.
/// </summary>
/// <remarks>
/// The record retains <paramref name="PreferredCategories"/> without cloning it. The built-in semantic-memory
/// implementation treats a non-empty category list as an allow-list for non-pinned candidates, limits output to
/// <paramref name="MaxEntryCount"/>, and treats <paramref name="MaxChars"/> as a content-plus-evidence budget. It may
/// include one over-budget first entry so that a single relevant memory is not silently lost. Non-positive bounds
/// on entry count produce no entries; a non-positive character budget can still admit that first entry. Other memory
/// providers may use the hints differently but must keep results bounded.
/// </remarks>
/// <param name="Intent">The reason for recall, or <see cref="AgentMemoryRecallIntent.None"/> to disable it.</param>
/// <param name="QueryText">The retrieval query. A provider may fall back to the current user message when this is blank.</param>
/// <param name="Reason">An optional diagnostic explanation of why this plan was selected.</param>
/// <param name="PreferredCategories">Optional category hints. Callers must treat the collection as immutable after construction.</param>
/// <param name="MaxEntryCount">The maximum number of entries requested; the built-in default is 6.</param>
/// <param name="MaxChars">The requested aggregate character budget for recalled content and evidence; the built-in default is 2,000.</param>
public sealed record AgentMemoryRecallPlan(
    AgentMemoryRecallIntent Intent,
    string QueryText,
    string? Reason = null,
    IReadOnlyList<string>? PreferredCategories = null,
    int MaxEntryCount = 6,
    int MaxChars = 2000)
{
    /// <summary>
    /// Gets whether the intent requests retrieval. Bounds and query content are evaluated separately by the provider.
    /// </summary>
    public bool ShouldRecall => Intent != AgentMemoryRecallIntent.None;

    /// <summary>
    /// Creates a plan that explicitly suppresses durable-memory retrieval.
    /// </summary>
    /// <param name="reason">An optional diagnostic explanation for suppressing recall.</param>
    /// <returns>A plan with <see cref="AgentMemoryRecallIntent.None"/> and zero entry and character bounds.</returns>
    public static AgentMemoryRecallPlan None(string? reason = null)
        => new(AgentMemoryRecallIntent.None, string.Empty, reason, PreferredCategories: null, MaxEntryCount: 0, MaxChars: 0);
}
