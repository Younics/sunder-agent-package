using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.Gemini;

public sealed partial class GeminiSettingsViewModel : ObservableObject
{
    private readonly IPackageContext _packageContext;

    public GeminiSettingsViewModel(IPackageContext packageContext)
    {
        _packageContext = packageContext;
        LoadSettings();
    }

    public ObservableCollection<GeminiUtilityModelOption> UtilityModels { get; } =
        [.. GeminiModelCatalog.UtilityModelOptions.Select(option => new GeminiUtilityModelOption(option.Value, option.Label))];

    public bool HasStoredApiKey => !string.IsNullOrWhiteSpace(_packageContext.Secrets.GetSecret("auth.apiKey"));

    public bool CanSaveSettings => !IsBusy;

    public bool IsApiKeyStatusWarning => !IsApiKeyStored;

    [ObservableProperty]
    private string? _apiKeyValue;

    [ObservableProperty]
    private GeminiUtilityModelOption? _selectedUtilityModel;

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
            _packageContext.Storage.State.GetValue(GeminiProviderConfiguration.UtilityModelKey)
            ?? _packageContext.Configuration.GetValue(GeminiProviderConfiguration.UtilityModelKey)
            ?? GeminiProviderConfiguration.DefaultUtilityModelId
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
            GeminiProviderConfiguration.UtilityModelKey,
            SelectedUtilityModel?.ModelId ?? GeminiProviderConfiguration.DefaultUtilityModelId
        );
    }

    private void RefreshStatus()
    {
        IsApiKeyStored = HasStoredApiKey;
        if (IsApiKeyStored)
        {
            ApiKeyStatusLabel = "Stored";
            ApiKeyStatusDetail = "Gemini API-key chat and embeddings are ready.";
        }
        else
        {
            ApiKeyStatusLabel = "Not stored";
            ApiKeyStatusDetail = "Add an API key to enable Gemini chat and embeddings.";
        }
    }

    private GeminiUtilityModelOption ResolveUtilityModelOption(string? modelId) =>
        UtilityModels.FirstOrDefault(option => string.Equals(option.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
        ?? UtilityModels.First(option => option.ModelId == GeminiProviderConfiguration.DefaultUtilityModelId);
}

public sealed record GeminiUtilityModelOption(string ModelId, string DisplayName);
