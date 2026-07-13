using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.Anthropic;

public sealed partial class AnthropicSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ApiKeyUtilitySettingsState _settings;

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
        _settings = new ApiKeyUtilitySettingsState(
            packageContext,
            credentials,
            "A stored API key is used for Claude chat. Blank input retains the current key.",
            "sk-ant-...",
            static hasCredential => hasCredential
                ? new ApiKeyStatus("Stored", "Anthropic API-key chat is ready.")
                : new ApiKeyStatus("Not stored", "Add an API key to enable Claude chat.", IsWarning: true),
            AnthropicProviderConfiguration.UtilityModelSelection);
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
