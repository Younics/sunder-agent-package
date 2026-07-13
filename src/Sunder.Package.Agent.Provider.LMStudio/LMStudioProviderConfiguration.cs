using Sunder.Sdk.Configuration;
using Sunder.Package.Agent.Provider.Shared;

namespace Sunder.Package.Agent.Provider.LMStudio;

public static class LMStudioProviderConfiguration
{
    public const string DefaultBaseUrl = "http://127.0.0.1:1234/v1";
    public const string BaseUrlKey = "connection.baseUrl";
    public const string ApiKeyKey = "connection.apiKey";
    public const string UtilityModelKey = "utility.modelId";

    public static PackageConfigurationSchema Schema { get; } = new(
        "sunder.package.agent.provider.lmstudio",
        "Sunder Agent Provider LM Studio",
        "Configure how the Agent package connects to a local LM Studio server.",
        [
            new PackageConfigurationSection(
                "connection",
                "Connection",
                "Point the provider at an LM Studio OpenAI-compatible endpoint.",
                [
                    new PackageConfigurationField(
                        BaseUrlKey,
                        "Base URL",
                        PackageConfigurationFieldKind.Text,
                        Description: "LM Studio OpenAI-compatible base URL.",
                        IsRequired: true,
                        DefaultValue: DefaultBaseUrl,
                        Placeholder: DefaultBaseUrl
                    ),
                    new PackageConfigurationField(
                        ApiKeyKey,
                        "API key",
                        PackageConfigurationFieldKind.Secret,
                        Description: "Optional API key if your LM Studio endpoint requires one.",
                        Placeholder: "lm-studio-key"
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
