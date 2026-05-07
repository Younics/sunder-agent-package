using Sunder.Sdk.Configuration;

namespace Sunder.Package.Agent.Provider.OpenAI;

public static class OpenAiProviderConfiguration
{
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
                        "auth.mode",
                        "Auth mode",
                        PackageConfigurationFieldKind.Select,
                        Description: "The OpenAI provider supports direct API key usage and browser-based ChatGPT Plus/Pro OAuth.",
                        IsRequired: true,
                        DefaultValue: "codex-connected",
                        Options:
                        [
                            new PackageConfigurationOption("api-key", "API key"),
                            new PackageConfigurationOption("codex-connected", "ChatGPT Plus/Pro"),
                        ]
                    ),
                    new PackageConfigurationField(
                        "auth.apiKey",
                        "API key",
                        PackageConfigurationFieldKind.Secret,
                        Description: "Used when auth mode is set to API key.",
                        Placeholder: "sk-..."
                    )
                ]
            )
        ]
    );
}
