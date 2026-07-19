namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptScrollCoordinator
{
    private sealed record ScrollAnchor(
        ScrollAnchorMode Mode,
        bool WasFollowingTail,
        double DistanceFromBottom,
        double OffsetY,
        double ExtentHeight,
        long InteractionRevision,
        IReadOnlyList<ItemAnchor> Items);

    private sealed record ScrollToBottomRequest(long InteractionRevision, bool Force);

    private sealed record ItemAnchor(object Item, double Top, double Bottom);

    private enum ScrollAnchorMode
    {
        LiveTranscriptMutation,
        ViewportMutation,
        OlderRowsMutation,
    }
}
