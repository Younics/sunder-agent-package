namespace Sunder.Package.Agent.Shared.Presentation;

internal sealed record ModelBindingSelection(
    string? ProviderId,
    string? ModelId,
    string? SettingsJson = null);

internal sealed record ModelBindingEditorOptions(
    bool SelectFirstProvider,
    string NoProvidersText,
    string NoProviderSelectedText,
    string LoadingText,
    string LoadFailurePrefix,
    string? EmptyProviderLabel = null,
    string DefaultReasoningDescription = "Use the selected model's default reasoning behavior.",
    bool IncludeSpeedInModelPicker = true);

internal sealed record ModelReasoningOption(
    string? VariantId,
    string Label,
    string? Description = null,
    bool IsAvailable = true)
{
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
}

internal sealed record ModelSpeedOption(
    string? SpeedOptionId,
    string Label,
    string? Description = null,
    bool IsAvailable = true)
{
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
}

internal sealed record ModelModeOption(
    string? ModeOptionId,
    string Label,
    string? Description = null,
    bool DisablesReasoning = false,
    bool IsAvailable = true)
{
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
}
