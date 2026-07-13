using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Configuration;

namespace Sunder.Package.Agent.Provider.OpenAI;

internal static class OpenAiModelCatalog
{
    private static readonly IReadOnlySet<string> LowTextVerbosityModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "gpt-5.4",
        "gpt-5.4-mini",
        "gpt-5.4-nano",
        "gpt-5.5",
        "gpt-5.6",
        "gpt-5.6-sol",
        "gpt-5.6-terra",
        "gpt-5.6-luna",
    };

    private static readonly IReadOnlySet<string> ResponsesLiteModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "gpt-5.6-sol",
        "gpt-5.6-terra",
        "gpt-5.6-luna",
    };

    private static readonly IReadOnlySet<string> CodexUnsupportedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "gpt-5.6",
    };

    private static readonly IReadOnlyDictionary<string, (int ContextWindow, int MaxOutputTokens)> CodexModelLimits =
        new Dictionary<string, (int ContextWindow, int MaxOutputTokens)>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.6-sol"] = (500000, 128000),
            ["gpt-5.6-terra"] = (500000, 128000),
            ["gpt-5.6-luna"] = (500000, 128000),
            ["gpt-5.5"] = (400000, 128000),
            ["gpt-5.4"] = (400000, 128000),
            ["gpt-5.4-mini"] = (400000, 128000),
            ["gpt-5.2"] = (400000, 128000),
        };

    private static readonly IReadOnlyList<AgentModelVariantDescriptor> ReasoningVariants =
    [
        new("none", "None", "Disable reasoning effort when the selected model supports it.", AgentReasoningEffort.None),
        new("low", "Low", "Use low reasoning effort for faster responses.", AgentReasoningEffort.Low),
        new("medium", "Medium", "Use balanced reasoning effort.", AgentReasoningEffort.Medium),
        new("high", "High", "Use high reasoning effort for complex tasks.", AgentReasoningEffort.High),
        new("xhigh", "Xhigh", "Use extra-high reasoning effort for the hardest tasks.", AgentReasoningEffort.ExtraHigh),
    ];

    private static readonly IReadOnlyList<AgentModelSpeedOptionDescriptor> FastSpeedOptions =
    [
        new("fast", "Fast", "Use OpenAI priority processing for lower latency."),
    ];

    private static readonly IReadOnlyList<AgentModelModeOptionDescriptor> ProModeOptions =
    [
        new("pro", "Pro", "Use GPT-5.6 Pro reasoning mode."),
    ];

    private static readonly IReadOnlyDictionary<string, DateOnly> ReleaseDates =
        new Dictionary<string, DateOnly>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai/gpt-5.6"] = new(2026, 7, 9),
            ["openai/gpt-5.6-sol"] = new(2026, 7, 9),
            ["openai/gpt-5.6-terra"] = new(2026, 7, 9),
            ["openai/gpt-5.6-luna"] = new(2026, 7, 9),
            ["openai/gpt-5.5"] = new(2026, 4, 23),
            ["openai/gpt-5.5-pro"] = new(2026, 4, 23),
            ["openai/gpt-5.4-mini"] = new(2026, 3, 17),
            ["openai/gpt-5.4-nano"] = new(2026, 3, 17),
            ["openai/gpt-5.4"] = new(2026, 3, 5),
            ["openai/gpt-5.4-pro"] = new(2026, 3, 5),
            ["openai/gpt-5.3-chat-latest"] = new(2026, 3, 3),
            ["openai/gpt-5.3-codex"] = new(2026, 2, 5),
            ["openai/gpt-5.3-codex-spark"] = new(2026, 2, 5),
            ["openai/gpt-5.2"] = new(2025, 12, 11),
            ["openai/gpt-5.2-codex"] = new(2025, 12, 11),
            ["openai/gpt-5.2-pro"] = new(2025, 12, 11),
            ["openai/gpt-5.2-chat-latest"] = new(2025, 12, 11),
            ["openai/gpt-5.1"] = new(2025, 11, 13),
            ["openai/gpt-5.1-codex"] = new(2025, 11, 13),
            ["openai/gpt-5.1-codex-max"] = new(2025, 11, 13),
            ["openai/gpt-5.1-codex-mini"] = new(2025, 11, 13),
            ["openai/gpt-5.1-chat-latest"] = new(2025, 11, 13),
            ["openai/gpt-5-pro"] = new(2025, 10, 6),
            ["openai/gpt-5-codex"] = new(2025, 9, 15),
            ["openai/gpt-5"] = new(2025, 8, 7),
            ["openai/gpt-5-mini"] = new(2025, 8, 7),
            ["openai/gpt-5-nano"] = new(2025, 8, 7),
            ["openai/o3-pro"] = new(2025, 6, 10),
            ["openai/o4-mini"] = new(2025, 4, 16),
            ["openai/o3"] = new(2025, 4, 16),
            ["openai/gpt-4.1"] = new(2025, 4, 14),
            ["openai/gpt-4.1-mini"] = new(2025, 4, 14),
            ["openai/gpt-4.1-nano"] = new(2025, 4, 14),
            ["openai/o1-pro"] = new(2025, 3, 19),
            ["openai/o3-mini"] = new(2024, 12, 20),
            ["openai/o1"] = new(2024, 12, 5),
            ["openai/gpt-4o-2024-11-20"] = new(2024, 11, 20),
            ["openai/gpt-4o-2024-08-06"] = new(2024, 8, 6),
            ["openai/gpt-4o-mini"] = new(2024, 7, 18),
            ["openai/o4-mini-deep-research"] = new(2024, 6, 26),
            ["openai/o3-deep-research"] = new(2024, 6, 26),
            ["openai/gpt-4o"] = new(2024, 5, 13),
            ["openai/gpt-4o-2024-05-13"] = new(2024, 5, 13),
            ["openai/gpt-4-turbo"] = new(2023, 11, 6),
            ["openai/gpt-4"] = new(2023, 11, 6),
            ["openai/gpt-3.5-turbo"] = new(2023, 3, 1),
        };

    private static readonly IReadOnlyList<AgentModelDescriptor> VendorModels =
        ProviderModelCatalog.ApplyReleaseDates([
        new("openai/gpt-5.6", "GPT-5.6", 1050000, 128000, Variants: ReasoningVariants, SpeedOptions: FastSpeedOptions, ModeOptions: ProModeOptions),
        new("openai/gpt-5.6-sol", "GPT-5.6 Sol", 1050000, 128000, IsRecommended: true, Variants: ReasoningVariants, SpeedOptions: FastSpeedOptions, ModeOptions: ProModeOptions),
        new("openai/gpt-5.6-terra", "GPT-5.6 Terra", 1050000, 128000, Variants: ReasoningVariants, SpeedOptions: FastSpeedOptions, ModeOptions: ProModeOptions),
        new("openai/gpt-5.6-luna", "GPT-5.6 Luna", 1050000, 128000, Variants: ReasoningVariants, SpeedOptions: FastSpeedOptions, ModeOptions: ProModeOptions),
        new("openai/gpt-5.5", "GPT-5.5", 1050000, 128000, IsRecommended: true, Variants: ReasoningVariants, SpeedOptions: FastSpeedOptions),
        new("openai/gpt-5.5-pro", "GPT-5.5 Pro", 1050000, 128000, Variants: ReasoningVariants),
        new("openai/gpt-5.4", "GPT-5.4", 1050000, 128000, IsRecommended: true, Variants: ReasoningVariants, SpeedOptions: FastSpeedOptions),
        new("openai/gpt-5.4-pro", "GPT-5.4 Pro", 1050000, 128000, Variants: ReasoningVariants),
        new("openai/gpt-5.4-mini", "GPT-5.4 Mini", 400000, 128000, Variants: ReasoningVariants, SpeedOptions: FastSpeedOptions),
        new("openai/gpt-5.4-nano", "GPT-5.4 Nano", 400000, 128000, Variants: ReasoningVariants),
        new("openai/gpt-5.3-codex", "GPT-5.3 Codex", 400000, 128000, IsRecommended: true, Variants: ReasoningVariants),
        new("openai/gpt-5.3-codex-spark", "GPT-5.3 Codex Spark", 128000, 32000, Variants: ReasoningVariants),
        new("openai/gpt-5.3-chat-latest", "GPT-5.3 Chat Latest", 128000, 16384),
        new("openai/gpt-5.2", "GPT-5.2", 400000, 128000, Variants: ReasoningVariants),
        new("openai/gpt-5.2-codex", "GPT-5.2 Codex", 400000, 128000, Variants: ReasoningVariants),
        new("openai/gpt-5.2-pro", "GPT-5.2 Pro", 400000, 128000, Variants: ReasoningVariants),
        new("openai/gpt-5.2-chat-latest", "GPT-5.2 Chat Latest", 128000, 16384),
        new("openai/gpt-5.1", "GPT-5.1", 400000, 128000, Variants: ReasoningVariants),
        new("openai/gpt-5.1-codex", "GPT-5.1 Codex", 400000, 128000, Variants: ReasoningVariants),
        new("openai/gpt-5.1-codex-max", "GPT-5.1 Codex Max", 400000, 128000, Variants: ReasoningVariants),
        new("openai/gpt-5.1-codex-mini", "GPT-5.1 Codex Mini", 400000, 128000, Variants: ReasoningVariants),
        new("openai/gpt-5.1-chat-latest", "GPT-5.1 Chat Latest", 128000, 16384),
        new("openai/gpt-5", "GPT-5", 400000, 128000, Variants: ReasoningVariants),
        new("openai/gpt-5-codex", "GPT-5 Codex", 400000, 128000, Variants: ReasoningVariants),
        new("openai/gpt-5-mini", "GPT-5 Mini", 400000, 128000, Variants: ReasoningVariants),
        new("openai/gpt-5-nano", "GPT-5 Nano", 400000, 128000, Variants: ReasoningVariants),
        new("openai/gpt-5-pro", "GPT-5 Pro", 400000, 272000, Variants: ReasoningVariants),
        new("openai/o4-mini", "o4 Mini", 200000, 100000, Variants: ReasoningVariants),
        new("openai/o4-mini-deep-research", "o4 Mini Deep Research", 200000, 100000, Variants: ReasoningVariants),
        new("openai/o3", "o3", 200000, 100000, Variants: ReasoningVariants),
        new("openai/o3-mini", "o3 Mini", 200000, 100000, Variants: ReasoningVariants),
        new("openai/o3-pro", "o3 Pro", 200000, 100000, Variants: ReasoningVariants),
        new("openai/o3-deep-research", "o3 Deep Research", 200000, 100000, Variants: ReasoningVariants),
        new("openai/o1", "o1", 200000, 100000, Variants: ReasoningVariants),
        new("openai/o1-pro", "o1 Pro", 200000, 100000, Variants: ReasoningVariants),
        new("openai/gpt-4.1", "GPT-4.1", 1047576, 32768),
        new("openai/gpt-4.1-mini", "GPT-4.1 Mini", 1047576, 32768),
        new("openai/gpt-4.1-nano", "GPT-4.1 Nano", 1047576, 32768),
        new("openai/gpt-4o", "GPT-4o", 128000, 16384),
        new("openai/gpt-4o-2024-11-20", "GPT-4o 2024-11-20", 128000, 16384),
        new("openai/gpt-4o-2024-08-06", "GPT-4o 2024-08-06", 128000, 16384),
        new("openai/gpt-4o-2024-05-13", "GPT-4o 2024-05-13", 128000, 4096),
        new("openai/gpt-4o-mini", "GPT-4o Mini", 128000, 16384),
        new("openai/gpt-4-turbo", "GPT-4 Turbo", 128000, 4096),
        new("openai/gpt-4", "GPT-4", 8192, 8192),
        new("openai/gpt-3.5-turbo", "GPT-3.5 Turbo", 16385, 4096),
        new("openai/codex-mini-latest", "Codex Mini Latest", 200000, 100000, Variants: ReasoningVariants),
    ], ReleaseDates);

    private static readonly ProviderModelCatalogSnapshot Catalog = ProviderModelCatalog.ValidateAndOrder(
        VendorModels,
        OpenAiProviderConfiguration.DefaultUtilityModelId,
        static _ => true);

    public static IReadOnlyList<AgentModelDescriptor> Models => Catalog.Models;

    public static IReadOnlyList<AgentModelDescriptor> CodexModels { get; } = Models
        .Where(model => !CodexUnsupportedModels.Contains(OpenAiModelIds.Normalize(model.ModelId)))
        .Select(model => CodexModelLimits.TryGetValue(OpenAiModelIds.Normalize(model.ModelId), out var limits)
            ? model with
            {
                ContextWindow = limits.ContextWindow,
                MaxOutputTokens = limits.MaxOutputTokens,
            }
            : model)
        .ToArray();

    public static IReadOnlyList<PackageConfigurationOption> UtilityModelOptions { get; } =
        Catalog.UtilityModelOptions;

    internal static OpenAiModelCapabilities GetCapabilities(string modelId)
    {
        var normalizedModelId = OpenAiModelIds.Normalize(modelId);
        var descriptor = ProviderModelCatalog.FindByNormalizedId(Models, modelId, OpenAiModelIds.Normalize);
        return new OpenAiModelCapabilities(
            descriptor?.Variants is { Count: > 0 },
            LowTextVerbosityModels.Contains(normalizedModelId),
            ResponsesLiteModels.Contains(normalizedModelId),
            string.Equals(normalizedModelId, "gpt-5.6-sol", StringComparison.OrdinalIgnoreCase) ? "low" : "medium");
    }
}

internal readonly record struct OpenAiModelCapabilities(
    bool SupportsReasoning,
    bool UseLowTextVerbosity,
    bool UseResponsesLite,
    string DefaultReasoningEffort);
