using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Settings;

namespace Sunder.Package.Agent.Provider.Gemini;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class GeminiModelCatalog
{
    private static readonly IReadOnlyList<AgentModelVariantDescriptor> ThinkingLevelVariants =
    [
        new() { VariantId = "low", DisplayName = "Low", Description = "Use low thinking effort for faster responses.", ReasoningEffort = AgentReasoningEffort.Low },
        new() { VariantId = "medium", DisplayName = "Medium", Description = "Use balanced thinking effort for most tasks.", ReasoningEffort = AgentReasoningEffort.Medium },
        new() { VariantId = "high", DisplayName = "High", Description = "Use high thinking effort for complex tasks.", ReasoningEffort = AgentReasoningEffort.High },
    ];

    private static readonly IReadOnlyList<AgentModelVariantDescriptor> ThinkingBudgetVariants =
    [
        new() { VariantId = "low", DisplayName = "Low", Description = "Use a low valid thinking-token budget.", ReasoningEffort = AgentReasoningEffort.Low },
        new() { VariantId = "medium", DisplayName = "Medium", Description = "Use a balanced valid thinking-token budget.", ReasoningEffort = AgentReasoningEffort.Medium },
        new() { VariantId = "high", DisplayName = "High", Description = "Use the model family's maximum thinking-token budget.", ReasoningEffort = AgentReasoningEffort.High },
    ];

    private static readonly IReadOnlyDictionary<string, DateOnly> ReleaseDates =
        new Dictionary<string, DateOnly>(StringComparer.OrdinalIgnoreCase)
        {
            ["gemini/gemini-3.5-flash"] = new(2026, 5, 19),
            ["gemini/gemini-3.1-flash-lite"] = new(2026, 5, 7),
            ["gemini/gemma-4-31b-it"] = new(2026, 4, 2),
            ["gemini/gemma-4-26b-a4b-it"] = new(2026, 4, 2),
            ["gemini/gemini-3.1-pro-preview"] = new(2026, 2, 19),
            ["gemini/gemini-3.1-pro-preview-customtools"] = new(2026, 2, 19),
            ["gemini/gemini-3-flash-preview"] = new(2025, 12, 17),
            ["gemini/gemini-flash-latest"] = new(2025, 9, 25),
            ["gemini/gemini-flash-lite-latest"] = new(2025, 9, 25),
            ["gemini/gemini-2.5-pro"] = new(2025, 6, 17),
            ["gemini/gemini-2.5-flash"] = new(2025, 6, 17),
            ["gemini/gemini-2.5-flash-lite"] = new(2025, 6, 17),
        };

    private static readonly IReadOnlyList<AgentModelDescriptor> VendorModels =
        ProviderModelCatalog.ApplyReleaseDates([
        new() { ModelId = "gemini/gemini-3.5-flash", DisplayName = "Gemini 3.5 Flash", ContextWindow = 1048576, MaxOutputTokens = 65536,
            IsRecommended = true, Variants = ThinkingLevelVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "gemini/gemini-3.1-pro-preview", DisplayName = "Gemini 3.1 Pro Preview", ContextWindow = 1048576, MaxOutputTokens = 65536,
            IsRecommended = true, Variants = ThinkingLevelVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "gemini/gemini-3.1-pro-preview-customtools", DisplayName = "Gemini 3.1 Pro Preview Custom Tools", ContextWindow = 1048576, MaxOutputTokens = 65536,
            IsRecommended = false, Variants = ThinkingLevelVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "gemini/gemini-3.1-flash-lite", DisplayName = "Gemini 3.1 Flash Lite", ContextWindow = 1048576, MaxOutputTokens = 65536,
            IsRecommended = false, Variants = ThinkingLevelVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "gemini/gemini-3-flash-preview", DisplayName = "Gemini 3 Flash Preview", ContextWindow = 1048576, MaxOutputTokens = 65536,
            IsRecommended = false, Variants = ThinkingLevelVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "gemini/gemini-2.5-pro", DisplayName = "Gemini 2.5 Pro", ContextWindow = 1048576, MaxOutputTokens = 65536,
            IsRecommended = false, Variants = ThinkingBudgetVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "gemini/gemini-2.5-flash", DisplayName = "Gemini 2.5 Flash", ContextWindow = 1048576, MaxOutputTokens = 65536,
            IsRecommended = true, Variants = ThinkingBudgetVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "gemini/gemini-2.5-flash-lite", DisplayName = "Gemini 2.5 Flash Lite", ContextWindow = 1048576, MaxOutputTokens = 65536,
            IsRecommended = false, Variants = ThinkingBudgetVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "gemini/gemini-flash-latest", DisplayName = "Gemini Flash Latest", ContextWindow = 1048576, MaxOutputTokens = 65536,
            IsRecommended = false, Variants = null, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "gemini/gemini-flash-lite-latest", DisplayName = "Gemini Flash Lite Latest", ContextWindow = 1048576, MaxOutputTokens = 65536,
            IsRecommended = false, Variants = null, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "gemini/gemma-4-31b-it", DisplayName = "Gemma 4 31B IT", ContextWindow = 262144, MaxOutputTokens = 32768,
            IsRecommended = false, Variants = null, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "gemini/gemma-4-26b-a4b-it", DisplayName = "Gemma 4 26B A4B IT", ContextWindow = 262144, MaxOutputTokens = 32768,
            IsRecommended = false, Variants = null, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
    ], ReleaseDates, requireEveryModel: true);

    private static readonly ProviderModelCatalogSnapshot Catalog = ProviderModelCatalog.ValidateAndOrder(
        VendorModels,
        GeminiProviderConfiguration.DefaultUtilityModelId,
        static _ => true);

    public static IReadOnlyList<AgentModelDescriptor> Models => Catalog.Models;

    public static IReadOnlyList<PackageSettingsOption> UtilityModelOptions { get; } =
        Catalog.UtilityModelOptions;
}
