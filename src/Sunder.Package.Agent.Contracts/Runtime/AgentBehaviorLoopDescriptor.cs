namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Describes a behavior-loop implementation that profiles can select.
/// </summary>
/// <remarks>
/// Identifier strings and feature kinds are opaque extension contracts. The record retains
/// <paramref name="FeatureKinds"/> without cloning it; providers must expose an immutable collection.
/// </remarks>
/// <param name="LoopId">The stable loop identifier used in profile persistence.</param>
/// <param name="DisplayName">The user-facing loop name.</param>
/// <param name="Description">A user-facing explanation of the loop's execution strategy.</param>
/// <param name="SourceId">An optional package/source identifier that disambiguates equal loop identifiers.</param>
/// <param name="FeatureKinds">Optional stable capability tags advertised by the loop, such as support for orchestration.</param>
/// <param name="SettingsSchemaJson">Optional extension-defined JSON describing the profile settings accepted by this loop, typically a JSON Schema document.</param>
public sealed record AgentBehaviorLoopDescriptor(
    string LoopId,
    string DisplayName,
    string Description,
    string? SourceId = null,
    IReadOnlyList<string>? FeatureKinds = null,
    string? SettingsSchemaJson = null);
