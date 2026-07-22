namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Supplies the optional profile context for selectable-capability discovery.
/// </summary>
/// <remarks>
/// The request is a read-only snapshot owned by the caller. Profile fields can contain user-authored and
/// sensitive data; providers must not mutate, persist, log, or disclose them merely to enumerate capabilities.
/// A request does not grant permissions or prove that referenced assignments remain available.
/// </remarks>
/// <param name="Profile">
/// The profile for which capabilities are being listed, or <see langword="null"/> when the caller requests a
/// general catalog. Providers should return generally discoverable capabilities for a missing profile and must
/// tolerate assignments owned by unavailable packages.
/// </param>
public sealed record AgentProfileSelectableCapabilityRequest(
    AgentProfileRecord? Profile = null);

/// <summary>
/// Describes one capability that can be displayed and optionally assigned to an agent profile.
/// </summary>
/// <remarks>
/// <para>
/// The logical identity is the ordinal case-insensitive tuple (kind, source identifier, capability identifier).
/// During aggregate discovery, descriptors missing kind, capability id, or display name are omitted; duplicate
/// identities use the first descriptor from provider invocation order. Final options are ordered by display name,
/// while presentation groups are ordered by group sort order and group title.
/// </para>
/// <para>
/// This record is a discovery snapshot and its strings are package-supplied display and routing metadata. It does
/// not prove package ownership, readiness, authorization, or trust. Editors and consumers must resolve the current
/// provider and enforce assignments, permissions, and execution scope again at use time. Do not include secrets,
/// credentials, raw user content, or unsafe markup in display fields.
/// </para>
/// </remarks>
/// <param name="Kind">
/// The stable capability category, such as a value from <see cref="AgentProfileSelectableCapabilityKinds"/>.
/// Matching is ordinal case-insensitive.
/// </param>
/// <param name="CapabilityId">
/// The stable opaque identifier within <paramref name="Kind"/> and <paramref name="SourceId"/>. Matching is
/// ordinal case-insensitive and the value is persisted in assignments.
/// </param>
/// <param name="SourceId">
/// The stable provider or source identifier used to disambiguate capabilities, normally
/// <see cref="Contracts.IAgentProfileSelectableCapabilityProvider.ProviderId"/>. A missing value creates an
/// unscoped identity and must not be used as evidence of provenance.
/// </param>
/// <param name="DisplayName">The non-empty, non-sensitive human-readable option label.</param>
/// <param name="Description">
/// Optional explanatory display text, or <see langword="null"/>. It is not interpreted as policy.
/// </param>
/// <param name="StatusText">
/// Optional transient readiness or availability text for display. Consumers must not parse it or use it instead
/// of authoritative readiness and permission checks.
/// </param>
/// <param name="IsSelectable">
/// Whether an editor should allow a new assignment to this descriptor. A value of <see langword="false"/> can
/// preserve visibility for an unavailable or informational capability but is not a security decision.
/// </param>
/// <param name="SourceDisplayName">
/// Optional human-readable source label. It is presentation text and is not verified package provenance.
/// </param>
/// <param name="GroupId">
/// Optional stable presentation-group identity within the capability kind and source. It does not affect the
/// capability's assignment identity.
/// </param>
/// <param name="GroupDisplayName">
/// Optional human-readable group title. When absent, editors can derive a title from source metadata.
/// </param>
/// <param name="GroupDescription">
/// Optional explanatory group text for display. It must not contain secrets or executable markup.
/// </param>
/// <param name="GroupSortOrder">
/// The ascending presentation order for the group; lower values are displayed first and equal values are ordered
/// by group title. The value has no execution or trust semantics.
/// </param>
public sealed record AgentProfileSelectableCapabilityDescriptor(
    string Kind,
    string CapabilityId,
    string? SourceId,
    string DisplayName,
    string? Description,
    string? StatusText = null,
    bool IsSelectable = true,
    string? SourceDisplayName = null,
    string? GroupId = null,
    string? GroupDisplayName = null,
    string? GroupDescription = null,
    int GroupSortOrder = 50);
