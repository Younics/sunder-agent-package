using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed class TranscriptRowPresenter : ContentControl
{
    internal static readonly AttachedProperty<bool> IsExactAnchorTargetProperty =
        AvaloniaProperty.RegisterAttached<TranscriptRowPresenter, Control, bool>("IsExactAnchorTarget");

    public static readonly StyledProperty<object?> AnchorKeyProperty =
        AvaloniaProperty.Register<TranscriptRowPresenter, object?>(nameof(AnchorKey));

    public static readonly StyledProperty<bool> IsRepeaterHostedProperty =
        AvaloniaProperty.Register<TranscriptRowPresenter, bool>(nameof(IsRepeaterHosted));

    public static readonly StyledProperty<TranscriptAnchorItemRole> AnchorRoleProperty =
        AvaloniaProperty.Register<TranscriptRowPresenter, TranscriptAnchorItemRole>(nameof(AnchorRole));

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

    public TranscriptAnchorItemRole AnchorRole
    {
        get => GetValue(AnchorRoleProperty);
        set => SetValue(AnchorRoleProperty, value);
    }

    public static bool GetIsExactAnchorTarget(Control control)
        => control.GetValue(IsExactAnchorTargetProperty);

    public static void SetIsExactAnchorTarget(Control control, bool value)
        => control.SetValue(IsExactAnchorTargetProperty, value);

    private IScrollAnchorProvider? _anchorProvider;
    private bool _isManuallyRegistered;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _anchorProvider = this.GetVisualAncestors().OfType<IScrollAnchorProvider>().FirstOrDefault();
        _isManuallyRegistered = _anchorProvider is not null
            && !IsRepeaterHosted;
        if (_isManuallyRegistered)
        {
            _anchorProvider!.RegisterAnchorCandidate(this);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_isManuallyRegistered)
        {
            _anchorProvider?.UnregisterAnchorCandidate(this);
        }

        _isManuallyRegistered = false;
        _anchorProvider = null;

        base.OnDetachedFromVisualTree(e);
    }
}
