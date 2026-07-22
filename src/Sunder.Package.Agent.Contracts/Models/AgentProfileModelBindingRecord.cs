namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Captures the persisted provider and model selection for one profile capability.
/// </summary>
/// <param name="ProfileId">The opaque identifier of the owning profile.</param>
/// <param name="CapabilityKind">The extension-defined capability key, such as chat or embedding.</param>
/// <param name="ProviderId">The selected provider identifier, or <see langword="null"/> when no provider is configured.</param>
/// <param name="ModelId">The selected provider-specific model identifier, or <see langword="null"/> when no model is configured.</param>
/// <param name="SettingsJson">Opaque provider/model settings JSON, or <see langword="null"/> for defaults. The target provider defines its schema and error behavior.</param>
/// <param name="UpdatedAtUtc">The UTC time at which this binding snapshot was last persisted.</param>
public sealed record AgentProfileModelBindingRecord(
    string ProfileId,
    string CapabilityKind,
    string? ProviderId,
    string? ModelId,
    string? SettingsJson,
    DateTimeOffset UpdatedAtUtc);
