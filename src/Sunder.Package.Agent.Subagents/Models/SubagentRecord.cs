using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Subagents.Models;

public sealed record SubagentRecord(
    string SubagentId,
    string DisplayName,
    string? Description,
    string? Instructions,
    string? ChatProviderId,
    string? ChatModelId,
    IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>? SelectableCapabilityAssignments,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? ChatModelSettingsJson = null)
{
    public bool HasRequiredDescription => !string.IsNullOrWhiteSpace(Description);

    public string DescriptionDisplay => HasRequiredDescription
        ? Description!.Trim()
        : "Incomplete: description required before this subagent can be used.";
}
