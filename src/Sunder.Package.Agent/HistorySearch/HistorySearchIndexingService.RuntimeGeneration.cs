namespace Sunder.Package.Agent.HistorySearch;

internal sealed partial class HistorySearchIndexingService
{
    private Guid? _runtimeEpoch;

    internal Guid? RuntimeEpoch => _runtimeEpoch;

    internal void BindRuntimeGeneration(Guid epoch)
    {
        _authoritative.EnsureRuntimeGenerationCurrent();
        _projection.BindRuntimeEpoch(epoch, _authoritative.EnsureRuntimeGenerationCurrent);
        _runtimeEpoch = epoch;
    }

    internal Task SignalStopAsync()
        => global::Sunder.Package.Agent.Services.AgentRuntimeWorkerCancellation.SignalAsync(
            Volatile.Read(ref _lifetime));
}
