namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentProfileCapabilityConsumerDescriptor(
    string CapabilityKind,
    string DisplayName,
    string Description);
