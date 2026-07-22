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
/// Captures an execution target's point-in-time canonical resource classification for permission planning.
/// </summary>
/// <remarks>
/// Resolution is advisory until execution. Targets must re-resolve and revalidate the resource after
/// approval to defend against symlink, mount, and other time-of-check/time-of-use changes.
/// </remarks>
/// <param name="ResourceKind">The stable target-defined resource category, such as <c>file</c> or <c>directory</c>.</param>
/// <param name="DisplayName">A user-facing resource name safe for approval UI and logs.</param>
/// <param name="CanonicalReference">A stable, non-secret reference used to bind approval to the resolved resource.</param>
/// <param name="PermissionBoundaryId">The permission boundary assigned by the execution target.</param>
/// <param name="Exists">Whether the resource existed at resolution time; this does not guarantee its state at execution.</param>
public sealed record AgentResolvedResource(
    string ResourceKind,
    string DisplayName,
    string CanonicalReference,
    string PermissionBoundaryId,
    bool Exists);
