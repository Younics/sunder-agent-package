using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed class TranscriptScrollAnchorHost : Grid, IScrollAnchorProvider
{
    private readonly HashSet<Control> _registeredRowCandidates = [];
    private readonly HashSet<Control> _forwardedCandidates = [];
    private IScrollAnchorProvider? _outerProvider;
    private Control? _tailAnchor;
    private bool _isFollowingTail = true;

    Control? IScrollAnchorProvider.CurrentAnchor
    {
        get
        {
            var anchor = _outerProvider?.CurrentAnchor;
            return anchor is not null && _forwardedCandidates.Contains(anchor) ? anchor : null;
        }
    }

    public void SetTailAnchor(Control tailAnchor)
    {
        ArgumentNullException.ThrowIfNull(tailAnchor);
        if (ReferenceEquals(_tailAnchor, tailAnchor))
        {
            return;
        }

        if (_tailAnchor is not null)
        {
            StopForwarding(_tailAnchor);
        }

        _tailAnchor = tailAnchor;
        ReconcileForwardedCandidates();
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

        _registeredRowCandidates.Add(element);
        if (!_isFollowingTail)
        {
            StartForwarding(element);
        }
    }

    void IScrollAnchorProvider.UnregisterAnchorCandidate(Control element)
    {
        ArgumentNullException.ThrowIfNull(element);
        _registeredRowCandidates.Remove(element);
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

        var desiredCandidates = _isFollowingTail
            ? _tailAnchor is null ? [] : new HashSet<Control> { _tailAnchor }
            : _registeredRowCandidates;
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

        _outerProvider.RegisterAnchorCandidate(candidate);
    }

    private void StopForwarding(Control candidate)
    {
        if (_outerProvider is not null && _forwardedCandidates.Remove(candidate))
        {
            _outerProvider.UnregisterAnchorCandidate(candidate);
        }
    }

    private void StopForwardingAll()
    {
        if (_outerProvider is not null)
        {
            foreach (var candidate in _forwardedCandidates)
            {
                _outerProvider.UnregisterAnchorCandidate(candidate);
            }
        }

        _forwardedCandidates.Clear();
    }
}
