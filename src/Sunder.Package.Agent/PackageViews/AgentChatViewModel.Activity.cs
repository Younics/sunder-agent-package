namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel
{
    private bool _isDetailedTranscript;

    public bool IsDetailedTranscript
    {
        get => _isDetailedTranscript;
        set
        {
            if (_isDetailedTranscript == value || _disposed) return;
            TranscriptChanging?.Invoke(false);
            _isDetailedTranscript = value;
            if (_timeline.SupportsCompact)
            {
                ChangeTranscriptMode(value);
            }
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

    private void OnRunActivityStateChanged() => ApplyRunActivityState();

    private void ApplyRunActivityState()
    {
        if (RunActivityRow.MatchesRunPresentation(_runActivity.Text, _runActivity.ShouldShow, _runActivity.ReasoningText, _runActivity.Retry)) return;
        // Initial/replacement presentation owns activity layout until this exact
        // session's transcript is loaded. Do not complete an empty old viewport.
        var changesLayout = _isInitialized && !_timeline.IsInitialLoading
                            && _timeline.SessionId is { } activitySession && activitySession == DisplayedSession?.SessionId
                            && (RunActivityRow.IsVisible || _runActivity.ShouldShow);
        if (changesLayout) OnTimelineRowsChanging(false);
        try { RunActivityRow.SetRunPresentation(_runActivity.Text, _runActivity.IsReasoning, _runActivity.ShouldShow, _runActivity.ReasoningText, _runActivity.Retry); }
        finally { if (changesLayout) OnTimelineRowsChanged(); }
    }
}
