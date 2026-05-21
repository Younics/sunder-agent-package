using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Provider.OpenAI.Auth;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.OpenAI;

public sealed partial class OpenAiSettingsViewModel : ObservableObject
{
    private static readonly TimeSpan AuthorizationTimeout = TimeSpan.FromMinutes(5);
    private readonly IPackageContext _packageContext;
    private readonly CodexConnectedAuthStrategy _codexConnectedAuthStrategy;
    private CancellationTokenSource? _authorizationCts;

    public OpenAiSettingsViewModel(
        IPackageContext packageContext,
        CodexConnectedAuthStrategy codexConnectedAuthStrategy)
    {
        _packageContext = packageContext;
        _codexConnectedAuthStrategy = codexConnectedAuthStrategy;
        LoadSettings();
    }

    public bool HasStoredApiKey => !string.IsNullOrWhiteSpace(_packageContext.Secrets.GetSecret("auth.apiKey"));

    public bool CanAuthorize => !IsBusy;

    public bool CanCancelAuthorization => IsAuthorizing;

    public bool CanSaveApiKey => !IsBusy;

    public bool IsCodexStatusWarning => IsAuthorizing || (!IsCodexConnected && !IsCodexStatusError);

    public bool IsApiKeyStatusWarning => !IsApiKeyStored;

    [ObservableProperty]
    private string? _apiKeyValue;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isAuthorizing;

    [ObservableProperty]
    private bool _canDisconnect;

    [ObservableProperty]
    private string _codexStatusLabel = string.Empty;

    [ObservableProperty]
    private string _codexStatusDetail = string.Empty;

    [ObservableProperty]
    private bool _isCodexConnected;

    [ObservableProperty]
    private bool _isCodexStatusError;

    [ObservableProperty]
    private string _apiKeyStatusLabel = string.Empty;

    [ObservableProperty]
    private string _apiKeyStatusDetail = string.Empty;

    [ObservableProperty]
    private bool _isApiKeyStored;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAuthorize));
        OnPropertyChanged(nameof(CanSaveApiKey));
    }

    partial void OnIsAuthorizingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCancelAuthorization));
        OnPropertyChanged(nameof(IsCodexStatusWarning));
    }

    partial void OnIsCodexConnectedChanged(bool value) => OnPropertyChanged(nameof(IsCodexStatusWarning));

    partial void OnIsCodexStatusErrorChanged(bool value) => OnPropertyChanged(nameof(IsCodexStatusWarning));

    partial void OnIsApiKeyStoredChanged(bool value) => OnPropertyChanged(nameof(IsApiKeyStatusWarning));

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            await SaveStateAsync();
            OnPropertyChanged(nameof(HasStoredApiKey));
            await RefreshStatusAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AuthorizeAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var authorizationCts = new CancellationTokenSource(AuthorizationTimeout);
        _authorizationCts = authorizationCts;
        IsBusy = true;
        IsAuthorizing = true;
        IsCodexStatusError = false;
        IsCodexConnected = false;
        CodexStatusLabel = "Authorizing...";
        CodexStatusDetail = "Opened auth.openai.com in your browser. Complete sign-in there; Sunder will finish authorization after the callback.";
        try
        {
            await SaveStateAsync();
            await _codexConnectedAuthStrategy.EnsureAuthenticatedAsync(authorizationCts.Token);
            await RefreshStatusAsync();
        }
        catch (OperationCanceledException) when (authorizationCts.IsCancellationRequested)
        {
            IsCodexStatusError = true;
            IsCodexConnected = false;
            CodexStatusLabel = "Authorization canceled";
            CodexStatusDetail = "Authorization was cancelled or timed out. Click Authorize to try again.";
        }
        catch (Exception ex)
        {
            IsCodexStatusError = true;
            IsCodexConnected = false;
            CodexStatusLabel = "Authorization failed";
            CodexStatusDetail = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_authorizationCts, authorizationCts))
            {
                _authorizationCts = null;
            }

            authorizationCts.Dispose();
            IsAuthorizing = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelAuthorization()
    {
        if (!IsAuthorizing)
        {
            return;
        }

        CodexStatusLabel = "Canceling...";
        CodexStatusDetail = "Canceling browser authorization.";
        try
        {
            _authorizationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    [RelayCommand]
    private Task DisconnectAsync()
    {
        _codexConnectedAuthStrategy.ClearSession();
        IsCodexConnected = false;
        IsCodexStatusError = false;
        CodexStatusLabel = "Not connected";
        CodexStatusDetail = "ChatGPT Plus/Pro session removed. Click Authorize to sign in again.";
        CanDisconnect = false;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task RefreshStatusAsync()
    {
        var session = _codexConnectedAuthStrategy.GetCachedSession();
        CanDisconnect = session is not null;
        var authMode = OpenAiAuthMode.GetSelected(_packageContext.Configuration);
        IsCodexStatusError = false;
        IsCodexConnected = session is not null;
        if (session is null)
        {
            CodexStatusLabel = "Not connected";
            CodexStatusDetail = "Authorize with ChatGPT Plus/Pro to use Codex-connected chat mode.";
        }
        else if (authMode == OpenAiAuthMode.CodexConnected)
        {
            CodexStatusLabel = "Connected, active";
            CodexStatusDetail = $"Authorized until {session.ExpiresAtUtc:O}. Codex-connected chat mode is active.";
        }
        else
        {
            CodexStatusLabel = "Connected";
            CodexStatusDetail = $"Authorized until {session.ExpiresAtUtc:O}. Inactive for chat while API-key mode is selected.";
        }

        IsApiKeyStored = HasStoredApiKey;
        if (IsApiKeyStored && authMode == OpenAiAuthMode.ApiKey)
        {
            ApiKeyStatusLabel = "Stored, active";
            ApiKeyStatusDetail = "API-key chat mode and embeddings are ready.";
        }
        else if (IsApiKeyStored)
        {
            ApiKeyStatusLabel = "Stored";
            ApiKeyStatusDetail = "Available for embeddings and for chat when API-key mode is selected.";
        }
        else if (authMode == OpenAiAuthMode.ApiKey)
        {
            ApiKeyStatusLabel = "Missing";
            ApiKeyStatusDetail = "API-key chat mode and embeddings are unavailable until you add one.";
        }
        else
        {
            ApiKeyStatusLabel = "Not stored";
            ApiKeyStatusDetail = "Add an API key to enable embeddings or API-key chat mode.";
        }

        return Task.CompletedTask;
    }

    private void LoadSettings()
    {
        ApiKeyValue = null;
        _ = RefreshStatusAsync();
    }

    private Task SaveStateAsync()
    {
        if (!string.IsNullOrWhiteSpace(ApiKeyValue))
        {
            _packageContext.Secrets.SetSecret("auth.apiKey", ApiKeyValue.Trim());
            ApiKeyValue = null;
        }

        return Task.CompletedTask;
    }
}
