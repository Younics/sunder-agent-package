namespace Sunder.Package.Agent.Subagents.PackageViews;

public sealed partial class SubsessionsViewModel
{
    private void OnRuntimeResnapshotRequired()
    {
        Interlocked.Exchange(ref _forceRuntimeTranscriptRefresh, 1);
        _tasks.Run(_runtimeRefresh.MarkDirty());
    }
}
