using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Provider.OpenAI;

public sealed partial class OpenAiSettingsViewModel : ObservableObject,
    IPackageViewNavigationPreparationTarget,
    IDisposable
{
    private readonly IPackageContext _packageContext;
    private readonly OpenAiAuthPresentationService _authPresentation;
    private readonly PackageCallbackFlowRunner _callbackFlow;
    private readonly PresentationTaskScope _tasks = new();
    private readonly object _initializationSyncRoot = new();
    private Task? _initialization;
    private int _authorizationActive;
    private bool _disposed;
    private long _statusGeneration;

    internal static IReadOnlyCollection<string> OwnedConfigurationKeys { get; } =
        [OpenAiAuthMode.ConfigurationKey, OpenAiProviderConfiguration.ApiKeySecretKey, OpenAiProviderConfiguration.UtilityModelKey];

    public OpenAiSettingsViewModel(
        IPackageContext packageContext,
        IPackageRuntimeClient runtimeClient)
        : this(
            packageContext,
            new OpenAiAuthPresentationService(runtimeClient),
            new PackageCallbackFlowRunner(packageContext.Callbacks),
            new ProviderCredentialAppRuntimeGateway(runtimeClient))
    {
    }

    internal OpenAiSettingsViewModel(
        IPackageContext packageContext,
        OpenAiAuthPresentationService authPresentation,
        IProviderCredentialSettingsGateway credentials)
        : this(
            packageContext,
            authPresentation,
            new PackageCallbackFlowRunner(packageContext.Callbacks),
            credentials)
    {
    }

    internal OpenAiSettingsViewModel(
        IPackageContext packageContext,
        OpenAiAuthPresentationService authPresentation,
        PackageCallbackFlowRunner callbackFlow,
        IProviderCredentialSettingsGateway credentials)
    {
        _packageContext = packageContext;
        _authPresentation = authPresentation;
        _callbackFlow = callbackFlow;
        _selectedAuthMode = ResolveAuthModeOption(OpenAiAuthMode.CodexConnected);
        ApiKeySettings = new ApiKeySettingsState(
            credentials,
            "A stored API key enables embeddings and is used for API-key chat. Blank input retains the current key.",
            "sk-...",
            ResolveApiKeyStatus);
        UtilityModelSettings = new UtilityModelSettingsState(
            packageContext,
            OpenAiProviderConfiguration.UtilityModelSelection.ConfigurationKey,
            OpenAiProviderConfiguration.UtilityModelSelection.DefaultModelId,
            OpenAiProviderConfiguration.UtilityModelSelection.Options.Select(option => (option.Value, option.Label)),
            OpenAiProviderConfiguration.UtilityModelSelection.Normalize);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Task initialization;
        lock (_initializationSyncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            initialization = _initialization ??= InitializeCoreAsync(_tasks.CancellationToken);
        }

        return cancellationToken.CanBeCanceled
            ? initialization.WaitAsync(cancellationToken)
            : initialization;
    }

    public async ValueTask<bool> PrepareNavigationAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return true;
    }

    public ValueTask OnNavigationPresentedAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        var configuredMode = await _packageContext.Settings.GetValueAsync(OpenAiAuthMode.ConfigurationKey, cancellationToken);
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

    public bool CanSaveAuthMode => !IsBusy;

    public bool CanAuthorize => !_disposed
        && !IsBusy
        && _callbackFlow.IsAvailable
        && SelectedAuthMode?.ModeId == OpenAiAuthMode.CodexConnected;

    public bool CanDisconnectAction => CanDisconnect && !IsBusy;

    public string AuthorizationButtonLabel => IsAuthorizing
        ? "Authorizing..."
        : IsCodexConnected
            ? "Reauthorize with ChatGPT Plus/Pro"
            : "Authorize with ChatGPT Plus/Pro";

    public bool IsCodexStatusWarning => !IsCodexConnected && !IsCodexStatusError;

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
        OnPropertyChanged(nameof(CanSaveAuthMode));
        OnPropertyChanged(nameof(CanAuthorize));
        OnPropertyChanged(nameof(CanDisconnectAction));
        AuthorizeCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsAuthorizingChanged(bool value) => OnPropertyChanged(nameof(AuthorizationButtonLabel));

    partial void OnCanDisconnectChanged(bool value) => OnPropertyChanged(nameof(CanDisconnectAction));

    partial void OnIsCodexConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCodexStatusWarning));
        OnPropertyChanged(nameof(AuthorizationButtonLabel));
    }

    partial void OnIsCodexStatusErrorChanged(bool value) => OnPropertyChanged(nameof(IsCodexStatusWarning));

    partial void OnSelectedAuthModeChanged(OpenAiAuthModeOption? value)
    {
        OnPropertyChanged(nameof(CanAuthorize));
        AuthorizeCommand.NotifyCanExecuteChanged();
        _tasks.Run(RefreshStatusAsync);
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

    [RelayCommand(CanExecute = nameof(CanAuthorize), AllowConcurrentExecutions = false)]
    private async Task AuthorizeAsync(CancellationToken cancellationToken)
    {
        if (!CanAuthorize || Interlocked.CompareExchange(ref _authorizationActive, 1, 0) != 0)
        {
            return;
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _tasks.CancellationToken);
        Interlocked.Increment(ref _statusGeneration);
        IsBusy = true;
        IsAuthorizing = true;
        IsCodexConnected = false;
        IsCodexStatusError = false;
        CodexStatusLabel = "Waiting for authorization";
        CodexStatusDetail = "Complete ChatGPT sign-in in the browser window. Sunder will detect the callback automatically.";
        try
        {
            await _packageContext.Settings.SetValueAsync(
                OpenAiAuthMode.ConfigurationKey,
                OpenAiAuthMode.CodexConnected,
                lifetime.Token);
            await _callbackFlow.RunAsync(
                PackageCallbackHandlerIds.Authentication,
                null,
                "OpenAI ChatGPT authorization",
                lifetime.Token);
            await RefreshStatusAsync(lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            if (!_disposed)
            {
                Interlocked.Increment(ref _statusGeneration);
                IsCodexConnected = false;
                IsCodexStatusError = true;
                CodexStatusLabel = "Authorization canceled";
                CodexStatusDetail = "Authorization was canceled before it completed. Click Authorize to try again.";
            }
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                Interlocked.Increment(ref _statusGeneration);
                IsCodexConnected = false;
                IsCodexStatusError = true;
                CodexStatusLabel = "Authorization failed";
                CodexStatusDetail = ex.Message;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _authorizationActive, 0);
            if (!_disposed)
            {
                IsAuthorizing = false;
                IsBusy = false;
            }
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        var disconnectGeneration = Interlocked.Increment(ref _statusGeneration);
        await _authPresentation.DisconnectAsync();
        if (disconnectGeneration != Volatile.Read(ref _statusGeneration))
        {
            return;
        }

        IsCodexConnected = false;
        IsCodexStatusError = false;
        CodexStatusLabel = "Not connected";
        CodexStatusDetail = "ChatGPT Plus/Pro session removed. Click Authorize with ChatGPT Plus/Pro to sign in again.";
        CanDisconnect = false;
    }

    [RelayCommand]
    private async Task RefreshStatusAsync(CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _tasks.CancellationToken);
        cancellationToken = lifetime.Token;
        var statusGeneration = Interlocked.Increment(ref _statusGeneration);
        var authMode = SelectedAuthMode?.ModeId
            ?? await OpenAiAuthMode.GetSelectedAsync(_packageContext.Settings, cancellationToken);
        OpenAiAuthOperationResult status;
        try
        {
            status = await _authPresentation.GetStatusAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            status = new OpenAiAuthOperationResult(false, false, null, ex.Message);
        }

        if (statusGeneration != Volatile.Read(ref _statusGeneration))
        {
            return;
        }

        CanDisconnect = status.HasCachedSession;
        IsCodexConnected = status.IsConnected;
        IsCodexStatusError = status.ErrorMessage is not null || (status.HasCachedSession && !status.IsConnected);
        if (status.IsConnected && authMode == OpenAiAuthMode.CodexConnected)
        {
            CodexStatusLabel = "Connected, active";
            CodexStatusDetail = $"Authorized until {status.ExpiresAtUtc:O}. Codex-connected chat mode is active.";
        }
        else if (status.IsConnected)
        {
            CodexStatusLabel = "Connected";
            CodexStatusDetail = $"Authorized until {status.ExpiresAtUtc:O}. Inactive for chat while API-key mode is selected.";
        }
        else if (status.HasCachedSession)
        {
            CodexStatusLabel = "Session expired";
            CodexStatusDetail = status.ErrorMessage is null
                ? "The cached ChatGPT session is expired or near expiry and could not be refreshed. Authorize again to reconnect."
                : $"The cached ChatGPT session could not be refreshed: {status.ErrorMessage}";
        }
        else
        {
            CodexStatusLabel = "Not connected";
            CodexStatusDetail = _callbackFlow.IsAvailable
                ? "Select ChatGPT Plus/Pro mode, then click Authorize with ChatGPT Plus/Pro to connect."
                : "Authorization is unavailable in this host context. Open these settings in the Sunder App to connect ChatGPT Plus/Pro.";
        }

        await ApiKeySettings.RefreshCredentialStatusAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _statusGeneration);
        _tasks.Dispose();
        ApiKeySettings.Dispose();
        UtilityModelSettings.Dispose();
    }

    private Task SaveAuthModeValueAsync(CancellationToken cancellationToken = default)
        => _packageContext.Settings.SetValueAsync(
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
