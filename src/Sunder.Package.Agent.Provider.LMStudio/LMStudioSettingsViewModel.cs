using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.LMStudio;

public sealed partial class LMStudioSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IPackageContext _packageContext;

    internal static IReadOnlyCollection<string> OwnedConfigurationKeys { get; } =
    [
        LMStudioProviderConfiguration.BaseUrlKey,
        LMStudioProviderConfiguration.ApiKeyKey,
        LMStudioProviderConfiguration.UtilityModelKey,
    ];

    public LMStudioSettingsViewModel(IPackageContext packageContext)
        : this(
            packageContext,
            new ProviderCredentialAccessor(packageContext.Secrets, LMStudioProviderConfiguration.ApiKeyKey))
    {
    }

    internal LMStudioSettingsViewModel(
        IPackageContext packageContext,
        ProviderCredentialAccessor credentials)
    {
        _packageContext = packageContext;
        ApiKeySettings = new ApiKeySettingsState(
            credentials,
            "The bearer key is optional. Blank input retains a stored key; use Clear Stored Key to remove it.",
            "lm-studio-key",
            static hasCredential => hasCredential
                ? new ApiKeyStatus("Stored", "A bearer key is stored and will be sent to LM Studio.")
                : new ApiKeyStatus("Optional", "No bearer key will be sent to the configured local endpoint."));
        RefreshConnectionStatus();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        BaseUrl = await _packageContext.Storage.State.GetValueAsync(LMStudioProviderConfiguration.BaseUrlKey, cancellationToken)
            ?? await _packageContext.Configuration.GetValueAsync(LMStudioProviderConfiguration.BaseUrlKey, cancellationToken)
            ?? LMStudioProviderConfiguration.DefaultBaseUrl;
        UtilityModelId = await _packageContext.Storage.State.GetValueAsync(LMStudioProviderConfiguration.UtilityModelKey, cancellationToken)
            ?? await _packageContext.Configuration.GetValueAsync(LMStudioProviderConfiguration.UtilityModelKey, cancellationToken)
            ?? string.Empty;
        await ApiKeySettings.RefreshCredentialStatusAsync(cancellationToken);
        RefreshConnectionStatus();
    }

    internal ApiKeySettingsState ApiKeySettings { get; }

    public bool CanSaveSettings => !IsBusy;

    public bool IsConnectionStatusWarning => !IsConnectionConfigured && !IsConnectionStatusError;

    [ObservableProperty]
    private string _baseUrl = LMStudioProviderConfiguration.DefaultBaseUrl;

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
    private bool _isConnectionStatusError;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanSaveSettings));

    partial void OnIsConnectionConfiguredChanged(bool value) => OnPropertyChanged(nameof(IsConnectionStatusWarning));

    partial void OnIsConnectionStatusErrorChanged(bool value) => OnPropertyChanged(nameof(IsConnectionStatusWarning));

    partial void OnBaseUrlChanged(string value) => RefreshConnectionStatus();

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (!await SaveConnectionCoreAsync())
        {
            return;
        }

        await ApiKeySettings.SaveCredentialAsync();
        await SaveUtilityModelCoreAsync();
    }

    [RelayCommand]
    private async Task SaveConnectionAsync() => await SaveConnectionCoreAsync();

    [RelayCommand]
    private async Task SaveUtilityModelAsync() => await SaveUtilityModelCoreAsync();

    public void Dispose() => ApiKeySettings.Dispose();

    private async Task<bool> SaveConnectionCoreAsync()
    {
        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        try
        {
            if (!LMStudioConnectionOptions.TryNormalizeBaseUrl(
                    BaseUrl,
                    out _,
                    out var normalizedBaseUrl,
                    out var validationError))
            {
                SetInvalidConnectionStatus(validationError);
                return false;
            }

            await _packageContext.Storage.State.SetValueAsync(
                LMStudioProviderConfiguration.BaseUrlKey,
                normalizedBaseUrl);
            BaseUrl = normalizedBaseUrl;
            RefreshConnectionStatus();
            return true;
        }
        catch (Exception ex)
        {
            SetConnectionErrorStatus($"Connection settings could not be saved: {ex.Message}");
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveUtilityModelCoreAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var utilityModelId = UtilityModelId.Trim();
            if (string.IsNullOrWhiteSpace(utilityModelId))
            {
                await _packageContext.Storage.State.DeleteValueAsync(LMStudioProviderConfiguration.UtilityModelKey);
                UtilityModelId = string.Empty;
                return;
            }

            var normalizedModelId = utilityModelId.StartsWith("lmstudio/", StringComparison.OrdinalIgnoreCase)
                ? utilityModelId
                : $"lmstudio/{utilityModelId}";
            await _packageContext.Storage.State.SetValueAsync(
                LMStudioProviderConfiguration.UtilityModelKey,
                normalizedModelId);
            UtilityModelId = normalizedModelId;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshConnectionStatus()
    {
        if (!LMStudioConnectionOptions.TryNormalizeBaseUrl(
                BaseUrl,
                out _,
                out _,
                out var validationError))
        {
            SetInvalidConnectionStatus(validationError);
            return;
        }

        IsConnectionConfigured = true;
        IsConnectionStatusError = false;
        ConnectionStatusLabel = "Configured";
        ConnectionStatusDetail = "LM Studio will be contacted at the configured OpenAI-compatible endpoint.";
    }

    private void SetInvalidConnectionStatus(string validationError)
    {
        IsConnectionConfigured = false;
        IsConnectionStatusError = false;
        ConnectionStatusLabel = "Invalid";
        ConnectionStatusDetail = validationError;
    }

    private void SetConnectionErrorStatus(string detail)
    {
        IsConnectionConfigured = false;
        IsConnectionStatusError = true;
        ConnectionStatusLabel = "Save failed";
        ConnectionStatusDetail = detail;
    }
}
