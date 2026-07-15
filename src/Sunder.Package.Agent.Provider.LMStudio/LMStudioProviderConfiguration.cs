using Sunder.Sdk.Settings;
using Sunder.Package.Agent.Provider.Shared;

namespace Sunder.Package.Agent.Provider.LMStudio;

public static class LMStudioProviderConfiguration
{
    public const string DefaultBaseUrl = "http://127.0.0.1:1234/v1";
    public const string BaseUrlKey = "connection.baseUrl";
    public const string ApiKeyKey = "connection.apiKey";
    public const string UtilityModelKey = "utility.modelId";

    public static PackageSettingsSchema Schema { get; } = new(
        "Configure how the Agent package connects to a local LM Studio server.",
        [
            new PackageSettingsSection(
                "connection",
                "Connection",
                "Point the provider at an LM Studio OpenAI-compatible endpoint.",
                [
                    new PackageSettingsField(
                        BaseUrlKey,
                        "Base URL",
                        PackageSettingsFieldKind.Text,
                        description: "LM Studio OpenAI-compatible base URL.",
                        isRequired: true,
                        defaultValue: DefaultBaseUrl,
                        placeholder: DefaultBaseUrl
                    ),
                    new PackageSettingsField(
                        ApiKeyKey,
                        "API key",
                        PackageSettingsFieldKind.Secret,
                        description: "Optional API key if your LM Studio endpoint requires one.",
                        placeholder: "lm-studio-key"
                    )
                ]
            ),
            ProviderConfigurationSections.UtilityModelText(
                UtilityModelKey,
                "Choose the local model used for background utility work such as session title generation.",
                "Optional. Use a full model id like lmstudio/model-name. Leave blank to use the first model returned by LM Studio.",
                "lmstudio/model-name")
        ]
    );
}
