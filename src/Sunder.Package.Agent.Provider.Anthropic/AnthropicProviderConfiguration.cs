using Sunder.Sdk.Configuration;

namespace Sunder.Package.Agent.Provider.Anthropic;

public static class AnthropicProviderConfiguration
{
    public const string UtilityModelKey = "utility.modelId";
    public const string DefaultUtilityModelId = "anthropic/claude-haiku-4-5";

    public static PackageConfigurationSchema Schema { get; } = new(
        "sunder.package.agent.provider.anthropic",
        "Sunder Agent Provider Anthropic",
        "Configure how the Agent package authenticates and talks to Claude models.",
        [
            new PackageConfigurationSection(
                "authentication",
                "Authentication",
                "Anthropic currently uses direct API-key access.",
                [
                    new PackageConfigurationField(
                        "auth.apiKey",
                        "API key",
                        PackageConfigurationFieldKind.Secret,
                        Description: "Anthropic API key used for Claude API access.",
                        Placeholder: "sk-ant-..."
                    )
                ]
            ),
            new PackageConfigurationSection(
                "utility",
                "Utility model",
                "Choose the cheap model used for background utility work such as session title generation.",
                [
                    new PackageConfigurationField(
                        UtilityModelKey,
                        "Utility model",
                        PackageConfigurationFieldKind.Select,
                        Description: "Used for short background tasks, not regular agent replies.",
                        IsRequired: true,
                        DefaultValue: DefaultUtilityModelId,
                        Options: AnthropicModelCatalog.UtilityModelOptions
                    )
                ]
            )
        ]
    );
}
