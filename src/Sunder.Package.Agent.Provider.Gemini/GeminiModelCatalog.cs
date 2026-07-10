using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Configuration;

namespace Sunder.Package.Agent.Provider.Gemini;

internal static class GeminiModelCatalog
{
    private static readonly IReadOnlyList<AgentModelVariantDescriptor> ReasoningVariants =
    [
        new("low", "Low", "Use low thinking effort for faster responses.", AgentReasoningEffort.Low),
        new("medium", "Medium", "Use balanced thinking effort for most tasks.", AgentReasoningEffort.Medium),
        new("high", "High", "Use high thinking effort for complex tasks.", AgentReasoningEffort.High),
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

    public static IReadOnlyList<AgentModelDescriptor> Models { get; } =
        ApplyReleaseDates([
        new("gemini/gemini-3.5-flash", "Gemini 3.5 Flash", 1048576, 65536, IsRecommended: true, Variants: ReasoningVariants),
        new("gemini/gemini-3.1-pro-preview", "Gemini 3.1 Pro Preview", 1048576, 65536, IsRecommended: true, Variants: ReasoningVariants),
        new("gemini/gemini-3.1-pro-preview-customtools", "Gemini 3.1 Pro Preview Custom Tools", 1048576, 65536, Variants: ReasoningVariants),
        new("gemini/gemini-3.1-flash-lite", "Gemini 3.1 Flash Lite", 1048576, 65536, Variants: ReasoningVariants),
        new("gemini/gemini-3-flash-preview", "Gemini 3 Flash Preview", 1048576, 65536, Variants: ReasoningVariants),
        new("gemini/gemini-2.5-pro", "Gemini 2.5 Pro", 1048576, 65536, Variants: ReasoningVariants),
        new("gemini/gemini-2.5-flash", "Gemini 2.5 Flash", 1048576, 65536, IsRecommended: true, Variants: ReasoningVariants),
        new("gemini/gemini-2.5-flash-lite", "Gemini 2.5 Flash Lite", 1048576, 65536, Variants: ReasoningVariants),
        new("gemini/gemini-flash-latest", "Gemini Flash Latest", 1048576, 65536, Variants: ReasoningVariants),
        new("gemini/gemini-flash-lite-latest", "Gemini Flash Lite Latest", 1048576, 65536, Variants: ReasoningVariants),
        new("gemini/gemma-4-31b-it", "Gemma 4 31B IT", 262144, 32768),
        new("gemini/gemma-4-26b-a4b-it", "Gemma 4 26B A4B IT", 262144, 32768),
    ]);

    public static IReadOnlyList<PackageConfigurationOption> UtilityModelOptions { get; } =
        Models.OrderNewestFirst()
            .Select(model => new PackageConfigurationOption(model.ModelId, model.DisplayName))
            .ToArray();

    private static IReadOnlyList<AgentModelDescriptor> ApplyReleaseDates(
        IEnumerable<AgentModelDescriptor> models)
        => models.Select(model => model with { ReleaseDate = ReleaseDates[model.ModelId] }).ToArray();
}
