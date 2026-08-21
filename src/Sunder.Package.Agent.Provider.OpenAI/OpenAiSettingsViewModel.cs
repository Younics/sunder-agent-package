using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Notifications;

namespace Sunder.Package.Agent.Provider.OpenAI;

public sealed partial class OpenAiSettingsViewModel : ObservableObject,
    IPackageViewNavigationPreparationTarget,
    IDisposable
{
    private readonly IPackageContext _packageContext;
    private readonly OpenAiAuthPresentationService _authPresentation;
    private readonly PackageCallbackFlowRunner _callbackFlow;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly PresentationOperationObserver _operations;
    private readonly IPresentationDispatcher _uiDispatcher;
    private readonly object _initializationSyncRoot = new();
    private Task? _initialization;
    private int _authorizationActive;
    private bool _disposed;
    private long _statusGeneration;
    private long _authModeIntentRevision;

    private static readonly PresentationOperation SaveSettingsOperation = new(
        "openai.save-settings",
        "OpenAI settings could not be saved. Current authentication state was refreshed.");
    private static readonly PresentationOperation SaveAuthModeOperation = new(
        "openai.save-auth-mode",
        "The OpenAI authentication mode could not be saved. Authentication state was refreshed.");
    private static readonly PresentationOperation AuthorizeOperation = new(
        "openai.authorize",
        "OpenAI authorization could not be completed. Authentication state was refreshed.");
    private static readonly PresentationOperation DisconnectOperation = new(
        "openai.disconnect",
        "The OpenAI account could not be disconnected. Authentication state was refreshed.");
    private static readonly PresentationOperation RefreshAuthOperation = new(
        "openai.refresh-auth",
        "OpenAI authentication state could not be refreshed.");

    internal static IReadOnlyCollection<string> OwnedConfigurationKeys { get; } =
        [OpenAiAuthMode.ConfigurationKey, OpenAiProviderConfiguration.ApiKeySecretKey, OpenAiProviderConfiguration.UtilityModelKey];

    internal OpenAiSettingsViewModel(
        IPackageContext packageContext,
        IOpenAiOwnerManagementClient management)
        : this(
            packageContext,
            new OpenAiAuthPresentationService(management),
            new PackageCallbackFlowRunner(packageContext.Callbacks),
            new ProviderCredentialOwnerClient(management))
    {
    }

    internal OpenAiSettingsViewModel(
        IPackageContext packageContext,
        OpenAiAuthPresentationService authPresentation,
        IProviderCredentialOwnerClient credentials)
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
        IProviderCredentialOwnerClient credentials)
        : this(
            packageContext,
            authPresentation,
            callbackFlow,
            credentials,
            PresentationOperationObserver.CreateUnreported("OpenAI settings"))
    {
    }

    internal OpenAiSettingsViewModel(
        IPackageContext packageContext,
        OpenAiAuthPresentationService authPresentation,
        PackageCallbackFlowRunner callbackFlow,
        IProviderCredentialOwnerClient credentials,
        PresentationOperationObserver operationObserver,
        IPresentationDispatcher? uiDispatcher = null)
    {
        _packageContext = packageContext;
        _authPresentation = authPresentation;
        _callbackFlow = callbackFlow;
        _operations = operationObserver;
        _uiDispatcher = uiDispatcher ?? PresentationDispatcher.Capture();
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
            initialization = _initialization ??= InitializeCoreAsync(_lifetimeCancellation.Token);
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
        await RefreshStatusCoreAsync(cancellationToken);
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
        Interlocked.Increment(ref _authModeIntentRevision);
        OnPropertyChanged(nameof(CanAuthorize));
        AuthorizeCommand.NotifyCanExecuteChanged();
        _operations.Submit(
            RefreshAuthOperation,
            RefreshStatusCoreAsync,
            ReconcileAuthStatusAsync);
    }

    [RelayCommand]
    private Task SaveAsync()
        => _operations.RunAsync(
            SaveSettingsOperation,
            SaveCoreAsync,
            ReconcileAuthStatusAsync);

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        await SaveAuthModeValueAsync(
            SelectedAuthMode?.ModeId ?? OpenAiAuthMode.CodexConnected,
            cancellationToken);
        await ApiKeySettings.SaveCredentialAsync();
        await UtilityModelSettings.SaveUtilityModelAsync();
        await RefreshStatusCoreAsync(cancellationToken);
    }

    [RelayCommand]
    private Task SaveAuthModeAsync()
        => _operations.RunAsync(
            SaveAuthModeOperation,
            SaveAuthModeCoreAsync,
            ReconcileAuthStatusAsync);

    private async Task SaveAuthModeCoreAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        var mode = SelectedAuthMode?.ModeId ?? OpenAiAuthMode.CodexConnected;
        var intentRevision = Volatile.Read(ref _authModeIntentRevision);
        IsBusy = true;
        try
        {
            await SaveAuthModeValueAsync(mode, cancellationToken);
            if (intentRevision == Volatile.Read(ref _authModeIntentRevision))
            {
                await RefreshStatusCoreAsync(cancellationToken);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAuthorize), AllowConcurrentExecutions = false)]
    private Task AuthorizeAsync(CancellationToken cancellationToken)
        => _operations.RunAsync(
            AuthorizeOperation,
            AuthorizeCoreAsync,
            ReconcileAuthorizationAsync,
            cancellationToken);

    private async Task AuthorizeCoreAsync(CancellationToken cancellationToken)
    {
        if (!CanAuthorize || Interlocked.CompareExchange(ref _authorizationActive, 1, 0) != 0)
        {
            return;
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
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
            try
            {
                await _callbackFlow.RunAsync(
                    PackageCallbackHandlerIds.Authentication,
                    null,
                    "OpenAI ChatGPT authorization",
                    lifetime.Token);
            }
            catch (InvalidOperationException exception) when (!PresentationExceptionPolicy.IsFatal(exception))
            {
                throw new PresentationOperationException(
                    "OpenAI authorization did not complete.",
                    exception);
            }
            await RefreshStatusCoreAsync(lifetime.Token);
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
    private Task DisconnectAsync()
        => _operations.RunAsync(
            DisconnectOperation,
            DisconnectCoreAsync,
            ReconcileAuthStatusAsync);

    private async Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        var disconnectGeneration = Interlocked.Increment(ref _statusGeneration);
        await _authPresentation.DisconnectAsync(cancellationToken);
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
    private Task RefreshStatusAsync(CancellationToken cancellationToken = default)
        => _operations.RunAsync(
            RefreshAuthOperation,
            RefreshStatusCoreAsync,
            ReconcileAuthStatusAsync,
            cancellationToken);

    private async Task RefreshStatusCoreAsync(CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        cancellationToken = lifetime.Token;
        var statusGeneration = Interlocked.Increment(ref _statusGeneration);
        var authMode = SelectedAuthMode?.ModeId
            ?? await OpenAiAuthMode.GetSelectedAsync(_packageContext.Settings, cancellationToken);
        var status = await _authPresentation.GetStatusAsync(cancellationToken);

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

    private Task ReconcileAuthStatusAsync(CancellationToken cancellationToken)
        => InvokeOnUiThreadAsync(() => ReconcileAuthStatusCoreAsync(cancellationToken));

    private async Task ReconcileAuthStatusCoreAsync(CancellationToken cancellationToken)
    {
        if (!_disposed)
        {
            Interlocked.Increment(ref _statusGeneration);
            IsCodexConnected = false;
            IsCodexStatusError = true;
            CodexStatusLabel = "Authentication unavailable";
            CodexStatusDetail = "OpenAI authentication state could not be confirmed. Try again.";
        }

        await RefreshStatusCoreAsync(cancellationToken);
    }

    private Task ReconcileAuthorizationAsync(
        Exception exception,
        CancellationToken cancellationToken)
        => InvokeOnUiThreadAsync(() => ReconcileAuthorizationCoreAsync(exception, cancellationToken));

    private async Task ReconcileAuthorizationCoreAsync(
        Exception exception,
        CancellationToken cancellationToken)
    {
        var failure = exception.InnerException ?? exception;
        try
        {
            await RefreshStatusCoreAsync(cancellationToken);
        }
        finally
        {
            if (!_disposed && !IsCodexConnected)
            {
                Interlocked.Increment(ref _statusGeneration);
                IsCodexStatusError = true;
                CodexStatusLabel = "Authorization failed";
                CodexStatusDetail = failure.Message;
            }
        }
    }

    private async Task InvokeOnUiThreadAsync(Func<Task> action)
    {
        Task operation = Task.CompletedTask;
        await _uiDispatcher.InvokeAsync(() => operation = action()).ConfigureAwait(false);
        await operation.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _statusGeneration);
        _operations.Dispose();
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        ApiKeySettings.Dispose();
        UtilityModelSettings.Dispose();
    }

    private Task SaveAuthModeValueAsync(
        string mode,
        CancellationToken cancellationToken = default)
        => _packageContext.Settings.SetValueAsync(
            OpenAiAuthMode.ConfigurationKey,
            mode,
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
        => OpenAiProviderConfiguration.NormalizeLegacyUtilityModelId(modelId);
}

public sealed record OpenAiAuthModeOption(string ModeId, string DisplayName);
