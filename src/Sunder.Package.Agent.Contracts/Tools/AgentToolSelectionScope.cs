namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Defines how a tool is assigned to an agent profile.
/// </summary>
public enum AgentToolSelectionScope
{
    /// <summary>The tool is selected individually by its tool and source identity.</summary>
    Tool = 0,

    /// <summary>The tool is enabled as part of its declared selection group.</summary>
    Group = 1,
}
