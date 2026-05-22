using Sunder.Sdk.Configuration;

namespace Sunder.Package.Agent.Provider.OpenAI;

public static class OpenAiProviderConfiguration
{
    public const string UtilityModelKey = "utility.modelId";
    public const string DefaultUtilityModelId = "openai/gpt-5.4-mini";

    public static IReadOnlyList<PackageConfigurationOption> UtilityModelOptions { get; } =
    [
        new("openai/gpt-5.5-fast", "GPT-5.5 Fast"),
        new("openai/gpt-5.5", "GPT-5.5"),
        new("openai/gpt-5.4", "GPT-5.4"),
        new(DefaultUtilityModelId, "GPT-5.4 Mini"),
        new("openai/gpt-5.3-codex", "GPT-5.3 Codex"),
        new("openai/gpt-5.3-codex-spark", "GPT-5.3 Codex Spark"),
        new("openai/gpt-5.2", "GPT-5.2"),
        new("openai/gpt-5.2-codex", "GPT-5.2 Codex"),
        new("openai/gpt-5.1-codex", "GPT-5.1 Codex"),
        new("openai/gpt-5.1-codex-max", "GPT-5.1 Codex Max"),
        new("openai/gpt-5.1-codex-mini", "GPT-5.1 Codex Mini"),
        new("openai/gpt-5-codex", "GPT-5 Codex"),
        new("openai/codex-mini-latest", "Codex Mini Latest"),
    ];

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
                        Options: UtilityModelOptions
                    )
                ]
            )
        ]
    );
}
