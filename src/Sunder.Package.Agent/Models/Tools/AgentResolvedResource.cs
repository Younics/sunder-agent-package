using System.Text.Json.Serialization;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>Identifies how an execution target established a resource's permission scope.</summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public enum AgentPermissionScopeClassificationBasis
{
    /// <summary>The target did not provide structured scope provenance.</summary>
    [JsonStringEnumMemberName("unspecified")] Unspecified = 0,

    /// <summary>The configured root's opened ancestor identity chain matched the retained resource chain.</summary>
    [JsonStringEnumMemberName("opened-ancestor-identity")] OpenedAncestorIdentity = 1,

    /// <summary>Lexical containment was verified against opened filesystem identities.</summary>
    [JsonStringEnumMemberName("lexical-and-opened-identity")] LexicalAndOpenedIdentity = 2,

    /// <summary>The target verified scope through its own immutable path mapping.</summary>
    [JsonStringEnumMemberName("verified-target-mapping")] VerifiedTargetMapping = 3,

    /// <summary>The target could not securely establish scope.</summary>
    [JsonStringEnumMemberName("unresolved")] Unresolved = 4,

    /// <summary>A multi-resource request used more than one classification basis.</summary>
    [JsonStringEnumMemberName("mixed")] Mixed = 5,

    /// <summary>The target established outside scope because the normalized path had no lexical configured-root match.</summary>
    [JsonStringEnumMemberName("lexical-exclusion")] LexicalExclusion = 6,
}

/// <summary>
/// Captures an execution target's point-in-time resource classification and opaque identity for permission planning.
/// </summary>
/// <remarks>
/// Resolution is advisory until execution. Targets must securely reacquire and compare the resource after approval. A canonical reference is
/// stable claim correlation data, not authority and not necessarily a usable path.
/// </remarks>
/// <param name="ResourceKind">The stable target-defined resource category, such as <c>file</c> or <c>directory</c>.</param>
/// <param name="DisplayName">A user-facing resource name safe for approval UI and logs.</param>
/// <param name="CanonicalReference">A stable target-defined reference used to correlate the durable resource claim.</param>
/// <param name="PermissionBoundaryId">The permission boundary assigned by the execution target.</param>
/// <param name="Exists">Whether the resource existed at resolution time; this does not guarantee its state at execution.</param>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record AgentResolvedResource(
    string ResourceKind,
    string DisplayName,
    string CanonicalReference,
    string PermissionBoundaryId,
    bool Exists)
{
    /// <summary>Gets how the execution target established <see cref="PermissionBoundaryId"/>.</summary>
    public AgentPermissionScopeClassificationBasis ScopeClassificationBasis { get; init; }

    /// <summary>Gets the structured durable claim for this resource, when supplied by the target.</summary>
    public AgentResourceClaim? ResourceClaim { get; init; }

    /// <summary>Gets transient, process-local, single-use authority references for this resource.</summary>
    /// <remarks>These values must not be fingerprinted or persisted.</remarks>
    [JsonIgnore]
    [SunderServiceMember]
    public IReadOnlyList<string> AuthorityReferences { get; init; } = [];

    /// <summary>Gets the canonical identity of the directory entry removed by delete-link semantics.</summary>
    /// <remarks>When absent, <see cref="CanonicalReference"/> applies to both access and deletion.</remarks>
    public string? DeleteCanonicalReference { get; init; }

    /// <summary>Gets the durable claim for delete-link semantics when it differs from <see cref="ResourceClaim"/>.</summary>
    public AgentResourceClaim? DeleteResourceClaim { get; init; }

    /// <summary>Gets transient authority references for delete-link semantics.</summary>
    [JsonIgnore]
    [SunderServiceMember]
    public IReadOnlyList<string> DeleteAuthorityReferences { get; init; } = [];

    /// <summary>Gets the boundary of the directory entry affected by delete-link semantics.</summary>
    /// <remarks>When absent, <see cref="PermissionBoundaryId"/> applies to both access and deletion.</remarks>
    public string? DeletePermissionBoundaryId { get; init; }

    /// <summary>Gets whether the canonical physical target is an exact <c>AGENTS.md</c> instruction document.</summary>
    /// <remarks>Targets set this from the canonical target, not from the caller-provided path or an alias name.</remarks>
    public bool IsScopedInstructionDocument { get; init; }
}
