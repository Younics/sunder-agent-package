using Sunder.Package.Agent.Subagents.Runtime;

namespace Sunder.Package.Agent.Subagents.PackageViews;

public sealed partial class SubsessionsViewModel
{
    private readonly Dictionary<Guid, AgentRunActivityUpdate> _knownActivities = [];

    private void MergeRunActivities(IReadOnlyList<SubsessionRunActivity>? activities)
    {
        foreach (var activity in activities ?? [])
            if (!_knownActivities.TryGetValue(activity.SessionId, out var prior)
                || prior.RunRevision < activity.Activity.RunRevision
                || prior.RunRevision == activity.Activity.RunRevision && prior.Sequence < activity.Activity.Sequence)
                _knownActivities[activity.SessionId] = activity.Activity;
        foreach (var id in _knownActivities.Keys.ToArray())
            if (!_knownCheckpoints.TryGetValue(id, out var checkpoint) || checkpoint.Status != AgentRunStatus.Running
                || checkpoint.RunRevision != _knownActivities[id].RunRevision) _knownActivities.Remove(id);
    }

    private void OnRunActivityChanged(Guid sessionId, AgentRunActivityUpdate activity)
        => RunOnUiThread(() =>
        {
            if (_disposed || !_knownSessions.ContainsKey(sessionId)) return;
            if (!_knownCheckpoints.TryGetValue(sessionId, out var checkpoint)
                || checkpoint.Status != AgentRunStatus.Running || checkpoint.RunRevision != activity.RunRevision) return;
            if (_knownActivities.TryGetValue(sessionId, out var previous)
                && (previous.RunRevision > activity.RunRevision || previous.RunRevision == activity.RunRevision && previous.Sequence >= activity.Sequence)) return;
            _knownActivities[sessionId] = activity;
            if (SelectedSubsession?.SessionId == sessionId)
            {
                TrackCheckpointActivity(checkpoint);
                ApplyRunActivityState();
            }
        });

    private void TrackCheckpointActivity(AgentRunCheckpointRecord? checkpoint)
    {
        _runActivity.TrackCheckpoint(checkpoint);
        if (checkpoint?.Status == AgentRunStatus.Running
            && _knownActivities.TryGetValue(checkpoint.SessionId, out var activity)
            && activity.RunRevision == checkpoint.RunRevision)
            _runActivity.TrackStatus(activity.RunRevision, activity.Sequence, activity.Text,
                activity.Kind == AgentRunActivityKind.Reasoning, activity.ReasoningText, activity.Retry);
    }

    private bool _isDetailedTranscript;

    public bool IsDetailedTranscript
    {
        get => _isDetailedTranscript;
        set
        {
            if (_isDetailedTranscript == value || _disposed) return;
            TranscriptChanging?.Invoke(false);
            _isDetailedTranscript = value;
            if (_timeline.SupportsCompact) ChangeTranscriptMode(value);
            else _transcriptItemsProjection.SetDetailed(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActivityViewToggleText));
            TranscriptChanged?.Invoke();
        }
    }

    public string ActivityViewToggleText => IsDetailedTranscript ? "Compact activity" : "Detailed activity";

    internal object RevealTranscriptAnchor(object anchor) => _transcriptItemsProjection.RevealAnchor(anchor);
    internal object ResolveVisibleTranscriptAnchor(object anchor) => _timeline.SupportsCompact
        ? _timeline.ResolveCompactVisibleAnchor(anchor) : _transcriptItemsProjection.ResolveVisibleAnchor(anchor);
}
