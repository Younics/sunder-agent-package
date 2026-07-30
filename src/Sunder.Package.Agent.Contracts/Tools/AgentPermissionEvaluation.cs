namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>Identifies the policy layer that produced an effective permission decision.</summary>
public enum AgentPermissionDecisionSource
{
    /// <summary>The permission surface's boundary default applied.</summary>
    PackageDefault = 0,

    /// <summary>A persisted action and boundary override applied.</summary>
    ConfiguredOverride = 1,

    /// <summary>A session-scoped approval overlaid an Ask decision.</summary>
    SessionApproval = 2,

    /// <summary>Session Unrestricted Mode overlaid an Ask decision.</summary>
    UnrestrictedMode = 3,

    /// <summary>The action or boundary was unknown and failed closed to Ask.</summary>
    UnknownBoundary = 4,
}

/// <summary>
/// Captures the effective policy decision and its audit explanation for one permission request.
/// </summary>
/// <param name="Decision">The effective action after defaults, persisted overrides, and session-scoped approvals are considered.</param>
/// <param name="Reason">A user-facing and audit-safe explanation of how the decision was reached.</param>
/// <param name="Override">The persisted action/boundary override consulted, or <see langword="null" /> when the default applied.</param>
public sealed record AgentPermissionEvaluation(
    AgentPermissionDecision Decision,
    string Reason,
    AgentPermissionOverride? Override = null)
{
    /// <summary>Gets the policy layer that produced <see cref="Decision"/>.</summary>
    public AgentPermissionDecisionSource Source { get; init; } = Override is null
        ? AgentPermissionDecisionSource.PackageDefault
        : AgentPermissionDecisionSource.ConfiguredOverride;

    /// <summary>Gets the decision before session approval or Unrestricted Mode overlays.</summary>
    public AgentPermissionDecision BaseDecision { get; init; } = Decision;

    /// <summary>Gets the session that supplied the effective overlay, when applicable.</summary>
    public Guid? SourceSessionId { get; init; }
}
