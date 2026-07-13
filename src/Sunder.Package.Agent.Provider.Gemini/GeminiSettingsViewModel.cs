using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.Gemini;

public sealed partial class GeminiSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ApiKeyUtilitySettingsState _settings;

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
        _settings = new ApiKeyUtilitySettingsState(
            packageContext,
            credentials,
            "A stored API key enables Gemini chat and embeddings. Blank input retains the current key.",
            "AIza...",
            static hasCredential => hasCredential
                ? new ApiKeyStatus("Stored", "Gemini API-key chat and embeddings are ready.")
                : new ApiKeyStatus("Not stored", "Add an API key to enable Gemini chat and embeddings.", IsWarning: true),
            GeminiProviderConfiguration.UtilityModelSelection);
    }

    internal ApiKeySettingsState ApiKeySettings => _settings.ApiKey;

    internal UtilityModelSettingsState UtilityModelSettings => _settings.UtilityModel;

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        await _settings.SaveAsync();
    }

    public void Dispose()
    {
        _settings.Dispose();
    }
}
