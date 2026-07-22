namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Describes a chat model and the provider-defined options that can be persisted with its binding.
/// </summary>
/// <remarks>
/// Model identifiers must be stable and unique within their provider catalog. Input modalities and
/// tool-calling behavior are run capabilities, not guarantees made by this descriptor.
/// </remarks>
/// <param name="ModelId">The stable, provider-resolvable model identifier, normally qualified by provider.</param>
/// <param name="DisplayName">The model name shown to users.</param>
/// <param name="ContextWindow">The provider-advertised maximum total context size in tokens.</param>
/// <param name="MaxOutputTokens">The provider-advertised maximum generated output size in tokens.</param>
/// <param name="IsRecommended">Whether the provider recommends this model as a catalog choice; it does not force selection.</param>
/// <param name="Variants">Optional reasoning choices supported by this model.</param>
/// <param name="SpeedOptions">Optional latency or processing-tier choices supported by this model.</param>
/// <param name="ModeOptions">Optional provider operating modes supported by this model.</param>
public sealed record AgentModelDescriptor(
    string ModelId,
    string DisplayName,
    int ContextWindow,
    int MaxOutputTokens,
    bool IsRecommended = false,
    IReadOnlyList<AgentModelVariantDescriptor>? Variants = null,
    IReadOnlyList<AgentModelSpeedOptionDescriptor>? SpeedOptions = null,
    IReadOnlyList<AgentModelModeOptionDescriptor>? ModeOptions = null)
{
    /// <summary>
    /// Gets the model's public release date for catalog ordering, or <see langword="null" /> when unknown.
    /// </summary>
    public DateOnly? ReleaseDate { get; init; }
}

/// <summary>
/// Describes a selectable reasoning configuration for a particular chat model.
/// </summary>
/// <param name="VariantId">The stable identifier stored in <see cref="AgentChatModelSettings" />.</param>
/// <param name="DisplayName">The option name shown to users.</param>
/// <param name="Description">An optional explanation of cost, latency, or reasoning behavior.</param>
/// <param name="ReasoningEffort">The standardized effort hint to send to the provider, or <see langword="null" /> for provider-specific handling.</param>
public sealed record AgentModelVariantDescriptor(
    string VariantId,
    string DisplayName,
    string? Description = null,
    AgentReasoningEffort? ReasoningEffort = null);

/// <summary>
/// Describes a provider-specific processing tier or latency option for a chat model.
/// </summary>
/// <param name="SpeedOptionId">The stable identifier stored in <see cref="AgentChatModelSettings" /> and passed to the provider adapter.</param>
/// <param name="DisplayName">The option name shown to users.</param>
/// <param name="Description">An optional explanation, including relevant cost or latency tradeoffs.</param>
public sealed record AgentModelSpeedOptionDescriptor(
    string SpeedOptionId,
    string DisplayName,
    string? Description = null);

/// <summary>
/// Describes a provider-specific operating mode for a chat model.
/// </summary>
/// <param name="ModeOptionId">The stable identifier stored in <see cref="AgentChatModelSettings" /> and passed to the provider adapter.</param>
/// <param name="DisplayName">The option name shown to users.</param>
/// <param name="Description">An optional explanation of the mode's behavior and tradeoffs.</param>
/// <param name="DisablesReasoning">Whether selecting this mode suppresses any independently selected reasoning variant.</param>
public sealed record AgentModelModeOptionDescriptor(
    string ModeOptionId,
    string DisplayName,
    string? Description = null,
    bool DisablesReasoning = false);

/// <summary>
/// Defines provider-neutral reasoning effort hints.
/// </summary>
/// <remarks>Providers map supported values to their own controls; a catalog must not advertise an effort it cannot translate.</remarks>
public enum AgentReasoningEffort
{
    /// <summary>Explicitly disables provider reasoning when the selected model supports that control.</summary>
    None = 0,

    /// <summary>Requests low effort to favor latency and token efficiency.</summary>
    Low = 1,

    /// <summary>Requests a balanced reasoning effort.</summary>
    Medium = 2,

    /// <summary>Requests high effort for more complex work.</summary>
    High = 3,

    /// <summary>Requests the provider's supported effort tier above high.</summary>
    ExtraHigh = 4,
}
