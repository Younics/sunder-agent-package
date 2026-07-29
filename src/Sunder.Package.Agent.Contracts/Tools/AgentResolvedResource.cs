using System.Text.Json.Serialization;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Defines conventional resource boundary identifiers shared by permission planners and execution targets.
/// </summary>
public static class AgentPermissionBoundaryIds
{
    /// <summary>The resource resolves within paths explicitly configured for the selected workspace.</summary>
    public const string ConfiguredScope = "configured-scope";

    /// <summary>The resource resolves outside the selected workspace's configured paths and requires explicit authorization.</summary>
    public const string OutsideConfiguredScope = "outside-configured-scope";

    /// <summary>The operation runs inside the explicitly selected execution target rather than against a classified path.</summary>
    public const string SelectedExecutionTarget = "selected-execution-target";

    /// <summary>The planner could not securely classify the resource and policy must fail closed or ask.</summary>
    public const string Unknown = "unknown";
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
public sealed record AgentResolvedResource(
    string ResourceKind,
    string DisplayName,
    string CanonicalReference,
    string PermissionBoundaryId,
    bool Exists)
{
    /// <summary>Gets the structured durable claim for this resource, when supplied by the target.</summary>
    public AgentResourceClaim? ResourceClaim { get; init; }

    /// <summary>Gets transient, process-local, single-use authority references for this resource.</summary>
    /// <remarks>These values must not be fingerprinted or persisted.</remarks>
    [JsonIgnore]
    public IReadOnlyList<string> AuthorityReferences { get; init; } = [];

    /// <summary>Gets the canonical identity of the directory entry removed by delete-link semantics.</summary>
    /// <remarks>When absent, <see cref="CanonicalReference"/> applies to both access and deletion.</remarks>
    public string? DeleteCanonicalReference { get; init; }

    /// <summary>Gets the durable claim for delete-link semantics when it differs from <see cref="ResourceClaim"/>.</summary>
    public AgentResourceClaim? DeleteResourceClaim { get; init; }

    /// <summary>Gets transient authority references for delete-link semantics.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> DeleteAuthorityReferences { get; init; } = [];

    /// <summary>Gets the boundary of the directory entry affected by delete-link semantics.</summary>
    /// <remarks>When absent, <see cref="PermissionBoundaryId"/> applies to both access and deletion.</remarks>
    public string? DeletePermissionBoundaryId { get; init; }

    /// <summary>Gets whether the canonical physical target is an exact <c>AGENTS.md</c> instruction document.</summary>
    /// <remarks>Targets set this from the canonical target, not from the caller-provided path or an alias name.</remarks>
    public bool IsScopedInstructionDocument { get; init; }
}
