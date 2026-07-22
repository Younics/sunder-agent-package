namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Defines the model-facing schema and host-enforced selection, permission, and scheduling metadata for a tool.
/// </summary>
/// <remarks>
/// The descriptor is security-sensitive. <paramref name="IsReadOnly" /> affects permission fallback,
/// result caching, and mutation barriers; it must be false if any valid invocation can change state.
/// Descriptions and runtime instructions enter privileged prompt context and must be static,
/// package-authored text rather than untrusted workspace, network, model, or user-controlled content.
/// Identifiers are compared case-insensitively and should remain stable across package updates.
/// </remarks>
/// <param name="ToolId">The stable function identifier, unique in the ready tool catalog.</param>
/// <param name="DisplayName">The tool name shown to users.</param>
/// <param name="Description">The concise model-facing description of when and how to use the tool.</param>
/// <param name="IsReadOnly">Whether every valid invocation is free of external and persisted state changes.</param>
/// <param name="RequiresNetwork">Whether normal execution communicates outside the local runtime; this is metadata, not a network permission grant.</param>
/// <param name="ArgumentsJsonSchema">A JSON Schema for an object argument payload; <see langword="null" /> produces an empty closed schema for non-native sources, while a native source supplies its declaration separately.</param>
/// <param name="SourceKind">The stable source category; the host fills it from the source when omitted.</param>
/// <param name="SourceId">The stable owning source identifier; the host fills it from the source when omitted.</param>
/// <param name="SourceDisplayName">The owning source name shown to users; the host fills it when omitted.</param>
/// <param name="Aliases">Optional legacy or alternate identifiers used for profile assignment and source-specific resolution.</param>
/// <param name="SelectionScope">Whether profiles select this tool individually or through a group.</param>
/// <param name="SelectionGroupId">The stable group identifier required when <paramref name="SelectionScope" /> is <see cref="AgentToolSelectionScope.Group" />.</param>
/// <param name="SelectionGroupDisplayName">The optional group name shown in profile editors.</param>
/// <param name="SelectionGroupDescription">An optional user-facing explanation of the grouped capability.</param>
/// <param name="RuntimeInstructions">Optional trusted package-authored instructions added to the system prompt while the tool is available.</param>
/// <param name="ActivationRequirement">An optional capability assignment that replaces normal tool/group selection.</param>
/// <param name="Priority">The relative catalog and model preference when multiple tools can satisfy a task.</param>
public sealed record AgentToolDescriptor(
    string ToolId,
    string DisplayName,
    string Description,
    bool IsReadOnly = true,
    bool RequiresNetwork = false,
    string? ArgumentsJsonSchema = null,
    string? SourceKind = null,
    string? SourceId = null,
    string? SourceDisplayName = null,
    IReadOnlyList<string>? Aliases = null,
    AgentToolSelectionScope SelectionScope = AgentToolSelectionScope.Tool,
    string? SelectionGroupId = null,
    string? SelectionGroupDisplayName = null,
    string? SelectionGroupDescription = null,
    string? RuntimeInstructions = null,
    AgentToolActivationRequirement? ActivationRequirement = null,
    AgentToolPriority Priority = AgentToolPriority.Medium)
{
    /// <summary>
    /// Gets the execution scheduling promise made by the tool implementation.
    /// </summary>
    /// <remarks>Marking a tool parallel-safe is independent of <see cref="IsReadOnly" /> and requires safe simultaneous independent invocations.</remarks>
    public AgentToolConcurrencyMode ConcurrencyMode { get; init; } = AgentToolConcurrencyMode.Sequential;
}

/// <summary>
/// Defines whether independent calls to a tool may execute concurrently within one provider turn.
/// </summary>
public enum AgentToolConcurrencyMode
{
    /// <summary>Each call forms a scheduling barrier and executes after earlier calls complete.</summary>
    Sequential = 0,

    /// <summary>Independent calls may execute simultaneously without corrupting state or depending on call order.</summary>
    ParallelSafe = 1,
}
