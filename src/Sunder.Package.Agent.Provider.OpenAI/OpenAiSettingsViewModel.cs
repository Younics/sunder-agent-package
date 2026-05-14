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

    [ObservableProperty]
    private string? _apiKeyValue;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isAuthorizing;

    [ObservableProperty]
    private bool _canDisconnect;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanAuthorize));

    partial void OnIsAuthorizingChanged(bool value) => OnPropertyChanged(nameof(CanCancelAuthorization));

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            await SaveStateAsync();
            StatusText = HasStoredApiKey
                ? "OpenAI API key saved. It will be used for chat when API-key mode is active."
                : "OpenAI settings saved.";
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
        StatusText = "Opened auth.openai.com in your browser. Complete sign-in there, or cancel and retry if you closed the page.";
        try
        {
            await SaveStateAsync();
            await _codexConnectedAuthStrategy.EnsureAuthenticatedAsync(authorizationCts.Token);
            StatusText = "Authorization succeeded. ChatGPT Plus/Pro session is ready.";
            await RefreshStatusAsync();
        }
        catch (OperationCanceledException) when (authorizationCts.IsCancellationRequested)
        {
            StatusText = "Authorization was cancelled or timed out. Click Authorize to try again.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
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

        StatusText = "Cancelling browser authorization...";
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
        StatusText = "ChatGPT Plus/Pro session removed.";
        CanDisconnect = false;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task RefreshStatusAsync()
    {
        var session = _codexConnectedAuthStrategy.GetCachedSession();
        CanDisconnect = session is not null;
        var authMode = OpenAiAuthMode.GetSelected(_packageContext.Configuration);
        var codexStatus = session is null
            ? "ChatGPT Plus/Pro: not authorized. Click Authorize to sign in."
            : authMode == OpenAiAuthMode.CodexConnected
                ? $"ChatGPT Plus/Pro: authorized until {session.ExpiresAtUtc:O}. Codex-connected chat mode is active."
                : $"ChatGPT Plus/Pro: authorized until {session.ExpiresAtUtc:O}, but inactive because API-key mode is selected.";
        var apiKeyStatus = HasStoredApiKey
            ? authMode == OpenAiAuthMode.ApiKey
                ? "API key: stored. API-key chat mode and embeddings are ready."
                : "API key: stored. It is available for embeddings and inactive for chat while ChatGPT Plus/Pro mode is selected."
            : authMode == OpenAiAuthMode.ApiKey
                ? "API key: not stored. API-key chat mode and embeddings are unavailable until you add one."
                : "API key: not stored. Embeddings are unavailable until you add one.";
        StatusText = codexStatus + "\n\n" + apiKeyStatus;
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
