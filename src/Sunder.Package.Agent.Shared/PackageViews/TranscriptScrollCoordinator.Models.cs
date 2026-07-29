using Avalonia.Controls;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal enum TranscriptViewportMutationKind
{
    LiveTranscript,
    ToolExpansion,
    StructuralLayout,
    TranscriptReplacement,
}

internal sealed class TranscriptPageAnchorAuthority(
    object? capturedAnchorKey,
    Func<object?> resolveCurrentAnchorKey)
{
    public object? CapturedAnchorKey { get; } = capturedAnchorKey;

    public object? ResolveCurrentAnchorKey()
        => resolveCurrentAnchorKey() ?? CapturedAnchorKey;
}

internal enum TranscriptViewportMutationMode
{
    ToolExpansionNativeAnchor,
    FollowingNativeObserver,
    DetachedLogicalRestore,
}

internal enum TranscriptViewportMutationStatus
{
    Active,
    Completed,
    AnchorUnavailable,
    Superseded,
    CanceledByAuthority,
    PresentationInactive,
    BudgetExhausted,
    CycleDetected,
    Disposed,
}

internal readonly record struct TranscriptViewportMutationDiagnostic(
    long Generation,
    long InteractionRevision,
    long AuthorityRevision,
    TranscriptViewportMutationKind Kind,
    TranscriptViewportMutationMode Mode,
    TranscriptViewportMutationStatus Status,
    int Passes,
    int Corrections,
    int StablePasses,
    int TerminalEpochs,
    long SignalSequence,
    object? ProtectedAnchorKey,
    bool GeometryPending,
    string GeometryCycle,
    string MarkdownRevisions,
    int CollapseClamps = 0,
    double HeaderDisplacement = 0);

internal enum TranscriptProgrammaticOffsetWriteSource
{
    AnchorRestoration,
    ViewportMutationCorrection,
    InitialAnchorPlacement,
    ScrollToBottom,
    BottomPlacementSettling,
    BottomPlacementExtentChanged,
    BottomPlacementRelease,
    ToolCollapseClamp,
}

internal readonly record struct TranscriptProgrammaticOffsetWriteDiagnostic(
    long Sequence,
    TranscriptProgrammaticOffsetWriteSource Source,
    double PreviousOffsetY,
    double RequestedOffsetY,
    double AppliedOffsetY,
    long InteractionRevision,
    long AuthorityRevision);

internal readonly record struct TranscriptScrollDiagnosticSnapshot(
    long InteractionRevision,
    long AuthorityRevision,
    long ProgrammaticOffsetWrites,
    int AnchoringSuspensionDepth,
    bool RenderedContentPending,
    bool BottomPlacementLockActive,
    bool RestoreAnchorPending,
    bool IsRestoringAnchor,
    bool ExactAnchorLeaseActive,
    bool TrailingCompensatorActive,
    TranscriptProgrammaticOffsetWriteDiagnostic? LastProgrammaticOffsetWrite,
    TranscriptViewportMutationDiagnostic? Mutation);

internal sealed partial class TranscriptScrollCoordinator
{
    private sealed record ScrollAnchor(
        ScrollAnchorMode Mode,
        double OffsetY,
        double ExtentHeight,
        long InteractionRevision,
        long InitialAuthorityRevision,
        IReadOnlyList<ItemAnchor> Items,
        IDisposable? InitialAnchoringSuspension) : IDisposable
    {
        private IDisposable? _anchoringSuspension = InitialAnchoringSuspension;

        public long AuthorityRevision { get; private set; } = InitialAuthorityRevision;

        public void TransferAuthority(long authorityRevision)
            => AuthorityRevision = authorityRevision;

        public void Dispose()
            => Interlocked.Exchange(ref _anchoringSuspension, null)?.Dispose();
    }

    private sealed record ScrollToBottomRequest(
        long InteractionRevision,
        long AuthorityRevision,
        bool Force);

    private sealed record ItemAnchor(object Item, double Top, double Bottom);

    private enum ScrollAnchorMode
    {
        ExplicitViewportRestore,
        OlderRowsMutation,
    }

    private sealed class ViewportMutationTransaction(
        long generation,
        long interactionRevision,
        long authorityRevision,
        TranscriptViewportMutationKind kind,
        TranscriptViewportMutationMode mode,
        Control? scope,
        object? preferredAnchorKey,
        ItemAnchor? protectedAnchor,
        bool? isExpanding,
        bool wasFollowingTail,
        IDisposable? viewportAuthorityLease)
    {
        private IDisposable? _viewportAuthorityLease = viewportAuthorityLease;
        private readonly CancellationTokenSource _watchdogCancellation = new();

        public long Generation { get; } = generation;

        public long InteractionRevision { get; } = interactionRevision;

        public long AuthorityRevision { get; } = authorityRevision;

        public TranscriptViewportMutationKind Kind { get; } = kind;

        public TranscriptViewportMutationMode Mode { get; } = mode;

        public Control? Scope { get; } = scope;

        public object? PreferredAnchorKey { get; } = preferredAnchorKey;

        public ItemAnchor? ProtectedAnchor { get; } = protectedAnchor;

        public bool? IsExpanding { get; } = isExpanding;

        public bool WasFollowingTail { get; } = wasFollowingTail;

        public TranscriptViewportMutationStatus Status { get; set; } = TranscriptViewportMutationStatus.Active;

        public int Passes { get; set; }

        public int Corrections { get; set; }

        public int StablePasses { get; set; }

        public int TerminalEpochs { get; set; }

        public long SignalSequence { get; set; }

        public bool CallbackQueued { get; set; }

        public bool WatchdogStarted { get; set; }

        public ViewportGeometryFingerprint? LastFingerprint { get; set; }

        public ViewportGeometryFingerprint? LastCorrectionFingerprint { get; set; }

        public Queue<ViewportGeometryFingerprint> RecentFingerprints { get; } = new();

        public HashSet<ITranscriptGeometrySource> ObservedGeometrySources { get; }
            = new(ReferenceEqualityComparer.Instance);

        public string MarkdownRevisions { get; set; } = string.Empty;

        public int CollapseClamps { get; set; }

        public double HeaderDisplacement { get; set; }

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsTerminal => Status != TranscriptViewportMutationStatus.Active;

        public CancellationToken WatchdogCancellationToken => _watchdogCancellation.Token;

        public IDisposable? TakeViewportAuthorityLease()
            => Interlocked.Exchange(ref _viewportAuthorityLease, null);

        public void ReleaseResources()
        {
            _watchdogCancellation.Cancel();
            Interlocked.Exchange(ref _viewportAuthorityLease, null)?.Dispose();
            _watchdogCancellation.Dispose();
        }
    }

    private readonly record struct ViewportGeometryFingerprint(
        long ExtentWidth,
        long ExtentHeight,
        long ViewportWidth,
        long ViewportHeight,
        long ItemsWidth,
        long ItemsHeight,
        long AnchorTop,
        bool AnchorAvailable,
        int GeometrySourceCount,
        long GeometrySourceFingerprint,
        bool GeometryPending);
}
