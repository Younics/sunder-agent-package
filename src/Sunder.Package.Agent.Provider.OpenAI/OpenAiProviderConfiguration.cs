using Sunder.Sdk.Settings;

namespace Sunder.Package.Agent.Provider.OpenAI;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public static class OpenAiProviderConfiguration
{
    public const string ApiKeySecretKey = "auth.apiKey";
    public const string UtilityModelKey = "utility.modelId";
    public const string DefaultUtilityModelId = "openai/gpt-5.4-mini";

    public static IReadOnlyList<PackageSettingsOption> UtilityModelOptions => OpenAiModelCatalog.UtilityModelOptions;

    internal static OpenAiUtilityModelSelection UtilityModelSelection { get; } = new(
        UtilityModelKey,
        DefaultUtilityModelId,
        OpenAiModelCatalog.UtilityModelOptions);

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
            new PackageSettingsSection(
                "utility",
                "Utility model",
                "Choose the cheap model used for background utility work such as session title generation.",
                [
                    new PackageSettingsField(
                        UtilityModelSelection.ConfigurationKey,
                        "Utility model",
                        PackageSettingsFieldKind.Select,
                        description: "Used for short background tasks, not regular agent replies.",
                        isRequired: true,
                        defaultValue: UtilityModelSelection.DefaultModelId,
                        options: UtilityModelSelection.Options),
                ])
        ]
    );

    internal static string? NormalizeLegacyUtilityModelId(string? modelId)
        => string.Equals(modelId?.Trim(), "openai/gpt-5.5-fast", StringComparison.OrdinalIgnoreCase)
            ? "openai/gpt-5.5"
            : modelId;
}
