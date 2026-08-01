namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Describes the stable identity and advertised capabilities of a registered execution-target contribution.
/// </summary>
/// <remarks>
/// Capability flags are discovery hints, not readiness or permission grants. Consumers must still resolve the selected workspace binding,
/// check readiness, and apply the relevant permission policy before invoking the target.
/// </remarks>
/// <param name="TargetKind">
/// The stable backend category, such as <c>local</c> or <c>docker</c>. It is suitable for diagnostics and backend classification but does not
/// identify a workspace binding.
/// </param>
/// <param name="TargetId">
/// The stable contribution identity selected by a workspace binding. It must be unique among execution-target contributions visible to the
/// Agent Runtime and must not contain credentials or user-facing text.
/// </param>
/// <param name="DisplayName">The human-readable target name shown in selection and status surfaces.</param>
/// <param name="Description">An optional user-facing explanation of the target, including material isolation or security limitations.</param>
/// <param name="SupportsShell">Whether the target accepts shell command requests when it is ready.</param>
/// <param name="SupportsFiles">Whether the target accepts file read and mutation requests when it is ready.</param>
public sealed record AgentExecutionTargetDescriptor(
    string TargetKind,
    string TargetId,
    string DisplayName,
    string? Description,
    bool SupportsShell,
    bool SupportsFiles)
{
    /// <summary>Gets the exact wire facets advertised by this target activation.</summary>
    public IReadOnlyList<string> Facets { get; init; } = [];

    /// <summary>Tests whether this target explicitly advertises a wire facet.</summary>
    public bool SupportsFacet(string facetId) => Facets.Contains(facetId, StringComparer.Ordinal);
}
