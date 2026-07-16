using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed class TranscriptRowPresenter : ContentControl
{
    public static readonly StyledProperty<object?> AnchorKeyProperty =
        AvaloniaProperty.Register<TranscriptRowPresenter, object?>(nameof(AnchorKey));

    public static readonly StyledProperty<bool> IsRepeaterHostedProperty =
        AvaloniaProperty.Register<TranscriptRowPresenter, bool>(nameof(IsRepeaterHosted));

    public object? AnchorKey
    {
        get => GetValue(AnchorKeyProperty);
        set => SetValue(AnchorKeyProperty, value);
    }

    public bool IsRepeaterHosted
    {
        get => GetValue(IsRepeaterHostedProperty);
        set => SetValue(IsRepeaterHostedProperty, value);
    }

    private ScrollViewer? _scrollViewer;
    private bool _isManuallyRegistered;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _scrollViewer = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        _isManuallyRegistered = _scrollViewer is not null
            && !IsRepeaterHosted;
        if (_isManuallyRegistered)
        {
            _scrollViewer!.RegisterAnchorCandidate(this);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_isManuallyRegistered)
        {
            _scrollViewer?.UnregisterAnchorCandidate(this);
        }

        _isManuallyRegistered = false;
        _scrollViewer = null;

        base.OnDetachedFromVisualTree(e);
    }
}
