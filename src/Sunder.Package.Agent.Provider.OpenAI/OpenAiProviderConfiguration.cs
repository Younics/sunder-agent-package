using Sunder.Sdk.Configuration;
using Sunder.Package.Agent.Provider.Shared;

namespace Sunder.Package.Agent.Provider.OpenAI;

public static class OpenAiProviderConfiguration
{
    public const string ApiKeySecretKey = "auth.apiKey";
    public const string UtilityModelKey = "utility.modelId";
    public const string DefaultUtilityModelId = "openai/gpt-5.4-mini";

    public static IReadOnlyList<PackageConfigurationOption> UtilityModelOptions => OpenAiModelCatalog.UtilityModelOptions;

    internal static ProviderUtilityModelSelection UtilityModelSelection { get; } = new(
        UtilityModelKey,
        DefaultUtilityModelId,
        OpenAiModelCatalog.UtilityModelOptions,
        OpenAiSettingsViewModel.NormalizeLegacyUtilityModelId);

    public static PackageConfigurationSchema Schema { get; } = new(
        "sunder.package.agent.provider.openai",
        "Sunder Agent Provider OpenAI",
        "Configure how the Agent package authenticates and talks to OpenAI models.",
        [
            new PackageConfigurationSection(
                "authentication",
                "Authentication",
                "Choose between direct API-key usage and ChatGPT Plus/Pro OAuth.",
                [
                    new PackageConfigurationField(
                        OpenAiAuthMode.ConfigurationKey,
                        "Auth mode",
                        PackageConfigurationFieldKind.Select,
                        Description: "The OpenAI provider supports direct API key usage and browser-based ChatGPT Plus/Pro OAuth.",
                        IsRequired: true,
                        DefaultValue: OpenAiAuthMode.CodexConnected,
                        Options:
                        [
                            new PackageConfigurationOption(OpenAiAuthMode.ApiKey, "API key"),
                            new PackageConfigurationOption(OpenAiAuthMode.CodexConnected, "ChatGPT Plus/Pro"),
                        ]
                    ),
                    new PackageConfigurationField(
                        ApiKeySecretKey,
                        "API key",
                        PackageConfigurationFieldKind.Secret,
                        Description: "Used when auth mode is set to API key.",
                        Placeholder: "sk-..."
                    )
                ]
            ),
            ProviderConfigurationSections.UtilityModelSelect(
                UtilityModelSelection,
                "Choose the cheap model used for background utility work such as session title generation.")
        ]
    );
}
