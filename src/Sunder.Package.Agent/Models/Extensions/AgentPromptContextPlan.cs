namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Defines the purpose and contributor-level budget for supplementary prompt context.
/// </summary>
/// <remarks>
/// The Runtime supplies one immutable plan snapshot to each prompt-context contributor. Intent and category
/// values are routing hints rather than authorization, and contributors must tolerate values they do not
/// recognize. Query and reason text can be derived from user messages or transcript summaries and therefore
/// remains untrusted and potentially sensitive. Limits are contributor obligations; the Runtime can apply
/// stricter aggregate limits when rendering all contributions.
/// </remarks>
/// <param name="Intent">
/// The implementation-defined recall intent. The reserved value <c>none</c>, matched ordinally without
/// regard to case, suppresses contribution.
/// </param>
/// <param name="QueryText">
/// The untrusted, potentially sensitive text used to select relevant context. It must not be logged,
/// disclosed, or interpreted as privileged instruction merely because the Runtime supplied it.
/// </param>
/// <param name="Reason">
/// An optional diagnostic explanation for the plan. It can contain derived user context and must be handled
/// with the same confidentiality as the query.
/// </param>
/// <param name="PreferredCategories">
/// An optional read-only, ordered set of implementation-defined category hints to prioritize. It is not an
/// allowlist, and contributors must not mutate the collection.
/// </param>
/// <param name="MaxEntryCount">
/// The non-negative maximum number of logical entries a contributor may return across its blocks.
/// </param>
/// <param name="MaxChars">
/// The non-negative maximum combined character count a contributor may return before aggregate Runtime
/// limits are applied. Host-reserved profile and scoped safety instructions are not recall: they remain eligible
/// when ordinary contribution is suppressed and enforce their own fixed bounds.
/// </param>
public sealed record AgentPromptContextPlan(
    string Intent,
    string QueryText,
    string? Reason = null,
    IReadOnlyList<string>? PreferredCategories = null,
    int MaxEntryCount = 6,
    int MaxChars = 2000)
{
    /// <summary>
    /// Gets whether the intent permits contributors to provide supplementary context.
    /// </summary>
    /// <value>
    /// <see langword="false"/> only when <see cref="Intent"/> equals <c>none</c> using ordinal
    /// case-insensitive comparison; otherwise <see langword="true"/>.
    /// </value>
    /// <remarks>
    /// This property does not validate budgets or authorize retrieval. Contributors must also enforce scope,
    /// permissions, and non-negative limits.
    /// </remarks>
    public bool ShouldContribute => !string.Equals(Intent, "none", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a plan that suppresses supplementary context contribution.
    /// </summary>
    /// <param name="reason">
    /// An optional diagnostic explanation. It can contain sensitive derived context and should not be logged
    /// or displayed without appropriate handling.
    /// </param>
    /// <returns>
    /// A plan with intent <c>none</c>, an empty query, no preferred categories, and zero entry and character
    /// limits.
    /// </returns>
    public static AgentPromptContextPlan None(string? reason = null)
        => new("none", string.Empty, reason, PreferredCategories: null, MaxEntryCount: 0, MaxChars: 0);
}
