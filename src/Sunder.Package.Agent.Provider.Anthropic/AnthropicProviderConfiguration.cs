using Sunder.Sdk.Configuration;

namespace Sunder.Package.Agent.Provider.Anthropic;

public static class AnthropicProviderConfiguration
{
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
            )
        ]
    );
}
