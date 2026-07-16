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
            StatusText = ex.Message;
        }
    }

    private Task EnsureInitializedAsync(
        Guid? selectedSessionId,
        CancellationToken cancellationToken)
        => _initialization.RunAsync(
            token => InitializeCoreAsync(selectedSessionId, token),
            cancellationToken);

    internal void ReportTranscriptPagingFailure(Exception exception)
    {
        if (!_disposed)
        {
            StatusText = $"Unable to load transcript: {exception.Message}";
        }
    }

    private async Task InitializeCoreAsync(
        Guid? selectedSessionId,
        CancellationToken cancellationToken)
    {
        if (_changeNotifications is ISubagentPresentationInitialization initialization)
        {
            await initialization.InitializeAsync(cancellationToken);
        }

        await ReloadSubsessionsAsync(selectedSessionId, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _initialization.Dispose();
        if (_changeNotifications is not null)
        {
            _changeNotifications.SessionChanged -= OnSessionChanged;
            _changeNotifications.TurnChanged -= OnTurnChanged;
        }

        _runActivity.Changed -= OnRunActivityStateChanged;
        _runActivity.Dispose();
        _timeline.PropertyChanged -= OnTimelinePropertyChanged;
        _timeline.TurnProjected -= OnTimelineTurnProjected;
        _timeline.Dispose();
        _activityTicker.Dispose();
        _tasks.Dispose();
    }
}
