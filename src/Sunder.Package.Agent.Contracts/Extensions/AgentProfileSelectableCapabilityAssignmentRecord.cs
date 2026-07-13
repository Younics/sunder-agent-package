namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentProfileSelectableCapabilityAssignmentRecord(
    string Kind,
    string CapabilityId,
    string? SourceId = null);
