using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.Anthropic;

public sealed partial class AnthropicSettingsViewModel : ObservableObject, IDisposable
{
    internal static IReadOnlyCollection<string> OwnedConfigurationKeys { get; } =
        [AnthropicProviderConfiguration.ApiKeySecretKey, AnthropicProviderConfiguration.UtilityModelKey];

    public AnthropicSettingsViewModel(IPackageContext packageContext)
        : this(
            packageContext,
            new ProviderCredentialAccessor(packageContext.Secrets, AnthropicProviderConfiguration.ApiKeySecretKey))
    {
    }

    internal AnthropicSettingsViewModel(
        IPackageContext packageContext,
        ProviderCredentialAccessor credentials)
    {
        ApiKeySettings = new ApiKeySettingsState(
            credentials,
            "A stored API key is used for Claude chat. Blank input retains the current key.",
            "sk-ant-...",
            static hasCredential => hasCredential
                ? new ApiKeyStatus("Stored", "Anthropic API-key chat is ready.")
                : new ApiKeyStatus("Not stored", "Add an API key to enable Claude chat.", IsWarning: true));
        UtilityModelSettings = new UtilityModelSettingsState(
            packageContext,
            AnthropicProviderConfiguration.UtilityModelKey,
            AnthropicProviderConfiguration.DefaultUtilityModelId,
            AnthropicModelCatalog.UtilityModelOptions.Select(option => (option.Value, option.Label)));
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
