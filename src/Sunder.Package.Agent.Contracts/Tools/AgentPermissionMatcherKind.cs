namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Defines how a session-scoped approval matches a later request with the same action identifier.
/// </summary>
public enum AgentPermissionMatcherKind
{
    /// <summary>Matches the approval pattern to the request's permission boundary identifier.</summary>
    ActionId = 0,

    /// <summary>Matches the approval pattern to the request's concrete tool identifier.</summary>
    ToolId = 1,
}
