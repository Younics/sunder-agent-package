using Sunder.Sdk.Configuration;

namespace Sunder.Package.Agent.Provider.LMStudio;

public static class LMStudioProviderConfiguration
{
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
                        "connection.baseUrl",
                        "Base URL",
                        PackageConfigurationFieldKind.Text,
                        Description: "LM Studio OpenAI-compatible base URL.",
                        IsRequired: true,
                        DefaultValue: "http://127.0.0.1:1234/v1",
                        Placeholder: "http://127.0.0.1:1234/v1"
                    ),
                    new PackageConfigurationField(
                        "connection.apiKey",
                        "API key",
                        PackageConfigurationFieldKind.Secret,
                        Description: "Optional API key if your LM Studio endpoint requires one.",
                        Placeholder: "lm-studio-key"
                    )
                ]
            )
        ]
    );
}
