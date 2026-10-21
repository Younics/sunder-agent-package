using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Settings;

namespace Sunder.Package.Agent.Provider.Anthropic;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class AnthropicModelCatalog
{
    private static readonly IReadOnlyList<AgentModelVariantDescriptor> StandardReasoningVariants =
    [
        new() { VariantId = "low", DisplayName = "Low", Description = "Use low effort for faster, more token-efficient responses.", ReasoningEffort = AgentReasoningEffort.Low },
        new() { VariantId = "medium", DisplayName = "Medium", Description = "Use balanced effort for most agentic tasks.", ReasoningEffort = AgentReasoningEffort.Medium },
        new() { VariantId = "high", DisplayName = "High", Description = "Use high effort for complex reasoning and difficult coding tasks.", ReasoningEffort = AgentReasoningEffort.High },
    ];

    private static readonly IReadOnlyList<AgentModelVariantDescriptor> OpusReasoningVariants =
    [
        new() { VariantId = "low", DisplayName = "Low", Description = "Use low effort for faster, more token-efficient responses.", ReasoningEffort = AgentReasoningEffort.Low },
        new() { VariantId = "medium", DisplayName = "Medium", Description = "Use balanced effort for most agentic tasks.", ReasoningEffort = AgentReasoningEffort.Medium },
        new() { VariantId = "high", DisplayName = "High", Description = "Use high effort for complex reasoning and difficult coding tasks.", ReasoningEffort = AgentReasoningEffort.High },
        new() { VariantId = "xhigh", DisplayName = "Xhigh", Description = "Use extra-high effort for advanced coding and long-horizon agentic work.", ReasoningEffort = AgentReasoningEffort.ExtraHigh },
    ];

    private static readonly IReadOnlyList<AgentModelSpeedOptionDescriptor> FastSpeedOptions =
    [
        new() { SpeedOptionId = "fast", DisplayName = "Fast", Description = "Use Anthropic fast mode for lower latency." },
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

    private static readonly IReadOnlyList<AgentModelDescriptor> VendorModels =
        ProviderModelCatalog.ApplyReleaseDates([
        new() { ModelId = "anthropic/claude-fable-5", DisplayName = "Claude Fable 5", ContextWindow = 1000000, MaxOutputTokens = 128000,
            IsRecommended = true, Variants = OpusReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "anthropic/claude-opus-4-8", DisplayName = "Claude Opus 4.8", ContextWindow = 1000000, MaxOutputTokens = 128000,
            IsRecommended = true, Variants = OpusReasoningVariants, SpeedOptions = FastSpeedOptions, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "anthropic/claude-opus-4-7", DisplayName = "Claude Opus 4.7", ContextWindow = 1000000, MaxOutputTokens = 128000,
            IsRecommended = false, Variants = OpusReasoningVariants, SpeedOptions = FastSpeedOptions, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "anthropic/claude-opus-4-6", DisplayName = "Claude Opus 4.6", ContextWindow = 1000000, MaxOutputTokens = 128000,
            IsRecommended = false, Variants = OpusReasoningVariants, SpeedOptions = FastSpeedOptions, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "anthropic/claude-sonnet-5", DisplayName = "Claude Sonnet 5", ContextWindow = 1000000, MaxOutputTokens = 128000,
            IsRecommended = true, Variants = StandardReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "anthropic/claude-sonnet-4-6", DisplayName = "Claude Sonnet 4.6", ContextWindow = 1000000, MaxOutputTokens = 128000,
            IsRecommended = false, Variants = StandardReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "anthropic/claude-sonnet-4-5", DisplayName = "Claude Sonnet 4.5", ContextWindow = 1000000, MaxOutputTokens = 64000,
            IsRecommended = false, Variants = StandardReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "anthropic/claude-sonnet-4-5-20250929", DisplayName = "Claude Sonnet 4.5 (2025-09-29)", ContextWindow = 1000000, MaxOutputTokens = 64000,
            IsRecommended = false, Variants = StandardReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "anthropic/claude-opus-4-5", DisplayName = "Claude Opus 4.5", ContextWindow = 200000, MaxOutputTokens = 64000,
            IsRecommended = false, Variants = StandardReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "anthropic/claude-opus-4-5-20251101", DisplayName = "Claude Opus 4.5 (2025-11-01)", ContextWindow = 200000, MaxOutputTokens = 64000,
            IsRecommended = false, Variants = StandardReasoningVariants, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "anthropic/claude-haiku-4-5", DisplayName = "Claude Haiku 4.5", ContextWindow = 200000, MaxOutputTokens = 64000,
            IsRecommended = false, Variants = null, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
        new() { ModelId = "anthropic/claude-haiku-4-5-20251001", DisplayName = "Claude Haiku 4.5 (2025-10-01)", ContextWindow = 200000, MaxOutputTokens = 64000,
            IsRecommended = false, Variants = null, SpeedOptions = null, ModeOptions = null, ReleaseDate = null },
    ], ReleaseDates, requireEveryModel: true);

    private static readonly ProviderModelCatalogSnapshot Catalog = ProviderModelCatalog.ValidateAndOrder(
        VendorModels,
        AnthropicProviderConfiguration.DefaultUtilityModelId,
        static _ => true);

    public static IReadOnlyList<AgentModelDescriptor> Models => Catalog.Models;

    public static IReadOnlyList<PackageSettingsOption> UtilityModelOptions { get; } =
        Catalog.UtilityModelOptions;

    internal static int? GetMaxOutputTokens(string modelId)
    {
        return checked((int?)ProviderModelCatalog.FindByNormalizedId(
                Models,
                modelId,
                static id => ProviderModelId.RemovePrefix(id, "anthropic"))
            ?.MaxOutputTokens);
    }
}
