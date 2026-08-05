using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Provider.LMStudio;

public sealed partial class LMStudioSettingsViewModel : ObservableObject,
    IPackageViewNavigationPreparationTarget,
    IDisposable
{
    private readonly IPackageContext _packageContext;
    private readonly IProviderCredentialSettingsGateway _credentials;
    private readonly ILogger _logger;
    private readonly object _initializationSyncRoot = new();
    private readonly SemaphoreSlim _navigationLoadGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _initialization;
    private bool _disposed;
    private long _loadGeneration;

    internal static IReadOnlyCollection<string> OwnedConfigurationKeys { get; } =
    [
        LMStudioProviderConfiguration.BaseUrlKey,
        LMStudioProviderConfiguration.ApiKeyKey,
        LMStudioProviderConfiguration.UtilityModelKey,
    ];

    public LMStudioSettingsViewModel(
        IPackageContext packageContext,
        IPackageRuntimeClient runtimeClient)
        : this(
            packageContext,
            new ProviderCredentialAppRuntimeGateway(runtimeClient))
    {
    }

    internal LMStudioSettingsViewModel(
        IPackageContext packageContext,
        IProviderCredentialSettingsGateway credentials)
    {
        _packageContext = packageContext;
        _credentials = credentials;
        _logger = packageContext.Logging.LoggerFactory.CreateLogger<LMStudioSettingsViewModel>();
        ApiKeySettings = new ApiKeySettingsState(
            credentials,
            "The bearer key is optional. Blank input retains a stored key; use Clear Stored Key to remove it.",
            "lm-studio-key",
            static hasCredential => hasCredential
                ? new ApiKeyStatus("Stored", "A bearer key is stored and will be sent to LM Studio.")
                : new ApiKeyStatus("Optional", "No bearer key will be sent to the configured local endpoint."));
        RefreshConnectionStatus();
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Task initialization;
        lock (_initializationSyncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialization is null
                || LoadState == LMStudioSettingsLoadState.Error && _initialization.IsCompleted)
            {
                _initialization = InitializeCoreAsync();
            }
            initialization = _initialization;
        }

        return cancellationToken.CanBeCanceled
            ? initialization.WaitAsync(cancellationToken)
            : initialization;
    }

    public async ValueTask<bool> PrepareNavigationAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        await _navigationLoadGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (LoadState == LMStudioSettingsLoadState.Ready)
            {
                await RefreshAsync(cancellationToken);
            }
            else
            {
                await InitializeAsync(cancellationToken);
            }
            return true;
        }
        finally
        {
            _navigationLoadGate.Release();
        }
    }

    public ValueTask OnNavigationPresentedAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private async Task InitializeCoreAsync()
    {
        await LoadSnapshotAsync(_lifetime.Token);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await LoadSnapshotAsync(linked.Token);
    }

    private async Task LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        var canceledState = LoadState == LMStudioSettingsLoadState.Ready
            ? LMStudioSettingsLoadState.Ready
            : LMStudioSettingsLoadState.Uninitialized;
        var generation = Interlocked.Increment(ref _loadGeneration);
        SetLoadState(LMStudioSettingsLoadState.Loading);
        try
        {
            var baseUrl = await _packageContext.Settings.GetValueAsync(
                    LMStudioProviderConfiguration.BaseUrlKey,
                    cancellationToken)
                ?? LMStudioProviderConfiguration.DefaultBaseUrl;
            var utilityModelId = await _packageContext.Settings.GetValueAsync(
                    LMStudioProviderConfiguration.UtilityModelKey,
                    cancellationToken)
                ?? string.Empty;
            var credentialStatus = await _credentials.GetStatusAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanApplyLoad(generation))
            {
                return;
            }

            BaseUrl = baseUrl;
            UtilityModelId = utilityModelId;
            ApiKeySettings.ApplyCredentialStatus(credentialStatus.HasStoredCredential);
            ClearRuntimeError();
            StatusText = string.Empty;
            RefreshConnectionStatus();
            SetLoadState(LMStudioSettingsLoadState.Ready);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (CanApplyLoad(generation))
            {
                SetLoadState(canceledState);
            }
            throw;
        }
        catch (PackageRuntimeInvocationException exception)
        {
            if (CanApplyLoad(generation))
            {
                ApplyRuntimeFailure(exception);
                SetLoadState(LMStudioSettingsLoadState.Error);
            }
        }
        catch (Exception exception)
        {
            if (CanApplyLoad(generation))
            {
                ApplyTransportFailure(exception);
                SetLoadState(LMStudioSettingsLoadState.Error);
            }
        }
    }

    internal ApiKeySettingsState ApiKeySettings { get; }

    public bool IsLoading => LoadState == LMStudioSettingsLoadState.Loading;

    public bool IsReady => LoadState == LMStudioSettingsLoadState.Ready;

    public bool IsError => LoadState == LMStudioSettingsLoadState.Error;

    public bool CanMutate => IsReady && !IsBusy && !_disposed;

    public bool CanSaveSettings => CanMutate;

    public bool IsConnectionStatusWarning => !IsConnectionConfigured && !IsConnectionStatusError;

    public bool HasRuntimeError => !string.IsNullOrWhiteSpace(RuntimeErrorCode);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading))]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyPropertyChangedFor(nameof(IsError))]
    [NotifyPropertyChangedFor(nameof(CanMutate))]
    [NotifyPropertyChangedFor(nameof(CanSaveSettings))]
    private LMStudioSettingsLoadState _loadState;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string? _runtimeErrorCode;

    [ObservableProperty]
    private string? _runtimeCorrelationId;

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

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanMutate));
        OnPropertyChanged(nameof(CanSaveSettings));
        SaveSettingsCommand.NotifyCanExecuteChanged();
        SaveConnectionCommand.NotifyCanExecuteChanged();
        SaveUtilityModelCommand.NotifyCanExecuteChanged();
    }

    partial void OnLoadStateChanged(LMStudioSettingsLoadState value)
    {
        RetryInitializationCommand.NotifyCanExecuteChanged();
        SaveSettingsCommand.NotifyCanExecuteChanged();
        SaveConnectionCommand.NotifyCanExecuteChanged();
        SaveUtilityModelCommand.NotifyCanExecuteChanged();
    }

    partial void OnRuntimeErrorCodeChanged(string? value)
        => OnPropertyChanged(nameof(HasRuntimeError));

    partial void OnIsConnectionConfiguredChanged(bool value) => OnPropertyChanged(nameof(IsConnectionStatusWarning));

    partial void OnIsConnectionStatusErrorChanged(bool value) => OnPropertyChanged(nameof(IsConnectionStatusWarning));

    partial void OnBaseUrlChanged(string value) => RefreshConnectionStatus();

    [RelayCommand(CanExecute = nameof(IsError))]
    private async Task RetryInitializationAsync()
        => await InitializeAsync();

    [RelayCommand(CanExecute = nameof(CanSaveSettings))]
    private async Task SaveSettingsAsync(CancellationToken cancellationToken)
    {
        if (!CanSaveSettings)
        {
            return;
        }
        if (!await SaveConnectionCoreAsync(cancellationToken))
        {
            return;
        }

        await ApiKeySettings.SaveCredentialAsync();
        await SaveUtilityModelCoreAsync(cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanSaveSettings))]
    private async Task SaveConnectionAsync(CancellationToken cancellationToken)
        => await SaveConnectionCoreAsync(cancellationToken);

    [RelayCommand(CanExecute = nameof(CanSaveSettings))]
    private async Task SaveUtilityModelAsync(CancellationToken cancellationToken)
        => await SaveUtilityModelCoreAsync(cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _loadGeneration);
        _lifetime.Cancel();
        ApiKeySettings.Dispose();
        _lifetime.Dispose();
    }

    private async Task<bool> SaveConnectionCoreAsync(CancellationToken cancellationToken)
    {
        if (!CanSaveSettings)
        {
            return false;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
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

            await _packageContext.Settings.SetValueAsync(
                LMStudioProviderConfiguration.BaseUrlKey,
                normalizedBaseUrl,
                linked.Token);
            if (!_disposed)
            {
                BaseUrl = normalizedBaseUrl;
                RefreshConnectionStatus();
            }
            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _logger.LogError(ex, "LM Studio connection settings could not be saved");
                SetConnectionErrorStatus("Connection settings could not be saved.");
            }
            return false;
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
            }
        }
    }

    private async Task SaveUtilityModelCoreAsync(CancellationToken cancellationToken)
    {
        if (!CanSaveSettings)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        IsBusy = true;
        try
        {
            var utilityModelId = UtilityModelId.Trim();
            if (string.IsNullOrWhiteSpace(utilityModelId))
            {
                await _packageContext.Settings.DeleteValueAsync(
                    LMStudioProviderConfiguration.UtilityModelKey,
                    linked.Token);
                if (!_disposed)
                {
                    UtilityModelId = string.Empty;
                }
                return;
            }

            var normalizedModelId = ProviderModelId.EnsurePrefix(utilityModelId, "lmstudio");
            await _packageContext.Settings.SetValueAsync(
                LMStudioProviderConfiguration.UtilityModelKey,
                normalizedModelId,
                linked.Token);
            if (!_disposed)
            {
                UtilityModelId = normalizedModelId;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                _logger.LogError(exception, "LM Studio utility-model settings could not be saved");
                SetConnectionErrorStatus("Utility-model settings could not be saved.");
            }
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
            }
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

    private bool CanApplyLoad(long generation)
        => !_disposed && generation == Volatile.Read(ref _loadGeneration);

    private void SetLoadState(LMStudioSettingsLoadState state)
    {
        if (!_disposed)
        {
            LoadState = state;
        }
    }

    private void ApplyRuntimeFailure(PackageRuntimeInvocationException exception)
    {
        RuntimeErrorCode = exception.Code;
        RuntimeCorrelationId = exception.CorrelationId;
        StatusText = FormatError("LM Studio settings could not be loaded.", exception.Code, exception.CorrelationId);
        _logger.LogWarning(
            "LM Studio settings Runtime request failed. Code: {Code}; CorrelationId: {CorrelationId}",
            exception.Code,
            exception.CorrelationId);
    }

    private void ApplyTransportFailure(Exception exception)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        RuntimeErrorCode = "lmstudio.settings.load-failed";
        RuntimeCorrelationId = correlationId;
        StatusText = FormatError(
            "LM Studio settings could not be loaded.",
            "lmstudio.settings.load-failed",
            correlationId);
        _logger.LogError(
            exception,
            "LM Studio settings load failed. CorrelationId: {CorrelationId}",
            correlationId);
    }

    private void ClearRuntimeError()
    {
        RuntimeErrorCode = null;
        RuntimeCorrelationId = null;
    }

    private static string FormatError(string message, string code, string? correlationId)
        => string.IsNullOrWhiteSpace(correlationId)
            ? $"{message} (code: {code})"
            : $"{message} (code: {code}; correlation: {correlationId})";
}

public enum LMStudioSettingsLoadState
{
    Uninitialized = 0,
    Loading = 1,
    Ready = 2,
    Error = 3,
}
