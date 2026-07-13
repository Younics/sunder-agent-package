namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentBehaviorLoopDescriptor(
    string LoopId,
    string DisplayName,
    string Description,
    string? SourceId = null,
    IReadOnlyList<string>? FeatureKinds = null,
    string? SettingsSchemaJson = null);
