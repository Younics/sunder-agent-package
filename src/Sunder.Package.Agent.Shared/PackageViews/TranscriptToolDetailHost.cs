using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal interface ITranscriptToolExpansionOwner
{
    event Action? DetailVisualInvalidated;

    void OnDetailVisualInvalidated();
}

internal sealed class TranscriptToolDetailHost : Panel
{
    private Control? _detailVisual;
    private ITranscriptToolExpansionOwner? _owner;
    private double _collapseCompensatorHeight;
    private bool _clearing;

    internal bool HasDetailVisual => _detailVisual is not null;

    internal void AttachPrepared(
        ITranscriptToolExpansionOwner owner,
        Control detailVisual)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(detailVisual);
        ReleaseCollapseCompensator();
        Clear(notifyOwner: false);
        _owner = owner;
        owner.DetailVisualInvalidated += OnDetailVisualInvalidated;
        _detailVisual = detailVisual;
        Children.Add(detailVisual);
        InvalidateMeasure();
    }

    internal void BeginCollapseCompensation()
    {
        var height = Bounds.Height;
        if (double.IsFinite(height) && height > 0)
        {
            _collapseCompensatorHeight = height;
            InvalidateMeasure();
        }
    }

    internal void ReleaseCollapseCompensator()
    {
        if (_collapseCompensatorHeight <= 0)
        {
            return;
        }
        _collapseCompensatorHeight = 0;
        InvalidateMeasure();
    }

    internal void Clear(
        bool notifyOwner = true,
        bool preserveCollapseCompensator = false)
    {
        if (_clearing)
        {
            return;
        }

        _clearing = true;
        var owner = _owner;
        _owner = null;
        if (owner is not null)
        {
            owner.DetailVisualInvalidated -= OnDetailVisualInvalidated;
        }
        try
        {
            if (_detailVisual is { } detailVisual)
            {
                _detailVisual = null;
                foreach (var markdown in detailVisual.GetVisualDescendants().OfType<StreamingMarkdownPresenter>())
                {
                    markdown.MarkdownBuilder = null;
                }
                Children.Remove(detailVisual);
                if (detailVisual is ContentPresenter presenter)
                {
                    presenter.Content = null;
                    presenter.ContentTemplate = null;
                }
                TranscriptToolDiagnostics.DetailVisualDestroyed();
            }
        }
        finally
        {
            _clearing = false;
        }

        if (notifyOwner)
        {
            owner?.OnDetailVisualInvalidated();
        }
        if (!preserveCollapseCompensator)
        {
            ReleaseCollapseCompensator();
        }
        InvalidateMeasure();
    }

    private void OnDetailVisualInvalidated()
        => Clear(notifyOwner: false);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_detailVisual is not { } detailVisual)
        {
            return new Size(
                double.IsFinite(availableSize.Width) ? availableSize.Width : 0,
                _collapseCompensatorHeight);
        }

        detailVisual.Measure(availableSize);
        return new Size(
            detailVisual.DesiredSize.Width,
            Math.Max(detailVisual.DesiredSize.Height, _collapseCompensatorHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _detailVisual?.Arrange(new Rect(finalSize));
        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataContextProperty
            && _owner is not null
            && !ReferenceEquals(DataContext, _owner))
        {
            Clear();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Clear();
        base.OnDetachedFromVisualTree(e);
    }
}
