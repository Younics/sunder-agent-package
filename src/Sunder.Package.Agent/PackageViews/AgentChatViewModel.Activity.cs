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
            _transcriptItemsProjection.SetDetailed(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActivityViewToggleText));
            TranscriptChanged?.Invoke();
        }
    }

    public string ActivityViewToggleText => IsDetailedTranscript ? "Compact activity" : "Detailed activity";

    internal object RevealTranscriptAnchor(object anchor) => _transcriptItemsProjection.RevealAnchor(anchor);
    internal object ResolveVisibleTranscriptAnchor(object anchor) => _transcriptItemsProjection.ResolveVisibleAnchor(anchor);
}
