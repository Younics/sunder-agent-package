using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Sunder.Package.Agent.Shared.PackageViews;

public sealed class TranscriptRowPresenter : ContentControl
{
    public static readonly StyledProperty<object?> AnchorKeyProperty =
        AvaloniaProperty.Register<TranscriptRowPresenter, object?>(nameof(AnchorKey));

    public object? AnchorKey
    {
        get => GetValue(AnchorKeyProperty);
        set => SetValue(AnchorKeyProperty, value);
    }

    private ScrollViewer? _scrollViewer;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _scrollViewer = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        _scrollViewer?.RegisterAnchorCandidate(this);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _scrollViewer?.UnregisterAnchorCandidate(this);
        _scrollViewer = null;

        base.OnDetachedFromVisualTree(e);
    }
}
