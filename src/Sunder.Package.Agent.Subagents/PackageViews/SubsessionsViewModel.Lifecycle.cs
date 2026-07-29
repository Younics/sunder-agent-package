using Sunder.Package.Agent.Subagents.Runtime;

namespace Sunder.Package.Agent.Subagents.PackageViews;

public sealed partial class SubsessionsViewModel
{
    internal SubsessionsViewModel(
        ISubsessionSessionReader sessionReader,
        ISubsessionCheckpointReader checkpointReader,
        ISubsessionTranscriptPageReader transcriptReader,
        ISubsessionChangeNotifications changeNotifications)
        : this(null, null)
    {
        SetRuntimePorts(sessionReader, checkpointReader, transcriptReader, changeNotifications);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureInitializedAsync(null, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await RunOnUiThreadAsync(() => StatusText = ex.Message, CancellationToken.None);
        }
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
            RunOnUiThread(() => StatusText = $"Unable to load transcript: {exception.Message}");
        }
    }

    private async Task InitializeCoreAsync(
        Guid? selectedSessionId,
        bool suppressTranscriptLoad,
        CancellationToken cancellationToken)
    {
        if (_changeNotifications is ISubagentPresentationInitialization initialization)
        {
            await initialization.InitializeAsync(cancellationToken);
        }

        await ReloadSubsessionsAsync(
            selectedSessionId,
            cancellationToken,
            suppressTranscriptLoad);
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
            (_changeNotifications as IDisposable)?.Dispose();
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
        _tasks.Dispose();
    }
}
