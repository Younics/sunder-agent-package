using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.LMStudio;

public sealed partial class LMStudioSettingsViewModel : ObservableObject
{
    private readonly IPackageContext _packageContext;

    public LMStudioSettingsViewModel(IPackageContext packageContext)
    {
        _packageContext = packageContext;
        LoadSettings();
    }

    public bool HasStoredApiKey => !string.IsNullOrWhiteSpace(_packageContext.Secrets.GetSecret("connection.apiKey"));

    public bool CanSaveSettings => !IsBusy;

    public bool IsConnectionStatusWarning => !IsConnectionConfigured;

    public bool IsApiKeyStatusWarning => !IsApiKeyStored;

    [ObservableProperty]
    private string _baseUrl = LMStudioProviderConfiguration.DefaultBaseUrl;

    [ObservableProperty]
    private string? _apiKeyValue;

    [ObservableProperty]
    private string _utilityModelId = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _connectionStatusLabel = string.Empty;

    [ObservableProperty]
    private string _connectionStatusDetail = string.Empty;

    [ObservableProperty]
    private bool _isConnectionConfigured;

    [ObservableProperty]
    private string _apiKeyStatusLabel = string.Empty;

    [ObservableProperty]
    private string _apiKeyStatusDetail = string.Empty;

    [ObservableProperty]
    private bool _isApiKeyStored;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanSaveSettings));

    partial void OnIsConnectionConfiguredChanged(bool value) => OnPropertyChanged(nameof(IsConnectionStatusWarning));

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
        BaseUrl = _packageContext.Storage.State.GetValue("connection.baseUrl")
            ?? _packageContext.Configuration.GetValue("connection.baseUrl")
            ?? LMStudioProviderConfiguration.DefaultBaseUrl;
        ApiKeyValue = null;
        UtilityModelId = _packageContext.Storage.State.GetValue(LMStudioProviderConfiguration.UtilityModelKey)
            ?? _packageContext.Configuration.GetValue(LMStudioProviderConfiguration.UtilityModelKey)
            ?? string.Empty;
        RefreshStatus();
    }

    private async Task SaveStateAsync()
    {
        var baseUrl = NormalizeBaseUrl(BaseUrl);
        await _packageContext.Storage.State.SetValueAsync("connection.baseUrl", baseUrl);
        BaseUrl = baseUrl;

        if (!string.IsNullOrWhiteSpace(ApiKeyValue))
        {
            _packageContext.Secrets.SetSecret("connection.apiKey", ApiKeyValue.Trim());
            ApiKeyValue = null;
        }

        var utilityModelId = UtilityModelId.Trim();
        if (string.IsNullOrWhiteSpace(utilityModelId))
        {
            await _packageContext.Storage.State.DeleteValueAsync(LMStudioProviderConfiguration.UtilityModelKey);
            UtilityModelId = string.Empty;
        }
        else
        {
            await _packageContext.Storage.State.SetValueAsync(
                LMStudioProviderConfiguration.UtilityModelKey,
                utilityModelId.StartsWith("lmstudio/", StringComparison.OrdinalIgnoreCase)
                    ? utilityModelId
                    : $"lmstudio/{utilityModelId}"
            );
            UtilityModelId = utilityModelId;
        }
    }

    private void RefreshStatus()
    {
        IsConnectionConfigured = !string.IsNullOrWhiteSpace(BaseUrl);
        ConnectionStatusLabel = IsConnectionConfigured ? "Configured" : "Missing";
        ConnectionStatusDetail = IsConnectionConfigured
            ? "LM Studio will be contacted at the configured OpenAI-compatible endpoint."
            : "Set a base URL before using LM Studio models.";

        IsApiKeyStored = HasStoredApiKey;
        if (IsApiKeyStored)
        {
            ApiKeyStatusLabel = "Stored";
            ApiKeyStatusDetail = "A connection API key is stored and will be sent to LM Studio.";
        }
        else
        {
            ApiKeyStatusLabel = "Optional";
            ApiKeyStatusDetail = "Leave the API key blank unless your LM Studio endpoint requires one.";
        }
    }

    private static string NormalizeBaseUrl(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? LMStudioProviderConfiguration.DefaultBaseUrl
            : value.Trim().TrimEnd('/');
}
