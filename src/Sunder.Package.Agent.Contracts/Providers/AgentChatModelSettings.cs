namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Stores the provider-defined options selected for a chat model binding.
/// </summary>
/// <remarks>
/// Option identifiers are resolved against the selected <see cref="AgentModelDescriptor" />.
/// A missing or unrecognized identifier leaves that option at the provider default. When a
/// selected mode has <see cref="AgentModelModeOptionDescriptor.DisablesReasoning" /> set, the
/// runtime does not apply the reasoning variant. These settings contain identifiers, not secrets.
/// </remarks>
/// <param name="ReasoningVariantId">The selected reasoning variant identifier, or <see langword="null" /> for the provider default.</param>
/// <param name="SpeedOptionId">The selected speed option identifier, or <see langword="null" /> for the provider default.</param>
/// <param name="ModeOptionId">The selected operating mode identifier, or <see langword="null" /> for the provider default.</param>
public sealed record AgentChatModelSettings(
    string? ReasoningVariantId = null,
    string? SpeedOptionId = null,
    string? ModeOptionId = null);
