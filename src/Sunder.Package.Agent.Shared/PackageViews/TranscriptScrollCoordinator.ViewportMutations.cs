using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptScrollCoordinator
{
    private const int ViewportMutationMaxPasses = 24;
    private const int ViewportMutationMaxCorrections = 8;
    private const int ViewportMutationStablePasses = 2;
    private const double ViewportMutationCorrectionEpsilon = 0.5;
    private static readonly TimeSpan ViewportMutationWatchdogTimeout = TimeSpan.FromSeconds(2);

    private ViewportMutationTransaction? _activeViewportMutation;
    private TranscriptViewportMutationDiagnostic? _lastViewportMutation;
    private long _viewportMutationGeneration;
    private long _viewportAuthorityRevision;
    private long _viewportMutationSignalSequence;
    private long _programmaticOffsetWriteCount;
    private TranscriptProgrammaticOffsetWriteDiagnostic? _lastProgrammaticOffsetWrite;
    private Task _viewportMutationOperation = Task.CompletedTask;
    private Task _viewportMutationCompletionOperation = Task.CompletedTask;
    private Task _viewportMutationWatchdogOperation = Task.CompletedTask;
    private IDisposable? _settledExactAnchorLease;

    internal TranscriptScrollDiagnosticSnapshot DiagnosticSnapshot
        => new(
            _interactionRevision,
            _viewportAuthorityRevision,
            _programmaticOffsetWriteCount,
            _anchorHost?.AnchoringSuspensionDepth ?? 0,
            HasPendingRenderedContent(),
            _bottomPlacementLockActive,
            _restoreAnchorPending,
            _isRestoringAnchor,
            _anchorHost?.HasExactAnchorLease == true,
            _anchorHost?.IsTrailingCompensatorActive == true,
            _lastProgrammaticOffsetWrite,
            _activeViewportMutation is { } active
                ? CreateMutationDiagnostic(active)
                : _lastViewportMutation);

    private ViewportMutationTransaction? BeginViewportMutationTransaction(
        TranscriptViewportMutationKind kind,
        object? preferredAnchorKey = null,
        Control? scope = null,
        bool? isExpanding = null,
        bool startWatchdog = true)
    {
        if (_disposed || !_presentationActive)
        {
            return null;
        }

        var wasFollowingTail = IsFollowingTail;
        var exactToolAnchor = kind == TranscriptViewportMutationKind.ToolExpansion
                              && preferredAnchorKey is not null
                              && _anchorHost is not null
            ? CaptureExactMutationAnchor(preferredAnchorKey, scope)
            : null;
        var mode = exactToolAnchor is not null
            ? TranscriptViewportMutationMode.ToolExpansionNativeAnchor
            : wasFollowingTail
                ? TranscriptViewportMutationMode.FollowingNativeObserver
                : TranscriptViewportMutationMode.DetachedLogicalRestore;
        var protectedAnchor = mode switch
        {
            TranscriptViewportMutationMode.ToolExpansionNativeAnchor =>
                exactToolAnchor,
            TranscriptViewportMutationMode.DetachedLogicalRestore =>
                CaptureRealizedMutationAnchor(preferredAnchorKey, scope),
            _ => null,
        };
        var authorityRevision = SupersedeViewportMutationForAuthority(
            TranscriptViewportMutationStatus.Superseded);
        if (mode == TranscriptViewportMutationMode.ToolExpansionNativeAnchor
            && preferredAnchorKey is not null
            && (_loadOlderPending || _loadNewerPending))
        {
            Volatile.Write(ref _activePageProtectedAnchorKey, preferredAnchorKey);
        }
        _anchorHost?.ReleaseTrailingCompensator();
        ClearPendingAnchor();
        ReleaseOlderPagingAnchor();
        ReleaseNewerPagingAnchor();
        var viewportAuthorityLease = mode switch
        {
            TranscriptViewportMutationMode.ToolExpansionNativeAnchor =>
                _anchorHost!.AcquireExactAnchorLease(preferredAnchorKey!),
            TranscriptViewportMutationMode.DetachedLogicalRestore =>
                _anchorHost?.SuspendAnchoring(),
            _ => null,
        };
        var generation = ++_viewportMutationGeneration;
        if (mode == TranscriptViewportMutationMode.ToolExpansionNativeAnchor
                  && isExpanding == false)
        {
            _anchorHost!.BeginTrailingCompensator(preferredAnchorKey!, generation);
        }
        var transaction = new ViewportMutationTransaction(
            generation,
            _interactionRevision,
            authorityRevision,
            kind,
            mode,
            scope,
            preferredAnchorKey,
            protectedAnchor,
            isExpanding,
            wasFollowingTail,
            viewportAuthorityLease);

        _activeViewportMutation = transaction;
        _viewportMutationCompletionOperation = transaction.Completion.Task;
        if (mode == TranscriptViewportMutationMode.FollowingNativeObserver)
        {
            _anchorHost?.SetFollowingTail(true);
        }
        else if (mode == TranscriptViewportMutationMode.DetachedLogicalRestore)
        {
            _anchorHost?.SetFollowingTail(false);
        }

        if (startWatchdog)
        {
            StartViewportMutationWatchdog(transaction);
        }
        return transaction;
    }

    private void StartViewportMutationWatchdog(ViewportMutationTransaction transaction)
    {
        if (transaction.WatchdogStarted)
        {
            return;
        }
        transaction.WatchdogStarted = true;
        var operation = AwaitViewportMutationWatchdogAsync(transaction);
        _viewportMutationWatchdogOperation = ObservePagingOperationAsync(operation);
    }

    private async Task AwaitViewportMutationWatchdogAsync(ViewportMutationTransaction transaction)
    {
        var cancellationToken = transaction.WatchdogCancellationToken;
        try
        {
            await _waitForViewportMutationWatchdog(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (IsCurrentViewportMutation(transaction))
            {
                TerminalizeViewportMutation(
                    transaction,
                    TranscriptViewportMutationStatus.BudgetExhausted);
            }
        }, DispatcherPriority.Background);
    }

    private static Task WaitForDefaultViewportMutationWatchdogAsync(
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
        => Task.Delay(ViewportMutationWatchdogTimeout, timeProvider, cancellationToken);

    private ItemAnchor? CaptureRealizedMutationAnchor(object? preferredAnchorKey, Control? scope)
    {
        var anchors = CaptureItemAnchors();
        if (anchors.Count == 0)
        {
            return null;
        }

        if (preferredAnchorKey is not null
            && anchors.FirstOrDefault(anchor => Equals(anchor.Item, preferredAnchorKey)) is { } preferred)
        {
            return preferred;
        }

        if (FindTransientRowPresenter(scope) is
            {
                AnchorKey: { } scopeKey,
            }
            && anchors.FirstOrDefault(anchor => Equals(anchor.Item, scopeKey)) is { } scoped)
        {
            return scoped;
        }

        var current = CaptureCurrentViewportAnchor(anchors);
        return current ?? anchors[0];
    }

    private ItemAnchor? CaptureExactMutationAnchor(
        object anchorKey,
        Control? scope)
    {
        var presenter = scope as TranscriptRowPresenter
                        ?? scope?.GetVisualAncestors().OfType<TranscriptRowPresenter>().FirstOrDefault();
        if (presenter is not
            {
                AnchorRole: TranscriptAnchorItemRole.Transient,
                AnchorKey: { } presenterKey,
            }
            || !Equals(anchorKey, presenterKey)
            || !presenter.IsAttachedToVisualTree()
            || _anchorHost?.IsVisualAncestorOf(presenter) != true)
        {
            return null;
        }

        return TryGetTop(presenter, out var top)
            ? new ItemAnchor(anchorKey, top, top + presenter.Bounds.Height)
            : null;
    }

    private static TranscriptRowPresenter? FindTransientRowPresenter(Control? scope)
    {
        var presenter = scope as TranscriptRowPresenter
                        ?? scope?.GetVisualAncestors().OfType<TranscriptRowPresenter>().FirstOrDefault();
        return presenter?.AnchorRole == TranscriptAnchorItemRole.Transient ? presenter : null;
    }

    private void SignalViewportMutation(
        ViewportMutationSignalCause cause,
        ITranscriptGeometrySource? source = null)
    {
        var transaction = _activeViewportMutation;
        if (transaction is null
            || transaction.IsTerminal
            || !transaction.WatchdogStarted
            || source is not null && !IsGeometrySourceInScope(transaction, source))
        {
            return;
        }

        transaction.SignalSequence = ++_viewportMutationSignalSequence;
        QueueViewportMutationPass(transaction, cause);
    }

    private static bool IsGeometrySourceInScope(
        ViewportMutationTransaction transaction,
        ITranscriptGeometrySource source)
    {
        if (transaction.Scope is null || source is not Visual sourceVisual)
        {
            return true;
        }

        return transaction.ObservedGeometrySources.Contains(source)
               || ReferenceEquals(transaction.Scope, sourceVisual)
               || transaction.Scope.IsVisualAncestorOf(sourceVisual);
    }

    private void QueueViewportMutationPass(
        ViewportMutationTransaction transaction,
        ViewportMutationSignalCause cause)
    {
        if (!IsCurrentViewportMutation(transaction) || transaction.CallbackQueued)
        {
            return;
        }

        transaction.CallbackQueued = true;
        var operation = InvokeOnDispatcherAsync(
            () => ProcessViewportMutationPass(transaction, cause),
            DispatcherPriority.Render);
        _viewportMutationOperation = ObservePagingOperationAsync(operation);
    }

    private void ProcessViewportMutationPass(
        ViewportMutationTransaction transaction,
        ViewportMutationSignalCause cause,
        bool queueStabilityConfirmation = true)
    {
        transaction.CallbackQueued = false;
        if (!IsCurrentViewportMutation(transaction))
        {
            return;
        }

        if (transaction.Passes >= ViewportMutationMaxPasses)
        {
            TerminalizeViewportMutation(transaction, TranscriptViewportMutationStatus.BudgetExhausted);
            return;
        }

        transaction.Passes++;
        if (transaction.Mode == TranscriptViewportMutationMode.DetachedLogicalRestore
            && transaction.ProtectedAnchor is { } anchorToRealize
            && !TryGetRealizedAnchorTop(anchorToRealize.Item, out _))
        {
            _ = _realizeAnchorVisual?.Invoke(anchorToRealize.Item);
        }
        var fingerprint = CaptureViewportGeometry(transaction);
        if (transaction.Mode == TranscriptViewportMutationMode.DetachedLogicalRestore
            && transaction.ProtectedAnchor is { } protectedAnchor
            && fingerprint.AnchorAvailable
            && transaction.LastCorrectionFingerprint != fingerprint)
        {
            transaction.LastCorrectionFingerprint = fingerprint;
            if (TryGetRealizedAnchorTop(protectedAnchor.Item, out var currentTop))
            {
                var correction = currentTop - protectedAnchor.Top;
                if (Math.Abs(correction) > ViewportMutationCorrectionEpsilon)
                {
                    if (transaction.Corrections >= ViewportMutationMaxCorrections)
                    {
                        TerminalizeViewportMutation(
                            transaction,
                            TranscriptViewportMutationStatus.BudgetExhausted);
                        return;
                    }

                    if (TrySetViewportMutationOffset(
                            transaction,
                            _scrollViewer.Offset.Y + correction))
                    {
                        transaction.Corrections++;
                        fingerprint = CaptureViewportGeometry(transaction);
                    }
                }
            }
        }

        ReconcileToolExpansionFollowState(transaction);

        if (DetectViewportMutationCycle(transaction, fingerprint))
        {
            TerminalizeViewportMutation(transaction, TranscriptViewportMutationStatus.CycleDetected);
            return;
        }

        if (!fingerprint.GeometryPending && transaction.LastFingerprint == fingerprint)
        {
            transaction.StablePasses++;
        }
        else
        {
            transaction.StablePasses = 0;
        }
        transaction.LastFingerprint = fingerprint;
        transaction.TerminalEpochs = fingerprint.GeometryPending ? 0 : transaction.StablePasses + 1;

        if (transaction.StablePasses >= ViewportMutationStablePasses)
        {
            var status = transaction.Mode == TranscriptViewportMutationMode.DetachedLogicalRestore
                         && transaction.ProtectedAnchor is not null
                         && !fingerprint.AnchorAvailable
                ? TranscriptViewportMutationStatus.AnchorUnavailable
                : TranscriptViewportMutationStatus.Completed;
            TerminalizeViewportMutation(transaction, status);
            return;
        }

        if (transaction.Passes >= ViewportMutationMaxPasses)
        {
            TerminalizeViewportMutation(transaction, TranscriptViewportMutationStatus.BudgetExhausted);
            return;
        }

        if (!fingerprint.GeometryPending && queueStabilityConfirmation)
        {
            QueueViewportMutationPass(transaction, ViewportMutationSignalCause.StabilityConfirmation);
        }
    }

    private bool TryCompleteSynchronousToolCollapse()
    {
        if (_activeViewportMutation is not
            {
                Kind: TranscriptViewportMutationKind.ToolExpansion,
                Mode: TranscriptViewportMutationMode.ToolExpansionNativeAnchor,
                IsExpanding: false,
            } transaction
            || !IsCurrentViewportMutation(transaction))
        {
            return false;
        }

        transaction.SignalSequence = ++_viewportMutationSignalSequence;
        if (EnumerateGeometrySources(transaction).Any(source => source.IsGeometryPending))
        {
            QueueViewportMutationPass(transaction, ViewportMutationSignalCause.ContentChanged);
            return true;
        }
        for (var pass = 0;
             pass <= ViewportMutationStablePasses && IsCurrentViewportMutation(transaction);
             pass++)
        {
            _anchorHost?.UpdateLayout();
            _scrollViewer.UpdateLayout();
            ProcessViewportMutationPass(
                transaction,
                ViewportMutationSignalCause.ContentChanged,
                queueStabilityConfirmation: false);
            if (transaction.LastFingerprint?.GeometryPending == true)
            {
                QueueViewportMutationPass(transaction, ViewportMutationSignalCause.ContentChanged);
                break;
            }
        }
        return true;
    }

    private ViewportGeometryFingerprint CaptureViewportGeometry(ViewportMutationTransaction transaction)
    {
        var anchorTop = 0d;
        var anchorAvailable = transaction.ProtectedAnchor is { } protectedAnchor
                              && TryGetMutationAnchorTop(transaction, protectedAnchor.Item, out anchorTop);
        var sources = EnumerateGeometrySources(transaction).ToArray();
        var sourceFingerprint = 17L;
        var geometryPending = false;
        var sourceRevisions = new List<string>(sources.Length);
        foreach (var source in sources)
        {
            transaction.ObservedGeometrySources.Add(source);
            sourceFingerprint = unchecked(sourceFingerprint * 31 + source.RequestedRevision);
            sourceFingerprint = unchecked(sourceFingerprint * 31 + source.SettledRevision);
            sourceFingerprint = unchecked(sourceFingerprint * 31 + source.GeometryRevision);
            sourceFingerprint = unchecked(sourceFingerprint * 31 + (source.IsGeometryPending ? 1 : 0));
            geometryPending |= source.IsGeometryPending;
            sourceRevisions.Add(
                $"{source.GetType().Name}:r{source.RequestedRevision}/s{source.SettledRevision}/g{source.GeometryRevision}/p{source.IsGeometryPending}");
        }
        transaction.MarkdownRevisions = string.Join(",", sourceRevisions);

        return new ViewportGeometryFingerprint(
            QuantizeGeometry(_scrollViewer.Extent.Width),
            QuantizeGeometry(_scrollViewer.Extent.Height),
            QuantizeGeometry(_scrollViewer.Viewport.Width),
            QuantizeGeometry(_scrollViewer.Viewport.Height),
            QuantizeGeometry(_itemsControl?.Bounds.Width ?? 0),
            QuantizeGeometry(_itemsControl?.Bounds.Height ?? 0),
            QuantizeGeometry(anchorAvailable ? anchorTop : 0),
            anchorAvailable,
            sources.Length,
            sourceFingerprint,
            geometryPending);
    }

    private IEnumerable<ITranscriptGeometrySource> EnumerateGeometrySources(
        ViewportMutationTransaction transaction)
        => EnumerateGeometrySources(transaction.Scope);

    private IEnumerable<ITranscriptGeometrySource> EnumerateGeometrySources(Control? scope)
    {
        var root = scope ?? _itemsControl ?? _scrollViewer;
        if (root is ITranscriptGeometrySource rootSource
            && root.IsEffectivelyVisible)
        {
            yield return rootSource;
        }

        foreach (var source in root.GetVisualDescendants()
                     .OfType<ITranscriptGeometrySource>()
                     .Where(source => source is not Visual visual || visual.IsEffectivelyVisible))
        {
            yield return source;
        }
    }

    private bool TryGetRealizedAnchorTop(object anchorKey, out double top)
    {
        foreach (var (item, visual) in EnumerateRowAnchorVisuals())
        {
            if (Equals(item, anchorKey) && TryGetTop(visual, out top))
            {
                return true;
            }
        }

        top = 0;
        return false;
    }

    private bool TryGetMutationAnchorTop(
        ViewportMutationTransaction transaction,
        object anchorKey,
        out double top)
    {
        if (transaction.Mode != TranscriptViewportMutationMode.ToolExpansionNativeAnchor)
        {
            return TryGetRealizedAnchorTop(anchorKey, out top);
        }

        var presenter = transaction.Scope as TranscriptRowPresenter
                        ?? transaction.Scope?.GetVisualAncestors()
                            .OfType<TranscriptRowPresenter>()
                            .FirstOrDefault();
        if (presenter is not
            {
                AnchorRole: TranscriptAnchorItemRole.Transient,
                AnchorKey: { } presenterKey,
            }
            || !Equals(anchorKey, presenterKey))
        {
            top = 0;
            return false;
        }

        return TryGetTop(presenter, out top);
    }

    private void ReconcileToolExpansionFollowState(ViewportMutationTransaction transaction)
    {
        if (transaction.Mode != TranscriptViewportMutationMode.ToolExpansionNativeAnchor
            || !transaction.WasFollowingTail
            || _userDetached
            || _anchorHost is null)
        {
            return;
        }

        if (_anchorHost.IsTailVisible(_scrollViewer))
        {
            return;
        }

        DetachFromLatestForUser();
        UpdateJumpToLatestVisibility();
    }

    private bool TrySetViewportMutationOffset(
        ViewportMutationTransaction transaction,
        double offsetY)
    {
        if (!IsCurrentViewportMutation(transaction)
            || transaction.Mode != TranscriptViewportMutationMode.DetachedLogicalRestore)
        {
            return false;
        }

        var clampedOffset = Math.Clamp(offsetY, 0, MaxOffsetY());
        if (Math.Abs(_scrollViewer.Offset.Y - clampedOffset) <= 0.01)
        {
            return false;
        }

        SetProgrammaticOffset(
            clampedOffset,
            TranscriptProgrammaticOffsetWriteSource.ViewportMutationCorrection);
        return true;
    }

    private bool IsCurrentViewportMutation(ViewportMutationTransaction transaction)
        => !_disposed
           && _presentationActive
           && ReferenceEquals(_activeViewportMutation, transaction)
           && !transaction.IsTerminal
           && transaction.InteractionRevision == _interactionRevision
           && transaction.AuthorityRevision == _viewportAuthorityRevision;

    private static bool DetectViewportMutationCycle(
        ViewportMutationTransaction transaction,
        ViewportGeometryFingerprint fingerprint)
    {
        transaction.RecentFingerprints.Enqueue(fingerprint);
        while (transaction.RecentFingerprints.Count > 4)
        {
            transaction.RecentFingerprints.Dequeue();
        }

        if (transaction.RecentFingerprints.Count < 4)
        {
            return false;
        }

        var recent = transaction.RecentFingerprints.ToArray();
        return IsSameCycleGeometry(recent[0], recent[2])
               && IsSameCycleGeometry(recent[1], recent[3])
               && !IsSameCycleGeometry(recent[0], recent[1]);
    }

    private static bool IsSameCycleGeometry(
        ViewportGeometryFingerprint left,
        ViewportGeometryFingerprint right)
        => left.ExtentWidth == right.ExtentWidth
           && left.ExtentHeight == right.ExtentHeight
           && left.ViewportWidth == right.ViewportWidth
           && left.ViewportHeight == right.ViewportHeight
           && left.ItemsWidth == right.ItemsWidth
           && left.ItemsHeight == right.ItemsHeight;

    private long SupersedeViewportMutationForAuthority(
        TranscriptViewportMutationStatus status = TranscriptViewportMutationStatus.CanceledByAuthority)
    {
        ReleaseSettledExactAnchorLease();
        _viewportAuthorityRevision++;
        if (_activeViewportMutation is { } active)
        {
            TerminalizeViewportMutation(active, status);
        }

        return _viewportAuthorityRevision;
    }

    private void TerminalizeViewportMutation(
        ViewportMutationTransaction transaction,
        TranscriptViewportMutationStatus status)
    {
        if (transaction.IsTerminal)
        {
            return;
        }

        transaction.Status = status;
        if (ReferenceEquals(_activeViewportMutation, transaction))
        {
            _activeViewportMutation = null;
        }
        FinalizeTrailingCompensator(transaction);
        if (ShouldRetainExactAnchorLease(transaction, status))
        {
            var retainedLease = transaction.TakeViewportAuthorityLease();
            Interlocked.Exchange(ref _settledExactAnchorLease, retainedLease)?.Dispose();
        }
        _lastViewportMutation = CreateMutationDiagnostic(transaction);
        transaction.ReleaseResources();
        _anchorHost?.SetFollowingTail(IsFollowingTail);
        if (status is TranscriptViewportMutationStatus.Completed
            or TranscriptViewportMutationStatus.AnchorUnavailable
            or TranscriptViewportMutationStatus.BudgetExhausted
            or TranscriptViewportMutationStatus.CycleDetected)
        {
            QueueReevaluatePagingEdges(transaction.AuthorityRevision);
        }
        transaction.Completion.TrySetResult();
    }

    private static bool ShouldRetainExactAnchorLease(
        ViewportMutationTransaction transaction,
        TranscriptViewportMutationStatus status)
        => transaction.Mode == TranscriptViewportMutationMode.ToolExpansionNativeAnchor
           && transaction.IsExpanding != false
           && status == TranscriptViewportMutationStatus.Completed;

    private void ReleaseSettledExactAnchorLease()
        => Interlocked.Exchange(ref _settledExactAnchorLease, null)?.Dispose();

    private void FinalizeTrailingCompensator(ViewportMutationTransaction transaction)
    {
        if (_anchorHost is null
            || transaction.Kind != TranscriptViewportMutationKind.ToolExpansion
            || transaction.IsExpanding != false)
        {
            _anchorHost?.ReleaseTrailingCompensator(transaction.Generation);
            return;
        }

        var previousHeaderTop = transaction.ProtectedAnchor is { } protectedAnchor
                                && TryGetMutationAnchorTop(transaction, protectedAnchor.Item, out var capturedTop)
            ? capturedTop
            : (double?)null;
        var detailHost = transaction.Scope?.GetVisualDescendants()
            .OfType<TranscriptToolDetailHost>()
            .FirstOrDefault();
        if (transaction.Status == TranscriptViewportMutationStatus.Superseded)
        {
            detailHost?.ReleaseCollapseCompensator();
            _anchorHost.ReleaseTrailingCompensator(transaction.Generation);
            ObserveTerminalHeaderDisplacement(transaction, previousHeaderTop);
            return;
        }
        using (_anchorHost.SuspendAnchoring())
        {
            transaction.TakeViewportAuthorityLease()?.Dispose();
            detailHost?.ReleaseCollapseCompensator();
            _anchorHost.UpdateLayout();
            _scrollViewer.UpdateLayout();
            var naturalMaximumOffset = Math.Max(
                0,
                _scrollViewer.Extent.Height
                - _anchorHost.TrailingCompensatorHeight
                - _scrollViewer.Viewport.Height);
            var requiresClamp = _scrollViewer.Offset.Y > naturalMaximumOffset + 0.01;
            _anchorHost.ReleaseTrailingCompensator(transaction.Generation);
            if (requiresClamp)
            {
                SetProgrammaticOffset(
                    naturalMaximumOffset,
                    TranscriptProgrammaticOffsetWriteSource.ToolCollapseClamp);
                transaction.CollapseClamps++;
                TranscriptToolDiagnostics.CollapseClamped();
            }
            _anchorHost.UpdateLayout();
            _scrollViewer.UpdateLayout();
        }

        ObserveTerminalHeaderDisplacement(transaction, previousHeaderTop);
    }

    private void ObserveTerminalHeaderDisplacement(
        ViewportMutationTransaction transaction,
        double? previousHeaderTop)
    {
        if (previousHeaderTop is { } before
            && transaction.ProtectedAnchor is { } anchor
            && TryGetMutationAnchorTop(transaction, anchor.Item, out var after))
        {
            transaction.HeaderDisplacement = after - before;
            TranscriptToolDiagnostics.ObserveHeaderDisplacement(transaction.HeaderDisplacement);
        }
    }

    private static TranscriptViewportMutationDiagnostic CreateMutationDiagnostic(
        ViewportMutationTransaction transaction)
        => new(
            transaction.Generation,
            transaction.InteractionRevision,
            transaction.AuthorityRevision,
            transaction.Kind,
            transaction.Mode,
            transaction.Status,
            transaction.Passes,
            transaction.Corrections,
            transaction.StablePasses,
            transaction.TerminalEpochs,
            transaction.SignalSequence,
            transaction.ProtectedAnchor?.Item,
            transaction.LastFingerprint?.GeometryPending ?? false,
            string.Join(
                " | ",
                transaction.RecentFingerprints.Select(fingerprint =>
                    $"e={fingerprint.ExtentHeight},v={fingerprint.ViewportHeight},i={fingerprint.ItemsHeight},a={fingerprint.AnchorTop},s={fingerprint.GeometrySourceFingerprint},p={fingerprint.GeometryPending}")),
            transaction.MarkdownRevisions,
            transaction.CollapseClamps,
            transaction.HeaderDisplacement);

    private static long QuantizeGeometry(double value)
        => double.IsFinite(value) ? checked((long)Math.Round(value * 4)) : 0;

    private enum ViewportMutationSignalCause
    {
        ContentChanged,
        ExtentChanged,
        RenderedContent,
        StabilityConfirmation,
    }
}
