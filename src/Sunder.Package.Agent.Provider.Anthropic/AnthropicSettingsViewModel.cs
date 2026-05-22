using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.Anthropic;

public sealed partial class AnthropicSettingsViewModel : ObservableObject
{
    private readonly IPackageContext _packageContext;

    public AnthropicSettingsViewModel(IPackageContext packageContext)
    {
        _packageContext = packageContext;
        LoadSettings();
    }

    public ObservableCollection<AnthropicUtilityModelOption> UtilityModels { get; } =
    [
        new("anthropic/claude-opus-4-7", "Claude Opus 4.7"),
        new("anthropic/claude-sonnet-4-6", "Claude Sonnet 4.6"),
        new(AnthropicProviderConfiguration.DefaultUtilityModelId, "Claude Haiku 4.5"),
    ];

    public bool HasStoredApiKey => !string.IsNullOrWhiteSpace(_packageContext.Secrets.GetSecret("auth.apiKey"));

    public bool CanSaveSettings => !IsBusy;

    public bool IsApiKeyStatusWarning => !IsApiKeyStored;

    [ObservableProperty]
    private string? _apiKeyValue;

    [ObservableProperty]
    private AnthropicUtilityModelOption? _selectedUtilityModel;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _apiKeyStatusLabel = string.Empty;

    [ObservableProperty]
    private string _apiKeyStatusDetail = string.Empty;

    [ObservableProperty]
    private bool _isApiKeyStored;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanSaveSettings));

    partial void OnIsApiKeyStoredChanged(bool value) => OnPropertyChanged(nameof(IsApiKeyStatusWarning));

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        IsBusy = true;
        try
        {
            await SaveStateAsync();
            OnPropertyChanged(nameof(HasStoredApiKey));
            RefreshStatus();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void LoadSettings()
    {
        ApiKeyValue = null;
        SelectedUtilityModel = ResolveUtilityModelOption(
            _packageContext.Storage.State.GetValue(AnthropicProviderConfiguration.UtilityModelKey)
            ?? _packageContext.Configuration.GetValue(AnthropicProviderConfiguration.UtilityModelKey)
            ?? AnthropicProviderConfiguration.DefaultUtilityModelId
        );
        RefreshStatus();
    }

    private async Task SaveStateAsync()
    {
        if (!string.IsNullOrWhiteSpace(ApiKeyValue))
        {
            _packageContext.Secrets.SetSecret("auth.apiKey", ApiKeyValue.Trim());
            ApiKeyValue = null;
        }

        await _packageContext.Storage.State.SetValueAsync(
            AnthropicProviderConfiguration.UtilityModelKey,
            SelectedUtilityModel?.ModelId ?? AnthropicProviderConfiguration.DefaultUtilityModelId
        );
    }

    private void RefreshStatus()
    {
        IsApiKeyStored = HasStoredApiKey;
        if (IsApiKeyStored)
        {
            ApiKeyStatusLabel = "Stored";
            ApiKeyStatusDetail = "Anthropic API-key chat is ready.";
        }
        else
        {
            ApiKeyStatusLabel = "Not stored";
            ApiKeyStatusDetail = "Add an API key to enable Claude chat.";
        }
    }

    private AnthropicUtilityModelOption ResolveUtilityModelOption(string? modelId) =>
        UtilityModels.FirstOrDefault(option => string.Equals(option.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
        ?? UtilityModels.First(option => option.ModelId == AnthropicProviderConfiguration.DefaultUtilityModelId);
}

public sealed record AnthropicUtilityModelOption(string ModelId, string DisplayName);
