namespace Sunder.Package.Agent.Contracts.Models;

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
    AgentToolPriority Priority = AgentToolPriority.Medium);
