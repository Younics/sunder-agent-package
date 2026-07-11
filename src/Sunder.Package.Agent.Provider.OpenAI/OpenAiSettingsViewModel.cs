using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Provider.OpenAI.Auth;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.OpenAI;

public sealed partial class OpenAiSettingsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan AuthorizationTimeout = TimeSpan.FromMinutes(5);
    private readonly IPackageContext _packageContext;
    private readonly CodexConnectedAuthStrategy _codexConnectedAuthStrategy;
    private CancellationTokenSource? _authorizationCts;
    private long _statusGeneration;

    internal static IReadOnlyCollection<string> OwnedConfigurationKeys { get; } =
        [OpenAiAuthMode.ConfigurationKey, OpenAiProviderConfiguration.ApiKeySecretKey, OpenAiProviderConfiguration.UtilityModelKey];

    public OpenAiSettingsViewModel(
        IPackageContext packageContext,
        CodexConnectedAuthStrategy codexConnectedAuthStrategy)
        : this(
            packageContext,
            codexConnectedAuthStrategy,
            new ProviderCredentialAccessor(packageContext.Secrets, OpenAiProviderConfiguration.ApiKeySecretKey))
    {
    }

    internal OpenAiSettingsViewModel(
        IPackageContext packageContext,
        CodexConnectedAuthStrategy codexConnectedAuthStrategy,
        ProviderCredentialAccessor credentials)
    {
        _packageContext = packageContext;
        _codexConnectedAuthStrategy = codexConnectedAuthStrategy;
        _selectedAuthMode = ResolveAuthModeOption(OpenAiAuthMode.CodexConnected);
        ApiKeySettings = new ApiKeySettingsState(
            credentials,
            "A stored API key enables embeddings and is used for API-key chat. Blank input retains the current key.",
            "sk-...",
            ResolveApiKeyStatus);
        UtilityModelSettings = new UtilityModelSettingsState(
            packageContext,
            OpenAiProviderConfiguration.UtilityModelKey,
            OpenAiProviderConfiguration.DefaultUtilityModelId,
            OpenAiProviderConfiguration.UtilityModelOptions.Select(option => (option.Value, option.Label)),
            NormalizeLegacyUtilityModelId);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var configuredMode = await _packageContext.Storage.State.GetValueAsync(OpenAiAuthMode.ConfigurationKey, cancellationToken)
            ?? await _packageContext.Configuration.GetValueAsync(OpenAiAuthMode.ConfigurationKey, cancellationToken);
        SelectedAuthMode = ResolveAuthModeOption(configuredMode);
        await ApiKeySettings.RefreshCredentialStatusAsync(cancellationToken);
        await UtilityModelSettings.InitializeAsync(cancellationToken);
        await RefreshStatusAsync(cancellationToken);
    }

    public ObservableCollection<OpenAiAuthModeOption> AuthModes { get; } =
    [
        new(OpenAiAuthMode.CodexConnected, "ChatGPT Plus/Pro"),
        new(OpenAiAuthMode.ApiKey, "API key"),
    ];

    internal ApiKeySettingsState ApiKeySettings { get; }

    internal UtilityModelSettingsState UtilityModelSettings { get; }

    public bool CanAuthorize => !IsBusy;

    public bool CanCancelAuthorization => IsAuthorizing;

    public bool CanSaveAuthMode => !IsBusy;

    public bool IsCodexStatusWarning => IsAuthorizing || (!IsCodexConnected && !IsCodexStatusError);

    [ObservableProperty]
    private OpenAiAuthModeOption? _selectedAuthMode;

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

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAuthorize));
        OnPropertyChanged(nameof(CanSaveAuthMode));
    }

    partial void OnIsAuthorizingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCancelAuthorization));
        OnPropertyChanged(nameof(IsCodexStatusWarning));
    }

    partial void OnIsCodexConnectedChanged(bool value) => OnPropertyChanged(nameof(IsCodexStatusWarning));

    partial void OnIsCodexStatusErrorChanged(bool value) => OnPropertyChanged(nameof(IsCodexStatusWarning));

    partial void OnSelectedAuthModeChanged(OpenAiAuthModeOption? value)
    {
        _ = RefreshStatusAsync();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        await SaveAuthModeValueAsync();
        await ApiKeySettings.SaveCredentialAsync();
        await UtilityModelSettings.SaveUtilityModelAsync();
        await RefreshStatusAsync();
    }

    [RelayCommand]
    private async Task SaveAuthModeAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await SaveAuthModeValueAsync();
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
        var authorizationGeneration = Interlocked.Increment(ref _statusGeneration);
        _authorizationCts = authorizationCts;
        IsBusy = true;
        IsAuthorizing = true;
        IsCodexStatusError = false;
        IsCodexConnected = false;
        CodexStatusLabel = "Authorizing...";
        CodexStatusDetail = "Opened auth.openai.com in your browser. Complete sign-in there; Sunder will finish authorization after the callback.";
        try
        {
            await SaveAuthModeValueAsync(authorizationCts.Token);
            await _codexConnectedAuthStrategy.EnsureAuthenticatedAsync(authorizationCts.Token);
            await RefreshStatusAsync(authorizationCts.Token);
        }
        catch (OperationCanceledException) when (authorizationCts.IsCancellationRequested)
        {
            if (authorizationGeneration == Volatile.Read(ref _statusGeneration))
            {
                IsCodexStatusError = true;
                IsCodexConnected = false;
                CodexStatusLabel = "Authorization canceled";
                CodexStatusDetail = "Authorization was canceled or timed out. Click Authorize to try again.";
            }
        }
        catch (Exception ex)
        {
            if (authorizationGeneration == Volatile.Read(ref _statusGeneration))
            {
                IsCodexStatusError = true;
                IsCodexConnected = false;
                CodexStatusLabel = "Authorization failed";
                CodexStatusDetail = ex.Message;
            }
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
    private async Task DisconnectAsync()
    {
        var disconnectGeneration = Interlocked.Increment(ref _statusGeneration);
        try
        {
            _authorizationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        await _codexConnectedAuthStrategy.ClearSessionAsync();
        if (disconnectGeneration != Volatile.Read(ref _statusGeneration))
        {
            return;
        }

        IsCodexConnected = false;
        IsCodexStatusError = false;
        CodexStatusLabel = "Not connected";
        CodexStatusDetail = "ChatGPT Plus/Pro session removed. Click Authorize to sign in again.";
        CanDisconnect = false;
    }

    [RelayCommand]
    private async Task RefreshStatusAsync(CancellationToken cancellationToken = default)
    {
        var statusGeneration = Interlocked.Increment(ref _statusGeneration);
        var authMode = SelectedAuthMode?.ModeId
            ?? await OpenAiAuthMode.GetSelectedAsync(_packageContext.Configuration, cancellationToken);
        OpenAiCodexSession? activeSession = null;
        Exception? refreshError = null;
        try
        {
            activeSession = await _codexConnectedAuthStrategy.TryEnsureAuthenticatedSilentlyAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            refreshError = ex;
        }

        if (statusGeneration != Volatile.Read(ref _statusGeneration))
        {
            return;
        }

        var cachedSession = await _codexConnectedAuthStrategy.GetCachedSessionAsync(cancellationToken);
        CanDisconnect = cachedSession is not null;
        IsCodexConnected = activeSession is not null;
        IsCodexStatusError = refreshError is not null || (cachedSession is not null && activeSession is null);
        if (activeSession is not null && authMode == OpenAiAuthMode.CodexConnected)
        {
            CodexStatusLabel = "Connected, active";
            CodexStatusDetail = $"Authorized until {activeSession.ExpiresAtUtc:O}. Codex-connected chat mode is active.";
        }
        else if (activeSession is not null)
        {
            CodexStatusLabel = "Connected";
            CodexStatusDetail = $"Authorized until {activeSession.ExpiresAtUtc:O}. Inactive for chat while API-key mode is selected.";
        }
        else if (cachedSession is not null)
        {
            CodexStatusLabel = "Session expired";
            CodexStatusDetail = refreshError is null
                ? "The cached ChatGPT session is expired or near expiry and could not be refreshed. Authorize again to reconnect."
                : $"The cached ChatGPT session could not be refreshed: {refreshError.Message}";
        }
        else
        {
            CodexStatusLabel = "Not connected";
            CodexStatusDetail = "Authorize with ChatGPT Plus/Pro to use Codex-connected chat mode.";
        }

        await ApiKeySettings.RefreshCredentialStatusAsync(cancellationToken);
    }

    public void Dispose()
    {
        try
        {
            _authorizationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        ApiKeySettings.Dispose();
        UtilityModelSettings.Dispose();
    }

    private Task SaveAuthModeValueAsync(CancellationToken cancellationToken = default)
        => _packageContext.Storage.State.SetValueAsync(
            OpenAiAuthMode.ConfigurationKey,
            SelectedAuthMode?.ModeId ?? OpenAiAuthMode.CodexConnected,
            cancellationToken);

    private ApiKeyStatus ResolveApiKeyStatus(bool hasCredential)
    {
        var authMode = SelectedAuthMode?.ModeId ?? OpenAiAuthMode.CodexConnected;
        if (hasCredential && authMode == OpenAiAuthMode.ApiKey)
        {
            return new ApiKeyStatus("Stored, active", "API-key chat mode and embeddings are ready.");
        }

        if (hasCredential)
        {
            return new ApiKeyStatus("Stored", "Available for embeddings and for chat when API-key mode is selected.");
        }

        return authMode == OpenAiAuthMode.ApiKey
            ? new ApiKeyStatus("Missing", "API-key chat mode and embeddings are unavailable until you add one.", IsWarning: true)
            : new ApiKeyStatus("Not stored", "Add an API key to enable embeddings or API-key chat mode.", IsWarning: true);
    }

    private OpenAiAuthModeOption ResolveAuthModeOption(string? modeId)
        => AuthModes.First(mode => mode.ModeId == (string.Equals(modeId, OpenAiAuthMode.ApiKey, StringComparison.OrdinalIgnoreCase)
            ? OpenAiAuthMode.ApiKey
            : OpenAiAuthMode.CodexConnected));

    internal static string? NormalizeLegacyUtilityModelId(string? modelId)
        => string.Equals(modelId?.Trim(), "openai/gpt-5.5-fast", StringComparison.OrdinalIgnoreCase)
            ? "openai/gpt-5.5"
            : modelId;
}

public sealed record OpenAiAuthModeOption(string ModeId, string DisplayName);
