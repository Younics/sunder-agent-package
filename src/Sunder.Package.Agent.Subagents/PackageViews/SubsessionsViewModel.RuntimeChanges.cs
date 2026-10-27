namespace Sunder.Package.Agent.Subagents.PackageViews;

public sealed partial class SubsessionsViewModel
{
    private void OnRuntimeResnapshotRequired()
    {
        RunOnUiThread(() =>
        {
            _knownActivities.Clear();
            _runActivity.Reset();
        });
        Interlocked.Exchange(ref _forceRuntimeTranscriptRefresh, 1);
        _tasks.Run(_ => _runtimeRefresh.MarkDirty());
    }
}
