namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentToolActivationRequirement(
    string CapabilityKind,
    string? SourceId = null,
    string? CapabilityId = null);
