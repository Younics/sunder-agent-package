using Sunder.Package.Agent.Subagents.Runtime;

namespace Sunder.Package.Agent.Subagents.PackageViews;

public sealed partial class SubsessionsViewModel
{
    internal SubsessionsViewModel(
        ISubsessionSessionReader sessionReader,
        ISubsessionCheckpointReader checkpointReader,
        ISubsessionTranscriptPageReader transcriptReader,
        ISubsessionChangeNotifications changeNotifications)
        : this(null, null, initialize: false)
    {
        SetRuntimePorts(sessionReader, checkpointReader, transcriptReader, changeNotifications);
        _initialization = InitializeCoreAsync();
    }

    public Task InitializeAsync() => _initialization;

    internal void ReportTranscriptPagingFailure(Exception exception)
    {
        if (!_disposed)
        {
            StatusText = $"Unable to load transcript: {exception.Message}";
        }
    }

    private async Task InitializeCoreAsync()
    {
        try
        {
            await ReloadSubsessionsAsync(null);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
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
