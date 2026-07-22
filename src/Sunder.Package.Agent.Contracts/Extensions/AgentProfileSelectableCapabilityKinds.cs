namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Defines well-known categories for capabilities that can be assigned to an agent profile.
/// </summary>
/// <remarks>
/// Kinds are stable, ordinal case-insensitive routing identifiers used with a capability id and optional source
/// id. They describe selection semantics only; an assignment never replaces current catalog resolution,
/// readiness, permission, or execution-scope enforcement. Extension-defined kinds should be package-scoped to
/// avoid collisions.
/// </remarks>
public static class AgentProfileSelectableCapabilityKinds
{
    /// <summary>
    /// Identifies assignment of one concrete tool by its tool identifier and optional source identity.
    /// </summary>
    public const string Tool = "tool";

    /// <summary>
    /// Identifies assignment of a provider-defined group of tools by selection-group identifier.
    /// </summary>
    /// <remarks>Group membership is resolved dynamically and can change without changing the persisted assignment.</remarks>
    public const string ToolGroup = "tool-group";

    /// <summary>
    /// Identifies assignment of a subagent capability by its subagent identifier and source identity.
    /// </summary>
    public const string Subagent = "subagent";
}
