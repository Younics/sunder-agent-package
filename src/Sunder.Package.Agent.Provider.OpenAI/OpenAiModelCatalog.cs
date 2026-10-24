using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Settings;

namespace Sunder.Package.Agent.Provider.OpenAI;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
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

    private static readonly IReadOnlyDictionary<string, (int ContextWindow, int MaxOutputTokens)> CodexModelLimits =
        new Dictionary<string, (int ContextWindow, int MaxOutputTokens)>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-6.1-sol"] = (628000, 128000),
            ["gpt-6-astra"] = (400000, 128000),
            ["gpt-6-sol"] = (400000, 128000),
            ["gpt-6-luna"] = (400000, 128000),
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
        new() { VariantId = "none", DisplayName = "None", Description = "Disable reasoning effort when the selected model supports it.", ReasoningEffort = AgentReasoningEffort.None },
        new() { VariantId = "low", DisplayName = "Low", Description = "Use low reasoning effort for faster responses.", ReasoningEffort = AgentReasoningEffort.Low },
        new() { VariantId = "medium", DisplayName = "Medium", Description = "Use balanced reasoning effort.", ReasoningEffort = AgentReasoningEffort.Medium },
        new() { VariantId = "high", DisplayName = "High", Description = "Use high reasoning effort for complex tasks.", ReasoningEffort = AgentReasoningEffort.High },
        new() { VariantId = "xhigh", DisplayName = "Xhigh", Description = "Use extra-high reasoning effort for the hardest tasks.", ReasoningEffort = AgentReasoningEffort.ExtraHigh },
    ];

    private static readonly IReadOnlyList<AgentModelSpeedOptionDescriptor> FastSpeedOptions =
    [
        new() { SpeedOptionId = "fast", DisplayName = "Fast", Description = "Request accelerated processing without changing reasoning effort. Premium token pricing applies (currently 2× standard on GPT-6.1 Sol); availability depends on the connection, account and region." },
    ];

    private static readonly IReadOnlyList<AgentModelModeOptionDescriptor> ProModeOptions =
    [
        new() { ModeOptionId = "pro", DisplayName = "Pro", Description = "Use GPT-5.6 Pro reasoning mode.", DisablesReasoning = false },
    ];

    private static readonly IReadOnlyDictionary<string, DateOnly> ReleaseDates =
        new Dictionary<string, DateOnly>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai/gpt-6.1-sol"] = new(2026, 9, 29),
            ["openai/gpt-6-astra"] = new(2026, 9, 4),
            ["openai/gpt-6-sol"] = new(2026, 9, 22),
            ["openai/gpt-6-luna"] = new(2026, 9, 22),
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
        new() { ModelId = "openai/gpt-6.1-sol", DisplayName = "GPT-6.1 Sol", ContextWindow = 1050000, MaxOutputTokens = 128000,
            IsRecommended = true, Variants = ReasoningVariants, SpeedOptions = FastSpeedOptions, ModeOptions = null },
        new() { ModelId = "openai/gpt-6-astra", DisplayName = "GPT-6 Astra", ContextWindow = 1050000, MaxOutputTokens = 128000,
            IsRecommended = true, Variants = ReasoningVariants, SpeedOptions = FastSpeedOptions, ModeOptions = null },
        new() { ModelId = "openai/gpt-6-sol", DisplayName = "GPT-6 Sol", ContextWindow = 1050000, MaxOutputTokens = 128000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = FastSpeedOptions, ModeOptions = null },
        new() { ModelId = "openai/gpt-6-luna", DisplayName = "GPT-6 Luna", ContextWindow = 1050000, MaxOutputTokens = 128000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = FastSpeedOptions, ModeOptions = null },
        new() { ModelId = "openai/gpt-5.6", DisplayName = "GPT-5.6", ContextWindow = 1050000, MaxOutputTokens = 128000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = FastSpeedOptions, ModeOptions = ProModeOptions, ReleaseDate = null },
        new() { ModelId = "openai/gpt-5.6-sol", DisplayName = "GPT-5.6 Sol", ContextWindow = 1050000, MaxOutputTokens = 128000,
            IsRecommended = true, Variants = ReasoningVariants, SpeedOptions = FastSpeedOptions, ModeOptions = ProModeOptions, ReleaseDate = null },
        new() { ModelId = "openai/gpt-5.6-terra", DisplayName = "GPT-5.6 Terra", ContextWindow = 1050000, MaxOutputTokens = 128000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = FastSpeedOptions, ModeOptions = ProModeOptions, ReleaseDate = null },
        new() { ModelId = "openai/gpt-5.6-luna", DisplayName = "GPT-5.6 Luna", ContextWindow = 1050000, MaxOutputTokens = 128000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = FastSpeedOptions, ModeOptions = ProModeOptions, ReleaseDate = null },
        new() { ModelId = "openai/gpt-5.5", DisplayName = "GPT-5.5", ContextWindow = 1050000, MaxOutputTokens = 128000,
            IsRecommended = true, Variants = ReasoningVariants, SpeedOptions = FastSpeedOptions, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-5.5-pro", DisplayName = "GPT-5.5 Pro", ContextWindow = 1050000, MaxOutputTokens = 128000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-5.4", DisplayName = "GPT-5.4", ContextWindow = 1050000, MaxOutputTokens = 128000,
            IsRecommended = true, Variants = ReasoningVariants, SpeedOptions = FastSpeedOptions, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-5.4-pro", DisplayName = "GPT-5.4 Pro", ContextWindow = 1050000, MaxOutputTokens = 128000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-5.4-mini", DisplayName = "GPT-5.4 Mini", ContextWindow = 400000, MaxOutputTokens = 128000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = FastSpeedOptions, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-5.4-nano", DisplayName = "GPT-5.4 Nano", ContextWindow = 400000, MaxOutputTokens = 128000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-5.3-codex", DisplayName = "GPT-5.3 Codex", ContextWindow = 400000, MaxOutputTokens = 128000,
            IsRecommended = true, Variants = ReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-5.3-codex-spark", DisplayName = "GPT-5.3 Codex Spark", ContextWindow = 128000, MaxOutputTokens = 32000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-5.3-chat-latest", DisplayName = "GPT-5.3 Chat Latest", ContextWindow = 128000, MaxOutputTokens = 16384,
            IsRecommended = false, Variants = null, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-5.2", DisplayName = "GPT-5.2", ContextWindow = 400000, MaxOutputTokens = 128000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-5.2-codex", DisplayName = "GPT-5.2 Codex", ContextWindow = 400000, MaxOutputTokens = 128000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-5.2-pro", DisplayName = "GPT-5.2 Pro", ContextWindow = 400000, MaxOutputTokens = 128000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-5.2-chat-latest", DisplayName = "GPT-5.2 Chat Latest", ContextWindow = 128000, MaxOutputTokens = 16384,
            IsRecommended = false, Variants = null, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-5.1", DisplayName = "GPT-5.1", ContextWindow = 400000, MaxOutputTokens = 128000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-5.1-codex", DisplayName = "GPT-5.1 Codex", ContextWindow = 400000, MaxOutputTokens = 128000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-5.1-codex-max", DisplayName = "GPT-5.1 Codex Max", ContextWindow = 400000, MaxOutputTokens = 128000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-5.1-codex-mini", DisplayName = "GPT-5.1 Codex Mini", ContextWindow = 400000, MaxOutputTokens = 128000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-5.1-chat-latest", DisplayName = "GPT-5.1 Chat Latest", ContextWindow = 128000, MaxOutputTokens = 16384,
            IsRecommended = false, Variants = null, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-5", DisplayName = "GPT-5", ContextWindow = 400000, MaxOutputTokens = 128000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-5-codex", DisplayName = "GPT-5 Codex", ContextWindow = 400000, MaxOutputTokens = 128000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-5-mini", DisplayName = "GPT-5 Mini", ContextWindow = 400000, MaxOutputTokens = 128000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-5-nano", DisplayName = "GPT-5 Nano", ContextWindow = 400000, MaxOutputTokens = 128000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-5-pro", DisplayName = "GPT-5 Pro", ContextWindow = 400000, MaxOutputTokens = 272000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/o4-mini", DisplayName = "o4 Mini", ContextWindow = 200000, MaxOutputTokens = 100000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/o4-mini-deep-research", DisplayName = "o4 Mini Deep Research", ContextWindow = 200000, MaxOutputTokens = 100000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/o3", DisplayName = "o3", ContextWindow = 200000, MaxOutputTokens = 100000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/o3-mini", DisplayName = "o3 Mini", ContextWindow = 200000, MaxOutputTokens = 100000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/o3-pro", DisplayName = "o3 Pro", ContextWindow = 200000, MaxOutputTokens = 100000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/o3-deep-research", DisplayName = "o3 Deep Research", ContextWindow = 200000, MaxOutputTokens = 100000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/o1", DisplayName = "o1", ContextWindow = 200000, MaxOutputTokens = 100000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/o1-pro", DisplayName = "o1 Pro", ContextWindow = 200000, MaxOutputTokens = 100000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-4.1", DisplayName = "GPT-4.1", ContextWindow = 1047576, MaxOutputTokens = 32768,
            IsRecommended = false, Variants = null, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-4.1-mini", DisplayName = "GPT-4.1 Mini", ContextWindow = 1047576, MaxOutputTokens = 32768,
            IsRecommended = false, Variants = null, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-4.1-nano", DisplayName = "GPT-4.1 Nano", ContextWindow = 1047576, MaxOutputTokens = 32768,
            IsRecommended = false, Variants = null, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-4o", DisplayName = "GPT-4o", ContextWindow = 128000, MaxOutputTokens = 16384,
            IsRecommended = false, Variants = null, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-4o-2024-11-20", DisplayName = "GPT-4o 2024-11-20", ContextWindow = 128000, MaxOutputTokens = 16384,
            IsRecommended = false, Variants = null, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-4o-2024-08-06", DisplayName = "GPT-4o 2024-08-06", ContextWindow = 128000, MaxOutputTokens = 16384,
            IsRecommended = false, Variants = null, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-4o-2024-05-13", DisplayName = "GPT-4o 2024-05-13", ContextWindow = 128000, MaxOutputTokens = 4096,
            IsRecommended = false, Variants = null, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-4o-mini", DisplayName = "GPT-4o Mini", ContextWindow = 128000, MaxOutputTokens = 16384,
            IsRecommended = false, Variants = null, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-4-turbo", DisplayName = "GPT-4 Turbo", ContextWindow = 128000, MaxOutputTokens = 4096,
            IsRecommended = false, Variants = null, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-4", DisplayName = "GPT-4", ContextWindow = 8192, MaxOutputTokens = 8192,
            IsRecommended = false, Variants = null, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/gpt-3.5-turbo", DisplayName = "GPT-3.5 Turbo", ContextWindow = 16385, MaxOutputTokens = 4096,
            IsRecommended = false, Variants = null, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "openai/codex-mini-latest", DisplayName = "Codex Mini Latest", ContextWindow = 200000, MaxOutputTokens = 100000,
            IsRecommended = false, Variants = ReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
    ], ReleaseDates);

    private static readonly ProviderModelCatalogSnapshot Catalog = ProviderModelCatalog.ValidateAndOrder(
        VendorModels.Select(ReviewCapabilities),
        OpenAiProviderConfiguration.DefaultUtilityModelId,
        static _ => true);

    public static IReadOnlyList<AgentModelDescriptor> Models => Catalog.Models;

    public static IReadOnlyList<AgentModelDescriptor> CodexModels { get; } = Models
        .Where(model => CodexModelLimits.ContainsKey(OpenAiModelIds.Normalize(model.ModelId))
                        || OpenAiModelIds.Normalize(model.ModelId) is "gpt-5.3-codex" or "gpt-5.3-codex-spark" or "gpt-5.2-codex")
        .Select(model => CodexModelLimits.TryGetValue(OpenAiModelIds.Normalize(model.ModelId), out var limits)
            ? model with
            {
                ContextWindow = limits.ContextWindow,
                MaxOutputTokens = limits.MaxOutputTokens,
                ModeOptions = null,
            }
            : model with { ModeOptions = null })
        .ToArray();

    public static IReadOnlyList<PackageSettingsOption> UtilityModelOptions { get; } =
        Catalog.UtilityModelOptions;

    internal static OpenAiModelCapabilities GetCapabilities(string modelId)
    {
        var normalizedModelId = OpenAiModelIds.Normalize(modelId);
        var descriptor = ProviderModelCatalog.FindByNormalizedId(Models, modelId, OpenAiModelIds.Normalize);
        return new OpenAiModelCapabilities(
            descriptor?.Variants is { Count: > 0 },
            LowTextVerbosityModels.Contains(normalizedModelId),
            ResponsesLiteModels.Contains(normalizedModelId),
            descriptor?.DefaultReasoningVariantId ?? "medium");
    }

    internal static AgentModelDescriptor? Find(string modelId, bool connected = false)
        => ProviderModelCatalog.FindByNormalizedId(connected ? CodexModels : Models, modelId, OpenAiModelIds.Normalize);

    // Reviewed 2026-10-22 against the vendor model/effort guidance. Old IDs stay
    // selectable, but do not inherit capabilities merely because a new GPT does.
    private static AgentModelDescriptor ReviewCapabilities(AgentModelDescriptor model)
    {
        if (model.Variants is not { Count: > 0 }) return model;
        var id = OpenAiModelIds.Normalize(model.ModelId);
        string[] efforts = id switch
        {
            "gpt-6.1-sol" or "gpt-6-astra" => ["low", "medium", "high", "xhigh", "max"],
            "gpt-6-sol" or "gpt-6-luna" or "gpt-5.6" or "gpt-5.6-sol" or "gpt-5.6-terra" or "gpt-5.6-luna"
                => ["none", "low", "medium", "high", "xhigh", "max"],
            "gpt-5-pro" => ["high"],
            "gpt-5.2-pro" or "gpt-5.4-pro" or "gpt-5.5-pro" => ["medium", "high", "xhigh"],
            "gpt-5" or "gpt-5-mini" or "gpt-5-nano" => ["minimal", "low", "medium", "high"],
            "gpt-5.1" => ["none", "low", "medium", "high"],
            "gpt-5.1-codex-max" => ["low", "medium", "high", "xhigh"],
            "gpt-5.1-codex" or "gpt-5.1-codex-mini" => ["low", "medium", "high"],
            _ when id.StartsWith("gpt-5.", StringComparison.Ordinal) => ["none", "low", "medium", "high", "xhigh"],
            _ => ["low", "medium", "high"],
        };
        return model with
        {
            Variants = ProviderModelReasoning.Create(efforts),
            DefaultReasoningVariantId = efforts.Contains("medium") ? "medium" : "high",
        };
    }
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal readonly record struct OpenAiModelCapabilities(
    bool SupportsReasoning,
    bool UseLowTextVerbosity,
    bool UseResponsesLite,
    string DefaultReasoningEffort);
