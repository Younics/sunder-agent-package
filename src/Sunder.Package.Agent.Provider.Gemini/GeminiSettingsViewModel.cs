using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.Gemini;

public sealed partial class GeminiSettingsViewModel : ObservableObject, IDisposable
{
    internal static IReadOnlyCollection<string> OwnedConfigurationKeys { get; } =
        [GeminiProviderConfiguration.ApiKeySecretKey, GeminiProviderConfiguration.UtilityModelKey];

    public GeminiSettingsViewModel(IPackageContext packageContext)
        : this(
            packageContext,
            new ProviderCredentialAccessor(packageContext.Secrets, GeminiProviderConfiguration.ApiKeySecretKey))
    {
    }

    internal GeminiSettingsViewModel(
        IPackageContext packageContext,
        ProviderCredentialAccessor credentials)
    {
        ApiKeySettings = new ApiKeySettingsState(
            credentials,
            "A stored API key enables Gemini chat and embeddings. Blank input retains the current key.",
            "AIza...",
            static hasCredential => hasCredential
                ? new ApiKeyStatus("Stored", "Gemini API-key chat and embeddings are ready.")
                : new ApiKeyStatus("Not stored", "Add an API key to enable Gemini chat and embeddings.", IsWarning: true));
        UtilityModelSettings = new UtilityModelSettingsState(
            packageContext,
            GeminiProviderConfiguration.UtilityModelKey,
            GeminiProviderConfiguration.DefaultUtilityModelId,
            GeminiModelCatalog.UtilityModelOptions.Select(option => (option.Value, option.Label)));
    }

    internal ApiKeySettingsState ApiKeySettings { get; }

    internal UtilityModelSettingsState UtilityModelSettings { get; }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        await ApiKeySettings.SaveCredentialAsync();
        await UtilityModelSettings.SaveUtilityModelAsync();
    }

    public void Dispose()
    {
        ApiKeySettings.Dispose();
        UtilityModelSettings.Dispose();
    }
}
