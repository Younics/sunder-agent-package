namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Defines how a tool is assigned to an agent profile.
/// </summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public enum AgentToolSelectionScope
{
    /// <summary>The tool is selected individually by its tool and source identity.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("tool")] Tool = 0,

    /// <summary>The tool is enabled as part of its declared selection group.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("group")] Group = 1,
}
