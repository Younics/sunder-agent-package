namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Defines relative catalog ordering and model preference among tools that can satisfy the same task.
/// </summary>
/// <remarks>Priority does not bypass profile assignment, readiness, permission, or sequential execution barriers.</remarks>
public enum AgentToolPriority
{
    /// <summary>A fallback tool to prefer only when higher-priority tools do not fit or cannot complete the task.</summary>
    Low = 0,

    /// <summary>The normal preference for general-purpose tools.</summary>
    Medium = 1,

    /// <summary>A specialized or primary tool the model should prefer when applicable.</summary>
    High = 2,
}
