namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Classifies whether an execution target can currently serve a particular workspace binding.
/// </summary>
/// <remarks>
/// Readiness is a point-in-time operational assessment, not a reservation or permission decision. Value <c>2</c> is currently unassigned;
/// serialized consumers should tolerate unknown values from newer contract versions.
/// </remarks>
public enum AgentExecutionTargetReadinessStatus
{
    /// <summary>
    /// The target's required configuration and backend dependencies are available for the supplied context.
    /// </summary>
    Ready = 0,

    /// <summary>
    /// User-supplied target or workspace configuration is missing or incomplete and can normally be corrected through settings.
    /// </summary>
    NeedsConfiguration = 1,

    /// <summary>
    /// The target is configured but validation or an external backend dependency failed; the accompanying message provides diagnostics.
    /// </summary>
    Failed = 3,
}
