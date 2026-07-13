namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentProfileSelectableCapabilityRequest(
    AgentProfileRecord? Profile = null);

public sealed record AgentProfileSelectableCapabilityDescriptor(
    string Kind,
    string CapabilityId,
    string? SourceId,
    string DisplayName,
    string? Description,
    string? StatusText = null,
    bool IsSelectable = true,
    string? SourceDisplayName = null,
    string? GroupId = null,
    string? GroupDisplayName = null,
    string? GroupDescription = null,
    int GroupSortOrder = 50);
