namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Defines the policy action taken for a classified tool invocation.
/// </summary>
public enum AgentPermissionDecision
{
    /// <summary>The invocation may proceed without an interactive prompt.</summary>
    Allow = 0,

    /// <summary>The run is suspended until a user explicitly approves or denies the invocation.</summary>
    Ask = 1,

    /// <summary>The invocation must not execute.</summary>
    Deny = 2,
}
