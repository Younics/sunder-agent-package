namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentProfileModelBindingRecord(
    string ProfileId,
    string CapabilityKind,
    string? ProviderId,
    string? ModelId,
    string? SettingsJson,
    DateTimeOffset UpdatedAtUtc);
