using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Configuration;

namespace Sunder.Package.Agent.Provider.Anthropic;

internal static class AnthropicModelCatalog
{
    private static readonly IReadOnlyList<AgentModelVariantDescriptor> StandardReasoningVariants =
    [
        new("low", "Low", "Use low effort for faster, more token-efficient responses.", AgentReasoningEffort.Low),
        new("medium", "Medium", "Use balanced effort for most agentic tasks.", AgentReasoningEffort.Medium),
        new("high", "High", "Use high effort for complex reasoning and difficult coding tasks.", AgentReasoningEffort.High),
    ];

    private static readonly IReadOnlyList<AgentModelVariantDescriptor> OpusReasoningVariants =
    [
        new("low", "Low", "Use low effort for faster, more token-efficient responses.", AgentReasoningEffort.Low),
        new("medium", "Medium", "Use balanced effort for most agentic tasks.", AgentReasoningEffort.Medium),
        new("high", "High", "Use high effort for complex reasoning and difficult coding tasks.", AgentReasoningEffort.High),
        new("xhigh", "Xhigh", "Use extra-high effort for advanced coding and long-horizon agentic work.", AgentReasoningEffort.ExtraHigh),
    ];

    private static readonly IReadOnlyList<AgentModelSpeedOptionDescriptor> FastSpeedOptions =
    [
        new("fast", "Fast", "Use Anthropic fast mode for lower latency."),
    ];

    private static readonly IReadOnlyDictionary<string, DateOnly> ReleaseDates =
        new Dictionary<string, DateOnly>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic/claude-sonnet-5"] = new(2026, 6, 29),
            ["anthropic/claude-fable-5"] = new(2026, 6, 7),
            ["anthropic/claude-opus-4-8"] = new(2026, 5, 28),
            ["anthropic/claude-opus-4-7"] = new(2026, 4, 14),
            ["anthropic/claude-sonnet-4-6"] = new(2026, 2, 17),
            ["anthropic/claude-opus-4-6"] = new(2026, 2, 4),
            ["anthropic/claude-opus-4-5"] = new(2025, 11, 24),
            ["anthropic/claude-opus-4-5-20251101"] = new(2025, 11, 24),
            ["anthropic/claude-haiku-4-5"] = new(2025, 10, 15),
            ["anthropic/claude-haiku-4-5-20251001"] = new(2025, 10, 15),
            ["anthropic/claude-sonnet-4-5"] = new(2025, 9, 29),
            ["anthropic/claude-sonnet-4-5-20250929"] = new(2025, 9, 29),
        };

    public static IReadOnlyList<AgentModelDescriptor> Models { get; } =
        ApplyReleaseDates([
        new("anthropic/claude-fable-5", "Claude Fable 5", 1000000, 128000, IsRecommended: true, Variants: OpusReasoningVariants),
        new("anthropic/claude-opus-4-8", "Claude Opus 4.8", 1000000, 128000, IsRecommended: true, Variants: OpusReasoningVariants, SpeedOptions: FastSpeedOptions),
        new("anthropic/claude-opus-4-7", "Claude Opus 4.7", 1000000, 128000, Variants: OpusReasoningVariants, SpeedOptions: FastSpeedOptions),
        new("anthropic/claude-opus-4-6", "Claude Opus 4.6", 1000000, 128000, Variants: OpusReasoningVariants, SpeedOptions: FastSpeedOptions),
        new("anthropic/claude-sonnet-5", "Claude Sonnet 5", 1000000, 128000, IsRecommended: true, Variants: StandardReasoningVariants),
        new("anthropic/claude-sonnet-4-6", "Claude Sonnet 4.6", 1000000, 128000, Variants: StandardReasoningVariants),
        new("anthropic/claude-sonnet-4-5", "Claude Sonnet 4.5", 1000000, 64000, Variants: StandardReasoningVariants),
        new("anthropic/claude-sonnet-4-5-20250929", "Claude Sonnet 4.5 (2025-09-29)", 1000000, 64000, Variants: StandardReasoningVariants),
        new("anthropic/claude-opus-4-5", "Claude Opus 4.5", 200000, 64000, Variants: StandardReasoningVariants),
        new("anthropic/claude-opus-4-5-20251101", "Claude Opus 4.5 (2025-11-01)", 200000, 64000, Variants: StandardReasoningVariants),
        new("anthropic/claude-haiku-4-5", "Claude Haiku 4.5", 200000, 64000),
        new("anthropic/claude-haiku-4-5-20251001", "Claude Haiku 4.5 (2025-10-01)", 200000, 64000),
    ]);

    public static IReadOnlyList<PackageConfigurationOption> UtilityModelOptions { get; } =
        Models.OrderNewestFirst()
            .Select(model => new PackageConfigurationOption(model.ModelId, model.DisplayName))
            .ToArray();

    private static IReadOnlyList<AgentModelDescriptor> ApplyReleaseDates(
        IEnumerable<AgentModelDescriptor> models)
        => models.Select(model => model with { ReleaseDate = ReleaseDates[model.ModelId] }).ToArray();
}
