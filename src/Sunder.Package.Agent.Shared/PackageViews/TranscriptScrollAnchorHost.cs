using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed class TranscriptScrollAnchorHost : Grid, IScrollAnchorProvider
{
    private readonly Dictionary<Control, TranscriptRowPresenter> _registeredCandidates = [];
    private readonly HashSet<Control> _forwardedCandidates = [];
    private IScrollAnchorProvider? _outerProvider;
    private bool _isFollowingTail = true;
    private readonly List<AnchoringSuspension> _anchoringSuspensions = [];
    private object? _exactAnchorKey;
    private long _exactAnchorLeaseRevision;
    private double? _trailingCompensatorExtent;
    private long _trailingCompensatorGeneration;
    private long _trailingCompensatorStartedAt;
    private int _preservedRealizationDepth;

    public TranscriptScrollAnchorHost()
    {
        AddHandler(RequestBringIntoViewEvent, OnRequestBringIntoView, RoutingStrategies.Bubble);
    }

    // ItemsRepeater needs the request to retire its temporary made-anchor. Let
    // the repeater consume that protocol, then stop it before the outer scroller.
    public IDisposable SuppressPreservedRealizationNavigation()
    {
        _preservedRealizationDepth++;
        return new PreservedRealizationScope(this);
    }

    private void OnRequestBringIntoView(object? sender, RequestBringIntoViewEventArgs e)
    {
        if (_preservedRealizationDepth > 0) e.Handled = true;
    }

    internal bool IsAnchoringSuspended => _anchoringSuspensions.Count > 0;

    internal int AnchoringSuspensionDepth => _anchoringSuspensions.Count;

    internal bool HasExactAnchorLease => _exactAnchorKey is not null;

    internal object? ExactAnchorKey => _exactAnchorKey;

    internal bool HasTrailingCompensator => _trailingCompensatorExtent is not null;

    internal bool IsTrailingCompensatorActive
        => TrailingCompensatorHeight > 0.1;

    internal double TrailingCompensatorHeight
        => _trailingCompensatorExtent is { } extent
            ? Math.Max(0, extent - NaturalContentHeight())
            : 0;

    Control? IScrollAnchorProvider.CurrentAnchor
    {
        get
        {
            // Suspending native offset writes must not remove the repeater's
            // reading anchor. It still needs that row to survive reflow/recycling.
            foreach (var suspension in _anchoringSuspensions.AsEnumerable().Reverse())
            {
                if (suspension.ResolveRealizationAnchor?.Invoke() is { } key
                    && FindRealizationAnchor(key) is { } preserved)
                {
                    return preserved;
                }
            }
            var anchor = _outerProvider?.CurrentAnchor;
            return anchor is not null && _forwardedCandidates.Contains(anchor) ? anchor : null;
        }
    }

    public IDisposable SuspendAnchoring(Func<object?>? resolveRealizationAnchor = null)
    {
        var suspension = new AnchoringSuspension(this, resolveRealizationAnchor);
        _anchoringSuspensions.Add(suspension);
        ReconcileForwardedCandidates();
        if (resolveRealizationAnchor?.Invoke() is { } key
            && FindRealizationAnchor(key)?.GetVisualParent() is ItemsRepeater repeater)
        {
            repeater.InvalidateMeasure();
        }
        return suspension;
    }

    private TranscriptRowPresenter? FindRealizationAnchor(object key)
        => this.GetVisualDescendants().OfType<TranscriptRowPresenter>()
            .FirstOrDefault(presenter => presenter.AnchorRole is TranscriptAnchorItemRole.Transient or TranscriptAnchorItemRole.TailSentinel
                                         && Equals(presenter.AnchorKey, key));

    public IDisposable AcquireExactAnchorLease(object anchorKey)
    {
        ArgumentNullException.ThrowIfNull(anchorKey);
        var revision = ++_exactAnchorLeaseRevision;
        _exactAnchorKey = anchorKey;
        ReconcileForwardedCandidates();
        return new ExactAnchorLease(this, revision);
    }

    public void BeginTrailingCompensator(object anchorKey, long generation)
    {
        var hasExactAnchor = _registeredCandidates.Values
            .Distinct()
            .Any(presenter =>
                presenter.AnchorRole == TranscriptAnchorItemRole.Transient
                && Equals(presenter.AnchorKey, anchorKey));
        if (!hasExactAnchor)
        {
            return;
        }

        var currentHeight = Bounds.Height;
        if (!double.IsFinite(currentHeight) || currentHeight <= 0)
        {
            return;
        }

        ReleaseTrailingCompensator();
        _trailingCompensatorGeneration = generation;
        _trailingCompensatorExtent = currentHeight;
        _trailingCompensatorStartedAt = TranscriptToolDiagnostics.SpacerActivated();
        InvalidateMeasure();
    }

    public void ReleaseTrailingCompensator(long? generation = null)
    {
        if (_trailingCompensatorExtent is null
            || generation is { } expectedGeneration
               && expectedGeneration != _trailingCompensatorGeneration)
        {
            return;
        }
        _trailingCompensatorExtent = null;
        _trailingCompensatorGeneration = 0;
        TranscriptToolDiagnostics.SpacerReleased(_trailingCompensatorStartedAt);
        _trailingCompensatorStartedAt = 0;
        InvalidateMeasure();
    }

    public bool IsTailVisible(Visual viewport)
        => IsTailVisibleAfterVerticalShift(viewport, 0);

    public bool IsTailVisibleAfterVerticalShift(Visual viewport, double shift)
    {
        var viewportBounds = new Rect(viewport.Bounds.Size);
        foreach (var presenter in _registeredCandidates.Values.Distinct())
        {
            if (presenter.AnchorRole != TranscriptAnchorItemRole.TailSentinel
                || !presenter.IsEffectivelyVisible
                || presenter.TranslatePoint(default, viewport) is not { } topLeft)
            {
                continue;
            }

            if (new Rect(new Point(topLeft.X, topLeft.Y + shift), presenter.Bounds.Size)
                .Intersects(viewportBounds))
            {
                return true;
            }
        }

        return false;
    }

    public void SetFollowingTail(bool isFollowingTail)
    {
        if (_isFollowingTail == isFollowingTail)
        {
            return;
        }

        _isFollowingTail = isFollowingTail;
        ReconcileForwardedCandidates();
    }

    void IScrollAnchorProvider.RegisterAnchorCandidate(Control element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!this.IsVisualAncestorOf(element))
        {
            throw new InvalidOperationException("A transcript anchor candidate must be a descendant of its host.");
        }

        if (_registeredCandidates.ContainsKey(element))
        {
            return;
        }

        var presenter = FindPresenter(element)
                        ?? throw new InvalidOperationException(
                            "A transcript anchor candidate must have a TranscriptRowPresenter.");
        _registeredCandidates.Add(element, presenter);
        if (_registeredCandidates.Values.Count(candidate => ReferenceEquals(candidate, presenter)) == 1)
        {
            presenter.PropertyChanged += OnPresenterPropertyChanged;
        }
        ReconcileForwardedCandidates();
    }

    void IScrollAnchorProvider.UnregisterAnchorCandidate(Control element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (_registeredCandidates.Remove(element, out var presenter)
            && !_registeredCandidates.Values.Any(candidate => ReferenceEquals(candidate, presenter)))
        {
            presenter.PropertyChanged -= OnPresenterPropertyChanged;
        }
        StopForwarding(element);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _outerProvider = this.GetVisualAncestors().OfType<IScrollAnchorProvider>().FirstOrDefault();
        ReconcileForwardedCandidates();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ReleaseTrailingCompensator();
        StopForwardingAll();
        _outerProvider = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void ReconcileForwardedCandidates()
    {
        if (_outerProvider is null)
        {
            return;
        }

        var desiredCandidates = IsAnchoringSuspended
            ? []
            : ResolveDesiredCandidates();
        foreach (var candidate in _forwardedCandidates.ToArray())
        {
            if (!desiredCandidates.Contains(candidate))
            {
                StopForwarding(candidate);
            }
        }

        foreach (var candidate in desiredCandidates)
        {
            StartForwarding(candidate);
        }
    }

    private void StartForwarding(Control candidate)
    {
        if (_outerProvider is null
            || !this.IsVisualAncestorOf(candidate)
            || !_forwardedCandidates.Add(candidate))
        {
            return;
        }

        candidate.DetachedFromVisualTree += OnForwardedCandidateDetached;
        try
        {
            _outerProvider.RegisterAnchorCandidate(candidate);
        }
        catch
        {
            candidate.DetachedFromVisualTree -= OnForwardedCandidateDetached;
            _forwardedCandidates.Remove(candidate);
            throw;
        }
    }

    private void StopForwarding(Control candidate)
    {
        if (!_forwardedCandidates.Remove(candidate))
        {
            return;
        }

        candidate.DetachedFromVisualTree -= OnForwardedCandidateDetached;
        if (_outerProvider is not null)
        {
            _outerProvider.UnregisterAnchorCandidate(candidate);
        }
    }

    private void OnForwardedCandidateDetached(object? sender, VisualTreeAttachmentEventArgs eventArgs)
    {
        if (sender is Control candidate)
        {
            StopForwarding(candidate);
        }
    }

    private void StopForwardingAll()
    {
        foreach (var candidate in _forwardedCandidates.ToArray())
        {
            StopForwarding(candidate);
        }
    }

    private TranscriptRowPresenter? FindPresenter(Control candidate)
        => candidate as TranscriptRowPresenter
           ?? candidate.GetVisualAncestors().OfType<TranscriptRowPresenter>().FirstOrDefault()
           ?? candidate.GetVisualDescendants().OfType<TranscriptRowPresenter>().FirstOrDefault();

    private void OnPresenterPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.Property == TranscriptRowPresenter.AnchorRoleProperty
            || eventArgs.Property == TranscriptRowPresenter.AnchorKeyProperty)
        {
            ReconcileForwardedCandidates();
        }
    }

    private bool IsDesiredCandidate(TranscriptRowPresenter presenter)
    {
        var desiredRole = _isFollowingTail
            ? TranscriptAnchorItemRole.TailSentinel
            : TranscriptAnchorItemRole.Transient;
        return presenter.AnchorRole == desiredRole;
    }

    private HashSet<Control> ResolveDesiredCandidates()
    {
        if (_exactAnchorKey is not { } exactAnchorKey)
        {
            return _registeredCandidates
                .Where(candidate => IsDesiredCandidate(candidate.Value))
                .Select(candidate => candidate.Key)
                .ToHashSet();
        }

        var exactPresenters = _registeredCandidates
            .Where(candidate => candidate.Value.AnchorRole == TranscriptAnchorItemRole.Transient
                                && Equals(candidate.Value.AnchorKey, exactAnchorKey))
            .Select(candidate => candidate.Value)
            .Distinct()
            .ToArray();
        var exactTargets = exactPresenters
            .SelectMany(presenter => presenter.GetVisualDescendants().OfType<Control>())
            .Where(TranscriptRowPresenter.GetIsExactAnchorTarget)
            .ToHashSet();
        return exactTargets.Count > 0
            ? exactTargets
            : _registeredCandidates
                .Where(candidate => exactPresenters.Contains(candidate.Value))
                .Select(candidate => candidate.Key)
                .ToHashSet();
    }

    private double NaturalContentHeight()
        => Children.Count == 0
            ? 0
            : Children.Max(child => child.DesiredSize.Height);

    protected override Size MeasureOverride(Size availableSize)
    {
        var measured = base.MeasureOverride(availableSize);
        return _trailingCompensatorExtent is { } extent
            ? new Size(measured.Width, Math.Max(measured.Height, extent))
            : measured;
    }

    private void ReleaseExactAnchor(long revision)
    {
        if (revision != _exactAnchorLeaseRevision || _exactAnchorKey is null)
        {
            return;
        }

        _exactAnchorKey = null;
        ReconcileForwardedCandidates();
    }

    private void ResumeAnchoring(AnchoringSuspension suspension)
    {
        if (!_anchoringSuspensions.Remove(suspension))
        {
            return;
        }

        ReconcileForwardedCandidates();
    }

    private sealed class AnchoringSuspension(TranscriptScrollAnchorHost host, Func<object?>? resolveRealizationAnchor) : IDisposable
    {
        private TranscriptScrollAnchorHost? _host = host;
        public Func<object?>? ResolveRealizationAnchor { get; } = resolveRealizationAnchor;

        public void Dispose()
            => Interlocked.Exchange(ref _host, null)?.ResumeAnchoring(this);
    }

    private sealed class PreservedRealizationScope(TranscriptScrollAnchorHost host) : IDisposable
    {
        private TranscriptScrollAnchorHost? _host = host;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _host, null) is { } owner) owner._preservedRealizationDepth--;
        }
    }

    private sealed class ExactAnchorLease(
        TranscriptScrollAnchorHost host,
        long revision) : IDisposable
    {
        private TranscriptScrollAnchorHost? _host = host;

        public void Dispose()
            => Interlocked.Exchange(ref _host, null)?.ReleaseExactAnchor(revision);
    }
}
