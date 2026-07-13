using Sunder.Sdk.Configuration;
using Sunder.Package.Agent.Provider.Shared;

namespace Sunder.Package.Agent.Provider.Anthropic;

public static class AnthropicProviderConfiguration
{
    public const string ApiKeySecretKey = "auth.apiKey";
    public const string UtilityModelKey = "utility.modelId";
    public const string DefaultUtilityModelId = "anthropic/claude-haiku-4-5";

    internal static ProviderUtilityModelSelection UtilityModelSelection { get; } = new(
        UtilityModelKey,
        DefaultUtilityModelId,
        AnthropicModelCatalog.UtilityModelOptions);

    public static PackageConfigurationSchema Schema { get; } = new(
        "sunder.package.agent.provider.anthropic",
        "Sunder Agent Provider Anthropic",
        "Configure how the Agent package authenticates and talks to Claude models.",
        [
            ProviderConfigurationSections.ApiKey(
                ApiKeySecretKey,
                "Anthropic currently uses direct API-key access.",
                "Anthropic API key used for Claude API access.",
                "sk-ant-..."),
            ProviderConfigurationSections.UtilityModelSelect(
                UtilityModelSelection,
                "Choose the cheap model used for background utility work such as session title generation.")
        ]
    );
}
