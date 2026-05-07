using Sunder.Sdk.Configuration;

namespace Sunder.Package.Agent.Provider.Gemini;

public static class GeminiProviderConfiguration
{
    public static PackageConfigurationSchema Schema { get; } = new(
        "sunder.package.agent.provider.gemini",
        "Sunder Agent Provider Gemini",
        "Configure how the Agent package authenticates and talks to Gemini models.",
        [
            new PackageConfigurationSection(
                "authentication",
                "Authentication",
                "Gemini currently uses direct API-key access.",
                [
                    new PackageConfigurationField(
                        "auth.apiKey",
                        "API key",
                        PackageConfigurationFieldKind.Secret,
                        Description: "Gemini API key used for Gemini Developer API access.",
                        Placeholder: "AIza..."
                    )
                ]
            )
        ]
    );
}
