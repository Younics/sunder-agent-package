using Avalonia;
using Avalonia.Controls;

namespace Sunder.Package.Agent.Shared.Presentation;

internal sealed class AdaptiveMasterDetail : IDisposable
{
    internal const double DefaultWideMinimumWidth = 820;

    private readonly Control _owner;
    private readonly Grid _layout;
    private readonly Border _masterPane;
    private readonly Control _detailPane;
    private readonly Action<bool> _setCompactLayout;
    private readonly double _wideMinimumWidth;
    private bool _disposed;

    public AdaptiveMasterDetail(
        Control owner,
        Grid layout,
        Border masterPane,
        Control detailPane,
        Action<bool> setCompactLayout,
        double wideMinimumWidth = DefaultWideMinimumWidth)
    {
        _owner = owner;
        _layout = layout;
        _masterPane = masterPane;
        _detailPane = detailPane;
        _setCompactLayout = setCompactLayout;
        _wideMinimumWidth = wideMinimumWidth;
        _owner.Loaded += OwnerOnLoaded;
        _owner.SizeChanged += OwnerOnSizeChanged;
    }

    public void Apply()
        => ApplyForWidth(_owner.Bounds.Width);

    internal void ApplyForWidth(double width)
    {
        var useCompactLayout = width > 0 && width < _wideMinimumWidth;
        _setCompactLayout(useCompactLayout);
        _layout.ColumnSpacing = useCompactLayout ? 0 : 4;
        _masterPane.BorderThickness = useCompactLayout ? new Thickness(0) : new Thickness(0, 0, 1, 0);

        Grid.SetColumn(_masterPane, 0);
        Grid.SetColumn(_detailPane, useCompactLayout ? 0 : 1);
        Grid.SetColumnSpan(_masterPane, useCompactLayout ? 2 : 1);
        Grid.SetColumnSpan(_detailPane, useCompactLayout ? 2 : 1);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _owner.Loaded -= OwnerOnLoaded;
        _owner.SizeChanged -= OwnerOnSizeChanged;
    }

    private void OwnerOnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Apply();

    private void OwnerOnSizeChanged(object? sender, SizeChangedEventArgs e)
        => ApplyForWidth(e.NewSize.Width);
}
