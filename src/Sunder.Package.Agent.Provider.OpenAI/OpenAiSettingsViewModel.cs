using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Provider.OpenAI.Auth;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.OpenAI;

public sealed partial class OpenAiSettingsViewModel : ObservableObject
{
    private readonly IPackageContext _packageContext;
    private readonly CodexConnectedAuthStrategy _codexConnectedAuthStrategy;

    public OpenAiSettingsViewModel(
        IPackageContext packageContext,
        CodexConnectedAuthStrategy codexConnectedAuthStrategy)
    {
        _packageContext = packageContext;
        _codexConnectedAuthStrategy = codexConnectedAuthStrategy;
        LoadSettings();
    }

    public bool HasStoredApiKey => !string.IsNullOrWhiteSpace(_packageContext.Secrets.GetSecret("auth.apiKey"));

    [ObservableProperty]
    private string? _apiKeyValue;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _canDisconnect;

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
        IsBusy = true;
        try
        {
            await SaveStateAsync();
            await _codexConnectedAuthStrategy.EnsureAuthenticatedAsync(cancellationToken: default);
            StatusText = "Authorization succeeded. ChatGPT Plus/Pro session is ready.";
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
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
