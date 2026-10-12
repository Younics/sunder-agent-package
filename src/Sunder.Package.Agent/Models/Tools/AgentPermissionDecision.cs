namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Defines the policy action taken for a classified tool invocation.
/// </summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public enum AgentPermissionDecision
{
    /// <summary>The invocation may proceed without an interactive prompt.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("allow")] Allow = 0,

    /// <summary>The run is suspended until a user explicitly approves or denies the invocation.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("ask")] Ask = 1,

    /// <summary>The invocation must not execute.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("deny")] Deny = 2,
}
