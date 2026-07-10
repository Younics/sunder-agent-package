using Sunder.Sdk.Configuration;

namespace Sunder.Package.Agent.Provider.Gemini;

public static class GeminiProviderConfiguration
{
    public const string UtilityModelKey = "utility.modelId";
    public const string DefaultUtilityModelId = "gemini/gemini-2.5-flash";

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
                        Options: GeminiModelCatalog.UtilityModelOptions
                    )
                ]
            )
        ]
    );
}
