using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Execution.Local;

public sealed partial class LocalExecutionSettingsViewModel : ObservableObject,
    IPackageViewNavigationPreparationTarget,
    IDisposable
{
    private const string SettingsChannel = "local-settings";
    private const string ShellCatalogChannel = "local-shell-catalog";
    private readonly LocalExecutionAppRuntimeClient _runtimeClient;
    private readonly LatestRequestCoordinator _requests = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _initializationSyncRoot = new();
    private Task? _initialization;
    private bool _disposed;
    private bool _suppressRevisionTracking;
    private long _busyGeneration;
    private long _selectionRevision;
    private long _timeoutRevision;
    private long _shellListRevision;
    private long _shellCatalogRevision;

    internal LocalExecutionSettingsViewModel(LocalExecutionAppRuntimeClient runtimeClient)
    {
        _runtimeClient = runtimeClient;
        SyntaxOptions =
        [
            new ShellSyntaxOption(AgentShellSyntaxKinds.PowerShell, "PowerShell"),
            new ShellSyntaxOption(AgentShellSyntaxKinds.Cmd, "Command Prompt"),
            new ShellSyntaxOption(AgentShellSyntaxKinds.PosixSh, "POSIX sh"),
            new ShellSyntaxOption(AgentShellSyntaxKinds.Custom, "Custom"),
        ];
        TimeoutSeconds = LocalExecutionConfiguration.DefaultTimeoutSeconds;
    }

    internal static IReadOnlyCollection<string> OwnedConfigurationKeys { get; } =
        [LocalExecutionConfiguration.TimeoutKey];

    public ObservableCollection<LocalShellRowViewModel> DetectedShells { get; } = [];

    public ObservableCollection<LocalShellRowViewModel> CustomShells { get; } = [];

    public ObservableCollection<ShellSyntaxOption> SyntaxOptions { get; }

    public bool IsLoading => LoadState == LocalSettingsLoadState.Loading;

    public bool IsReady => LoadState == LocalSettingsLoadState.Ready;

    public bool IsError => LoadState == LocalSettingsLoadState.Error;

    public bool CanMutate => IsReady && !IsBusy && !_disposed;

    public bool CanAddShell => CanMutate;

    public bool CanSaveShells => CanMutate;

    public bool CanSaveExecutionSettings => CanMutate;

    public bool HasRuntimeError => !string.IsNullOrWhiteSpace(RuntimeErrorCode);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading))]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyPropertyChangedFor(nameof(IsError))]
    [NotifyPropertyChangedFor(nameof(CanMutate))]
    private LocalSettingsLoadState _loadState;

    [ObservableProperty]
    private LocalShellRowViewModel? _selectedShell;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string? _runtimeErrorCode;

    [ObservableProperty]
    private string? _runtimeCorrelationId;

    [ObservableProperty]
    private string _timeoutSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMutate))]
    private bool _isBusy;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Task initialization;
        lock (_initializationSyncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialization is null
                || LoadState == LocalSettingsLoadState.Error && _initialization.IsCompleted)
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
        if (LoadState is LocalSettingsLoadState.Uninitialized or LocalSettingsLoadState.Error)
        {
            await InitializeAsync(cancellationToken);
        }
        else if (LoadState == LocalSettingsLoadState.Ready)
        {
            await RefreshAsync(cancellationToken);
        }
        else
        {
            await InitializeAsync(cancellationToken);
        }
        return true;
    }

    public ValueTask OnNavigationPresentedAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(IsError))]
    private async Task RetryInitializationAsync()
        => await InitializeAsync();

    [RelayCommand(CanExecute = nameof(CanAddShell))]
    private void AddShell()
    {
        if (!CanAddShell)
        {
            return;
        }

        var row = new LocalShellRowViewModel(
            "custom-" + Guid.NewGuid().ToString("N"),
            "Shell",
            string.Empty,
            AgentShellSyntaxKinds.Custom,
            false,
            SyntaxOptions);
        CustomShells.Add(row);
        _shellListRevision++;
        SelectedShell = row;
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedShell))]
    private async Task DeleteSelectedShellAsync()
    {
        if (!CanDeleteSelectedShell() || SelectedShell is not { IsDetected: false } shell)
        {
            return;
        }

        await SaveShellsCoreAsync(shell.ShellId);
    }

    [RelayCommand(CanExecute = nameof(CanSaveShells))]
    private async Task SaveShellsAsync()
        => await SaveShellsCoreAsync(deletedShellId: null);

    [RelayCommand(CanExecute = nameof(CanSaveExecutionSettings))]
    private async Task SaveExecutionSettingsAsync(CancellationToken cancellationToken)
    {
        if (!CanSaveExecutionSettings)
        {
            return;
        }
        if (!BoundedValue.TryParseInt32(
                TimeoutSeconds,
                minimum: 1,
                maximum: BoundedProcessRunner.MaximumTimeoutSeconds,
                out var timeoutSeconds))
        {
            SetStatus($"Local shell timeout must be between 1 and {BoundedProcessRunner.MaximumTimeoutSeconds} seconds.");
            return;
        }

        var timeoutRevision = _timeoutRevision;
        var response = await RunMutationAsync(
            new LocalExecutionOperationRequest(
                LocalExecutionOperationKind.SaveSettings,
                TimeoutSeconds: timeoutSeconds.ToString()),
            cancellationToken);
        if (response is null)
        {
            return;
        }
        if (response.TimeoutSeconds is null)
        {
            SetProtocolError("SaveSettings response omitted timeoutSeconds.");
            return;
        }

        ApplyTimeout(response.TimeoutSeconds, timeoutRevision);
        _requests.Invalidate(SettingsChannel);
        SetStatus(response.Message ?? "Local execution settings saved.");
    }

    private async Task InitializeCoreAsync()
    {
        SetLoadState(LocalSettingsLoadState.Loading);
        await LoadSnapshotAsync(_lifetime.Token);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        await LoadSnapshotAsync(linked.Token);
    }

    private async Task LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        var settingsRequest = _requests.Begin(SettingsChannel, cancellationToken);
        var shellsRequest = _requests.Begin(ShellCatalogChannel, cancellationToken);
        var timeoutRevision = _timeoutRevision;
        var authority = CaptureShellAuthority();
        try
        {
            var response = await _runtimeClient.InvokeAsync(
                    new LocalExecutionOperationRequest(LocalExecutionOperationKind.GetSettings),
                    settingsRequest.CancellationToken)
                .AsTask()
                .WaitAsync(settingsRequest.CancellationToken);
            if (_disposed)
            {
                return;
            }
            if (response.Error is not null)
            {
                ApplyDomainError(response.Error);
                SetLoadState(LocalSettingsLoadState.Error);
                return;
            }
            if (response.TimeoutSeconds is null
                || response.DetectedShells is null
                || response.CustomShells is null
                || response.ShellCatalogRevision is not { } catalogRevision
                || catalogRevision < 0)
            {
                SetProtocolError("GetSettings response omitted required fields.");
                SetLoadState(LocalSettingsLoadState.Error);
                return;
            }

            if (_requests.IsCurrent(settingsRequest))
            {
                ApplyTimeout(response.TimeoutSeconds, timeoutRevision);
            }
            if (_requests.IsCurrent(shellsRequest))
            {
                ReconcileShells(
                    response.DetectedShells,
                    response.CustomShells,
                    catalogRevision,
                    authority);
            }
            ClearRuntimeError();
            SetStatus(string.Empty);
            SetLoadState(LocalSettingsLoadState.Ready);
        }
        catch (OperationCanceledException) when (settingsRequest.CancellationToken.IsCancellationRequested)
        {
            if (_lifetime.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
        catch (PackageRuntimeInvocationException exception)
        {
            ApplyRuntimeFailure(exception);
            SetLoadState(LocalSettingsLoadState.Error);
        }
        catch (Exception exception)
        {
            SetProtocolError(exception.GetType().Name);
            SetLoadState(LocalSettingsLoadState.Error);
        }
        finally
        {
            _requests.Complete(settingsRequest);
            _requests.Complete(shellsRequest);
        }
    }

    private async Task SaveShellsCoreAsync(string? deletedShellId)
    {
        if (!CanSaveShells)
        {
            return;
        }

        var authority = CaptureShellAuthority();
        var shells = new List<LocalShellDefinition>();
        foreach (var shell in CustomShells.Where(shell =>
                     !string.Equals(shell.ShellId, deletedShellId, StringComparison.OrdinalIgnoreCase)))
        {
            if (string.IsNullOrWhiteSpace(shell.ExecutablePath))
            {
                continue;
            }

            var path = shell.ExecutablePath.Trim();
            shells.Add(new LocalShellDefinition(
                shell.ShellId,
                string.IsNullOrWhiteSpace(shell.DisplayName)
                    ? Path.GetFileNameWithoutExtension(path)
                    : shell.DisplayName.Trim(),
                path,
                shell.SelectedSyntax?.SyntaxKind ?? AgentShellSyntaxKinds.Custom,
                IsDetected: false));
        }

        var request = _requests.Begin(ShellCatalogChannel, _lifetime.Token);
        var operation = BeginBusyOperation();
        try
        {
            var response = await _runtimeClient.InvokeAsync(
                    new LocalExecutionOperationRequest(
                        LocalExecutionOperationKind.SaveShells,
                        Shells: shells,
                        ExpectedShellCatalogRevision: _shellCatalogRevision),
                    request.CancellationToken)
                .AsTask()
                .WaitAsync(request.CancellationToken);
            if (_disposed || !_requests.IsCurrent(request))
            {
                return;
            }
            if (response.Error is not null)
            {
                ApplyDomainError(response.Error);
                return;
            }
            if (response.DetectedShells is null
                || response.CustomShells is null
                || response.ShellCatalogRevision is not { } catalogRevision
                || catalogRevision < 0)
            {
                SetProtocolError("SaveShells response omitted required fields.");
                return;
            }

            ReconcileShells(
                response.DetectedShells,
                response.CustomShells,
                catalogRevision,
                authority);
            ClearRuntimeError();
            SetStatus(response.Message ?? "Shell settings saved.");
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
        }
        catch (PackageRuntimeInvocationException exception)
        {
            ApplyRuntimeFailure(exception);
        }
        catch (Exception exception)
        {
            SetProtocolError(exception.GetType().Name);
        }
        finally
        {
            _requests.Complete(request);
            CompleteBusyOperation(operation);
        }
    }

    private async Task<LocalExecutionOperationResponse?> RunMutationAsync(
        LocalExecutionOperationRequest operationRequest,
        CancellationToken cancellationToken)
    {
        if (!CanMutate)
        {
            return null;
        }

        var operation = BeginBusyOperation();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        try
        {
            var response = await _runtimeClient.InvokeAsync(operationRequest, linked.Token)
                .AsTask()
                .WaitAsync(linked.Token);
            if (_disposed || !IsBusyOperationCurrent(operation))
            {
                return null;
            }
            if (response.Error is not null)
            {
                ApplyDomainError(response.Error);
                return null;
            }
            ClearRuntimeError();
            return response;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return null;
        }
        catch (PackageRuntimeInvocationException exception)
        {
            ApplyRuntimeFailure(exception);
            return null;
        }
        catch (Exception exception)
        {
            SetProtocolError(exception.GetType().Name);
            return null;
        }
        finally
        {
            CompleteBusyOperation(operation);
        }
    }

    private ShellReconciliationAuthority CaptureShellAuthority()
        => new(
            _selectionRevision,
            _shellListRevision,
            DetectedShells.Concat(CustomShells).ToDictionary(
                shell => shell.ShellId,
                shell => shell.CaptureRevision(),
                StringComparer.OrdinalIgnoreCase));

    private void ReconcileShells(
        IReadOnlyList<LocalShellDefinition> detectedShells,
        IReadOnlyList<LocalShellDefinition> customShells,
        long catalogRevision,
        ShellReconciliationAuthority authority)
    {
        var selectedId = SelectedShell?.ShellId;
        ReconcileCollection(DetectedShells, detectedShells, authority, preserveDrafts: false);
        ReconcileCollection(CustomShells, customShells, authority, preserveDrafts: true);
        _shellCatalogRevision = catalogRevision;

        _suppressRevisionTracking = true;
        try
        {
            var selected = FindShell(selectedId);
            if (authority.SelectionRevision != _selectionRevision && selected is not null)
            {
                SelectedShell = selected;
            }
            else
            {
                SelectedShell = selected
                    ?? CustomShells.FirstOrDefault()
                    ?? DetectedShells.FirstOrDefault();
            }
        }
        finally
        {
            _suppressRevisionTracking = false;
        }
    }

    private void ReconcileCollection(
        ObservableCollection<LocalShellRowViewModel> rows,
        IReadOnlyList<LocalShellDefinition> definitions,
        ShellReconciliationAuthority authority,
        bool preserveDrafts)
    {
        var desiredIds = definitions.Select(shell => shell.ShellId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = rows.Count - 1; index >= 0; index--)
        {
            var row = rows[index];
            if (desiredIds.Contains(row.ShellId))
            {
                continue;
            }
            if (!authority.Rows.TryGetValue(row.ShellId, out var revision)
                || row.HasChangedSince(revision)
                || preserveDrafts && row.IsDraft)
            {
                continue;
            }
            rows.RemoveAt(index);
        }

        for (var index = 0; index < definitions.Count; index++)
        {
            var definition = definitions[index];
            var existing = rows.FirstOrDefault(row => string.Equals(
                row.ShellId,
                definition.ShellId,
                StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                rows.Insert(index, CreateRow(definition));
                continue;
            }

            if (authority.Rows.TryGetValue(existing.ShellId, out var expectedRevision))
            {
                existing.Apply(definition, expectedRevision);
            }
            var existingIndex = rows.IndexOf(existing);
            if (existingIndex != index)
            {
                rows.Move(existingIndex, index);
            }
        }
    }

    private LocalShellRowViewModel? FindShell(string? shellId)
        => string.IsNullOrWhiteSpace(shellId)
            ? null
            : CustomShells.Concat(DetectedShells).FirstOrDefault(shell => string.Equals(
                shell.ShellId,
                shellId,
                StringComparison.OrdinalIgnoreCase));

    private LocalShellRowViewModel CreateRow(LocalShellDefinition shell)
        => new(
            shell.ShellId,
            shell.DisplayName,
            shell.ExecutablePath,
            shell.SyntaxKind,
            shell.IsDetected,
            SyntaxOptions);

    private void ApplyTimeout(string value, long expectedRevision)
    {
        if (_disposed || expectedRevision != _timeoutRevision)
        {
            return;
        }

        _suppressRevisionTracking = true;
        try
        {
            TimeoutSeconds = value;
        }
        finally
        {
            _suppressRevisionTracking = false;
        }
    }

    private bool CanDeleteSelectedShell()
        => CanMutate && SelectedShell is { IsDetected: false };

    partial void OnSelectedShellChanged(LocalShellRowViewModel? value)
    {
        if (!_suppressRevisionTracking)
        {
            _selectionRevision++;
        }
        DeleteSelectedShellCommand.NotifyCanExecuteChanged();
    }

    partial void OnTimeoutSecondsChanged(string value)
    {
        if (!_suppressRevisionTracking)
        {
            _timeoutRevision++;
        }
    }

    partial void OnLoadStateChanged(LocalSettingsLoadState value)
    {
        RetryInitializationCommand.NotifyCanExecuteChanged();
        NotifyMutationCommands();
    }

    partial void OnIsBusyChanged(bool value)
        => NotifyMutationCommands();

    partial void OnRuntimeErrorCodeChanged(string? value)
        => OnPropertyChanged(nameof(HasRuntimeError));

    private void NotifyMutationCommands()
    {
        AddShellCommand.NotifyCanExecuteChanged();
        DeleteSelectedShellCommand.NotifyCanExecuteChanged();
        SaveShellsCommand.NotifyCanExecuteChanged();
        SaveExecutionSettingsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAddShell));
        OnPropertyChanged(nameof(CanSaveShells));
        OnPropertyChanged(nameof(CanSaveExecutionSettings));
    }

    private long BeginBusyOperation()
    {
        var generation = ++_busyGeneration;
        IsBusy = true;
        return generation;
    }

    private bool IsBusyOperationCurrent(long generation)
        => !_disposed && generation == _busyGeneration;

    private void CompleteBusyOperation(long generation)
    {
        if (IsBusyOperationCurrent(generation))
        {
            IsBusy = false;
        }
    }

    private void SetLoadState(LocalSettingsLoadState state)
    {
        if (!_disposed)
        {
            LoadState = state;
        }
    }

    private void SetStatus(string message)
    {
        if (!_disposed)
        {
            StatusText = message;
        }
    }

    private void ApplyDomainError(LocalExecutionOperationError error)
    {
        if (!IsSafeLowercaseToken(error.Code)
            || string.IsNullOrWhiteSpace(error.Message)
            || error.Message.Length > 1024
            || error.CorrelationId is not null && !IsSafeToken(error.CorrelationId))
        {
            SetProtocolError("Operation error payload is invalid.");
            return;
        }
        RuntimeErrorCode = error.Code;
        RuntimeCorrelationId = error.CorrelationId;
        StatusText = FormatError(error.Message, error.Code, error.CorrelationId);
    }

    private void ApplyRuntimeFailure(PackageRuntimeInvocationException exception)
    {
        RuntimeErrorCode = exception.Code;
        RuntimeCorrelationId = exception.CorrelationId;
        StatusText = FormatError(
            "Local execution Runtime request failed.",
            exception.Code,
            exception.CorrelationId);
    }

    private void SetProtocolError(string diagnostic)
    {
        RuntimeErrorCode = "local.protocol.invalid-response";
        RuntimeCorrelationId = null;
        StatusText = "Local execution Runtime returned an invalid response (code: local.protocol.invalid-response).";
        _ = diagnostic;
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

    private static bool IsSafeLowercaseToken(string? value)
        => IsSafeToken(value)
           && value!.All(character => !char.IsAsciiLetter(character)
                                      || char.IsAsciiLetterLower(character));

    private static bool IsSafeToken(string? value)
        => value is { Length: > 0 and <= 128 }
           && value.All(character => char.IsAsciiLetterOrDigit(character)
                                     || character is '.' or '-' or '_');

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _busyGeneration++;
        _lifetime.Cancel();
        _requests.Dispose();
        _lifetime.Dispose();
    }

    private sealed record ShellReconciliationAuthority(
        long SelectionRevision,
        long ListRevision,
        IReadOnlyDictionary<string, LocalShellRowRevision> Rows);
}

public enum LocalSettingsLoadState
{
    Uninitialized = 0,
    Loading = 1,
    Ready = 2,
    Error = 3,
}
