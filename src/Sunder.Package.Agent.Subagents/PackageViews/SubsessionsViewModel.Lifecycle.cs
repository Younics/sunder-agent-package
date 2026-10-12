using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Package.Agent.Subagents.Runtime;

namespace Sunder.Package.Agent.Subagents.PackageViews;

public sealed partial class SubsessionsViewModel
{
    private bool _ownsChangeNotifications;
    private IOwnerStreamAvailability? _ownerAvailability;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _hasLoadError;

    [ObservableProperty]
    private bool _isRuntimeAvailable = true;

    internal SubsessionsViewModel(
        ISubsessionSessionReader sessionReader,
        ISubsessionCheckpointReader checkpointReader,
        ISubsessionTranscriptPageReader transcriptReader,
        ISubsessionChangeNotifications changeNotifications)
        : this(
            sessionReader,
            checkpointReader,
            transcriptReader,
            changeNotifications,
            PresentationDispatcher.Capture(),
            operationObserver: null)
    {
    }

    internal SubsessionsViewModel(
        ISubsessionSessionReader sessionReader,
        ISubsessionCheckpointReader checkpointReader,
        ISubsessionTranscriptPageReader transcriptReader,
        ISubsessionChangeNotifications changeNotifications,
        IPresentationDispatcher uiDispatcher,
        PresentationOperationObserver? operationObserver = null)
        : this(null, uiDispatcher, operationObserver)
    {
        SetRuntimePorts(sessionReader, checkpointReader, transcriptReader, changeNotifications);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => _operations.RunAsync(
            SubsessionRefreshOperation,
            token => EnsureInitializedAsync(null, token),
            ReconcileSubsessionLoadFailureAsync,
            cancellationToken);

    [RelayCommand]
    private Task RetryLoadAsync(CancellationToken cancellationToken)
        => _operations.RunAsync(
            SubsessionRefreshOperation,
            RetryLoadCoreAsync,
            ReconcileSubsessionLoadFailureAsync,
            cancellationToken);

    private async Task RetryLoadCoreAsync(CancellationToken cancellationToken)
    {
        Guid? selectedSessionId = null;
        await RunOnUiThreadAsync(() =>
        {
            selectedSessionId = SelectedSubsession?.SessionId;
            StatusText = "Retrying Agent Runtime...";
        }, cancellationToken);
        await InitializeCoreAsync(selectedSessionId, suppressTranscriptLoad: false, cancellationToken);
    }

    private Task EnsureInitializedAsync(
        Guid? selectedSessionId,
        CancellationToken cancellationToken,
        bool suppressTranscriptLoad = false)
        => _initialization.RunAsync(
            token => InitializeCoreAsync(selectedSessionId, suppressTranscriptLoad, token),
            cancellationToken);

    internal void ReportTranscriptPagingFailure(Exception exception)
    {
        if (!_disposed)
        {
            RunOnUiThread(() => SetLoadFailure($"Unable to load transcript: {exception.Message}"));
        }
    }

    private void SetLoadFailure(string message)
    {
        StatusText = message;
        HasLoadError = true;
    }

    private async Task InitializeCoreAsync(
        Guid? selectedSessionId,
        bool suppressTranscriptLoad,
        CancellationToken cancellationToken)
    {
        if (_changeNotifications is ISubagentPresentationInitialization initialization)
        {
            try
            {
                await initialization.InitializeAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsExpectedSubsessionRuntimeFailure(exception))
            {
                throw new PresentationOperationException(
                    "Subsessions could not initialize the Agent Runtime presentation.",
                    exception);
            }
        }

        // A reset can supersede the initial read. Initialization is a publication
        // barrier, so a canceled/stale read must not complete warmup prematurely.
        while (!await ReloadSubsessionsAsync(
                   selectedSessionId,
                   cancellationToken,
                   suppressTranscriptLoad))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _navigationHighlightGeneration);
        _navigationHighlightCancellation?.Cancel();
        _navigationHighlightCancellation?.Dispose();
        _navigationHighlightCancellation = null;
        _initialization.Dispose();
        if (_changeNotifications is not null)
        {
            _changeNotifications.SessionChanged -= OnSessionChanged;
            _changeNotifications.TurnChanged -= OnTurnChanged;
            _changeNotifications.ResnapshotRequired -= OnRuntimeResnapshotRequired;
            if (_ownsChangeNotifications) (_changeNotifications as IDisposable)?.Dispose();
        }
        if (_ownerAvailability is not null)
        {
            _ownerAvailability.OwnerStreamAvailabilityChanged -= OnOwnerStreamAvailabilityChanged;
        }

        _runActivity.Changed -= OnRunActivityStateChanged;
        _runActivity.Dispose();
        _transcriptItemsProjection.Dispose();
        RunActivityRow.Dispose();
        _timeline.PropertyChanged -= OnTimelinePropertyChanged;
        _timeline.TurnProjected -= OnTimelineTurnProjected;
        _timeline.Dispose();
        _activityTicker.Dispose();
        _listDetail.PropertyChanged -= OnListDetailPropertyChanged;
        _runtimeRefresh.Dispose();
        _listDetail.Dispose();
        _requests.Dispose();
        _operations.Dispose();
        _tasks.Dispose();
    }

    private void OnOwnerStreamAvailabilityChanged(bool available)
        => RunOnUiThread(() => IsRuntimeAvailable = available);

    private void SetOwnerAvailability(ISubsessionChangeNotifications changeNotifications)
    {
        if (_ownerAvailability is not null)
        {
            _ownerAvailability.OwnerStreamAvailabilityChanged -= OnOwnerStreamAvailabilityChanged;
        }
        _ownerAvailability = changeNotifications as IOwnerStreamAvailability;
        IsRuntimeAvailable = _ownerAvailability?.IsOwnerStreamAvailable ?? true;
        if (_ownerAvailability is not null)
        {
            _ownerAvailability.OwnerStreamAvailabilityChanged += OnOwnerStreamAvailabilityChanged;
        }
    }

    private void SetRuntimePorts(
        ISubsessionSessionReader sessionReader,
        ISubsessionCheckpointReader checkpointReader,
        ISubsessionTranscriptPageReader transcriptReader,
        ISubsessionChangeNotifications changeNotifications,
        bool ownsChangeNotifications = false)
    {
        _sessionReader = sessionReader;
        _checkpointReader = checkpointReader;
        _transcriptReader = transcriptReader;
        _changeNotifications = changeNotifications;
        SetOwnerAvailability(changeNotifications);
        _ownsChangeNotifications = ownsChangeNotifications;
        changeNotifications.SessionChanged += OnSessionChanged;
        changeNotifications.TurnChanged += OnTurnChanged;
        changeNotifications.ResnapshotRequired += OnRuntimeResnapshotRequired;
    }
}
