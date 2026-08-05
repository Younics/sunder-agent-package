using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Provider.Shared;

internal readonly record struct ApiKeyStatus(string Label, string Detail, bool IsWarning = false);

internal sealed partial class ApiKeySettingsState : ObservableObject, IDisposable
{
    private static readonly TimeSpan SuccessStatusDuration = TimeSpan.FromSeconds(4);
    private readonly IProviderCredentialSettingsGateway _credentials;
    private readonly Func<bool, ApiKeyStatus> _resolveStatus;
    private readonly OperationState _operation = new();
    private readonly TimedStatusController _statusTimer;

    internal ApiKeySettingsState(
        IProviderCredentialSettingsGateway credentials,
        string description,
        string placeholder,
        Func<bool, ApiKeyStatus> resolveStatus,
        TimeProvider? timeProvider = null)
    {
        _credentials = credentials;
        Description = description;
        Placeholder = placeholder;
        _resolveStatus = resolveStatus;
        _statusTimer = new TimedStatusController(timeProvider);
        _operation.PropertyChanged += OnOperationPropertyChanged;
        var status = _resolveStatus(false);
        CredentialStatusLabel = status.Label;
        CredentialStatusDetail = status.Detail;
        IsCredentialStatusWarning = status.IsWarning;
    }

    public string Description { get; }

    public string Placeholder { get; }

    public bool HasStoredCredential { get; private set; }

    public bool CanSaveCredential => !_operation.IsBusy;

    public bool CanRequestClearCredential => HasStoredCredential && !_operation.IsBusy && !IsClearConfirmationRequested;

    public bool CanConfirmClearCredential => HasStoredCredential && !_operation.IsBusy && IsClearConfirmationRequested;

    public bool HasOperationStatus => !string.IsNullOrWhiteSpace(_operation.Message);

    public string OperationStatus => _operation.Message;

    public bool IsOperationStatusSuccess => _operation.Severity == OperationSeverity.Success;

    public bool IsOperationStatusWarning => _operation.Severity == OperationSeverity.Warning;

    public bool IsOperationStatusError => _operation.Severity == OperationSeverity.Error;

    [ObservableProperty]
    private string? _enteredCredential;

    [ObservableProperty]
    private string _credentialStatusLabel = string.Empty;

    [ObservableProperty]
    private string _credentialStatusDetail = string.Empty;

    [ObservableProperty]
    private bool _isCredentialStatusWarning;

    [ObservableProperty]
    private bool _isClearConfirmationRequested;

    partial void OnIsClearConfirmationRequestedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRequestClearCredential));
        OnPropertyChanged(nameof(CanConfirmClearCredential));
    }

    [RelayCommand]
    internal async Task SaveCredentialAsync()
    {
        if (_operation.IsBusy)
        {
            return;
        }

        _statusTimer.Cancel();
        var operation = _operation.Begin("Saving API key...", canCancel: false);
        try
        {
            operation.CancellationToken.ThrowIfCancellationRequested();
            var changed = !string.IsNullOrWhiteSpace(EnteredCredential);
            var snapshot = changed
                ? await _credentials.SetAsync(EnteredCredential!, operation.CancellationToken)
                : await _credentials.GetStatusAsync(operation.CancellationToken);
            EnteredCredential = null;
            IsClearConfirmationRequested = false;
            ApplyCredentialStatus(snapshot.HasStoredCredential);
            CompleteWithTransientSuccess(
                operation,
                changed ? "API key saved." : "Settings saved. The existing API key was retained.");
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
            _operation.TryComplete(operation, "Save canceled.", OperationSeverity.Warning);
        }
        catch (Exception ex)
        {
            _operation.TryComplete(operation, $"API key could not be saved: {ex.Message}", OperationSeverity.Error);
        }

    }

    [RelayCommand]
    private void RequestClearCredential()
    {
        if (CanRequestClearCredential)
        {
            IsClearConfirmationRequested = true;
        }
    }

    [RelayCommand]
    internal async Task ClearCredentialAsync()
    {
        if (!CanConfirmClearCredential)
        {
            return;
        }

        _statusTimer.Cancel();
        var operation = _operation.Begin("Clearing API key...", canCancel: false);
        try
        {
            operation.CancellationToken.ThrowIfCancellationRequested();
            var snapshot = await _credentials.ClearAsync(operation.CancellationToken);
            EnteredCredential = null;
            IsClearConfirmationRequested = false;
            ApplyCredentialStatus(snapshot.HasStoredCredential);
            CompleteWithTransientSuccess(operation, "Stored API key cleared.");
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
            _operation.TryComplete(operation, "Clear canceled.", OperationSeverity.Warning);
        }
        catch (Exception ex)
        {
            _operation.TryComplete(operation, $"API key could not be cleared: {ex.Message}", OperationSeverity.Error);
        }

    }

    [RelayCommand]
    private void CancelClearCredential() => IsClearConfirmationRequested = false;

    internal async Task RefreshCredentialStatusAsync(CancellationToken cancellationToken = default)
    {
        ApplyCredentialStatus((await _credentials.GetStatusAsync(cancellationToken)).HasStoredCredential);
    }

    internal void ApplyCredentialStatus(bool hasStoredCredential)
    {
        HasStoredCredential = hasStoredCredential;
        var status = _resolveStatus(HasStoredCredential);
        CredentialStatusLabel = status.Label;
        CredentialStatusDetail = status.Detail;
        IsCredentialStatusWarning = status.IsWarning;
        if (!HasStoredCredential)
        {
            IsClearConfirmationRequested = false;
        }

        OnPropertyChanged(nameof(HasStoredCredential));
        OnPropertyChanged(nameof(CanRequestClearCredential));
        OnPropertyChanged(nameof(CanConfirmClearCredential));
    }

    public void Dispose()
    {
        _operation.PropertyChanged -= OnOperationPropertyChanged;
        _operation.Dispose();
        _statusTimer.Dispose();
    }

    private void CompleteWithTransientSuccess(OperationGeneration operation, string message)
    {
        if (!_operation.TryComplete(operation, message))
        {
            return;
        }

        _ = _statusTimer.ScheduleAsync(SuccessStatusDuration, _operation.ClearStatus);
    }

    private void OnOperationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CanSaveCredential));
        OnPropertyChanged(nameof(CanRequestClearCredential));
        OnPropertyChanged(nameof(CanConfirmClearCredential));
        OnPropertyChanged(nameof(HasOperationStatus));
        OnPropertyChanged(nameof(OperationStatus));
        OnPropertyChanged(nameof(IsOperationStatusSuccess));
        OnPropertyChanged(nameof(IsOperationStatusWarning));
        OnPropertyChanged(nameof(IsOperationStatusError));
    }
}
