using Sunder.Sdk.Settings;
using Sunder.Package.Agent.Provider.Shared;

namespace Sunder.Package.Agent.Provider.Gemini;

public static class GeminiProviderConfiguration
{
    public const string ApiKeySecretKey = "auth.apiKey";
    public const string UtilityModelKey = "utility.modelId";
    public const string DefaultUtilityModelId = "gemini/gemini-2.5-flash";

    internal static ProviderUtilityModelSelection UtilityModelSelection { get; } = new(
        UtilityModelKey,
        DefaultUtilityModelId,
        GeminiModelCatalog.UtilityModelOptions);

    public static PackageSettingsSchema Schema { get; } = new(
        "Configure how the Agent package authenticates and talks to Gemini models.",
        [
            ProviderConfigurationSections.ApiKey(
                ApiKeySecretKey,
                "Gemini currently uses direct API-key access.",
                "Gemini API key used for Gemini Developer API access.",
                "AIza..."),
            ProviderConfigurationSections.UtilityModelSelect(
                UtilityModelSelection,
                "Choose the cheap model used for background utility work such as session title generation.")
        ]
    );
}
