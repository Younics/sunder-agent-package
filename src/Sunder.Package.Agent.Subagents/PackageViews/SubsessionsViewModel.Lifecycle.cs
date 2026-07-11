namespace Sunder.Package.Agent.Subagents.PackageViews;

public sealed partial class SubsessionsViewModel
{
    public Task InitializeAsync() => _initialization;

    private Task InitializeCoreAsync()
    {
        try
        {
            ReloadSubsessions(null);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_runtimeCatalog is not null)
        {
            _runtimeCatalog.SessionChanged -= OnSessionChanged;
            _runtimeCatalog.TurnChanged -= OnTurnChanged;
        }

        _runActivity.Changed -= OnRunActivityStateChanged;
        _runActivity.Dispose();
        _timeline.PropertyChanged -= OnTimelinePropertyChanged;
        _timeline.TurnProjected -= OnTimelineTurnProjected;
        _timeline.Dispose();
    }
}
