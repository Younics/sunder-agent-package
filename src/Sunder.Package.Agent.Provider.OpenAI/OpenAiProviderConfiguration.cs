using Sunder.Sdk.Settings;
using Sunder.Package.Agent.Provider.Shared;

namespace Sunder.Package.Agent.Provider.OpenAI;

public static class OpenAiProviderConfiguration
{
    public const string ApiKeySecretKey = "auth.apiKey";
    public const string UtilityModelKey = "utility.modelId";
    public const string DefaultUtilityModelId = "openai/gpt-5.4-mini";

    public static IReadOnlyList<PackageSettingsOption> UtilityModelOptions => OpenAiModelCatalog.UtilityModelOptions;

    internal static ProviderUtilityModelSelection UtilityModelSelection { get; } = new(
        UtilityModelKey,
        DefaultUtilityModelId,
        OpenAiModelCatalog.UtilityModelOptions,
        OpenAiSettingsViewModel.NormalizeLegacyUtilityModelId);

    public static PackageSettingsSchema Schema { get; } = new(
        "Configure how the Agent package authenticates and talks to OpenAI models.",
        [
            new PackageSettingsSection(
                "authentication",
                "Authentication",
                "Choose between direct API-key usage and ChatGPT Plus/Pro OAuth.",
                [
                    new PackageSettingsField(
                        OpenAiAuthMode.ConfigurationKey,
                        "Auth mode",
                        PackageSettingsFieldKind.Select,
                        description: "The OpenAI provider supports direct API key usage and browser-based ChatGPT Plus/Pro OAuth.",
                        isRequired: true,
                        defaultValue: OpenAiAuthMode.CodexConnected,
                        options:
                        [
                            new PackageSettingsOption(OpenAiAuthMode.ApiKey, "API key"),
                            new PackageSettingsOption(OpenAiAuthMode.CodexConnected, "ChatGPT Plus/Pro"),
                        ]
                    ),
                    new PackageSettingsField(
                        ApiKeySecretKey,
                        "API key",
                        PackageSettingsFieldKind.Secret,
                        description: "Used when auth mode is set to API key.",
                        placeholder: "sk-..."
                    )
                ]
            ),
            ProviderConfigurationSections.UtilityModelSelect(
                UtilityModelSelection,
                "Choose the cheap model used for background utility work such as session title generation.")
        ]
    );
}
