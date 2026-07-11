using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Configuration;

namespace Sunder.Package.Agent.Provider.Gemini;

internal static class GeminiModelCatalog
{
    private static readonly IReadOnlyList<AgentModelVariantDescriptor> ThinkingLevelVariants =
    [
        new("low", "Low", "Use low thinking effort for faster responses.", AgentReasoningEffort.Low),
        new("medium", "Medium", "Use balanced thinking effort for most tasks.", AgentReasoningEffort.Medium),
        new("high", "High", "Use high thinking effort for complex tasks.", AgentReasoningEffort.High),
    ];

    private static readonly IReadOnlyList<AgentModelVariantDescriptor> ThinkingBudgetVariants =
    [
        new("low", "Low", "Use a low valid thinking-token budget.", AgentReasoningEffort.Low),
        new("medium", "Medium", "Use a balanced valid thinking-token budget.", AgentReasoningEffort.Medium),
        new("high", "High", "Use the model family's maximum thinking-token budget.", AgentReasoningEffort.High),
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
        ApplyReleaseDates([
        new("gemini/gemini-3.5-flash", "Gemini 3.5 Flash", 1048576, 65536, IsRecommended: true, Variants: ThinkingLevelVariants),
        new("gemini/gemini-3.1-pro-preview", "Gemini 3.1 Pro Preview", 1048576, 65536, IsRecommended: true, Variants: ThinkingLevelVariants),
        new("gemini/gemini-3.1-pro-preview-customtools", "Gemini 3.1 Pro Preview Custom Tools", 1048576, 65536, Variants: ThinkingLevelVariants),
        new("gemini/gemini-3.1-flash-lite", "Gemini 3.1 Flash Lite", 1048576, 65536, Variants: ThinkingLevelVariants),
        new("gemini/gemini-3-flash-preview", "Gemini 3 Flash Preview", 1048576, 65536, Variants: ThinkingLevelVariants),
        new("gemini/gemini-2.5-pro", "Gemini 2.5 Pro", 1048576, 65536, Variants: ThinkingBudgetVariants),
        new("gemini/gemini-2.5-flash", "Gemini 2.5 Flash", 1048576, 65536, IsRecommended: true, Variants: ThinkingBudgetVariants),
        new("gemini/gemini-2.5-flash-lite", "Gemini 2.5 Flash Lite", 1048576, 65536, Variants: ThinkingBudgetVariants),
        new("gemini/gemini-flash-latest", "Gemini Flash Latest", 1048576, 65536),
        new("gemini/gemini-flash-lite-latest", "Gemini Flash Lite Latest", 1048576, 65536),
        new("gemini/gemma-4-31b-it", "Gemma 4 31B IT", 262144, 32768),
        new("gemini/gemma-4-26b-a4b-it", "Gemma 4 26B A4B IT", 262144, 32768),
    ]);

    private static readonly ProviderModelCatalogSnapshot Catalog = ProviderModelCatalog.ValidateAndOrder(
        VendorModels,
        GeminiProviderConfiguration.DefaultUtilityModelId,
        static _ => true);

    public static IReadOnlyList<AgentModelDescriptor> Models => Catalog.Models;

    public static IReadOnlyList<PackageConfigurationOption> UtilityModelOptions { get; } =
        Catalog.UtilityModelOptions;

    private static IReadOnlyList<AgentModelDescriptor> ApplyReleaseDates(
        IEnumerable<AgentModelDescriptor> models)
        => models.Select(model => model with { ReleaseDate = ReleaseDates[model.ModelId] }).ToArray();
}
