using Avalonia;
using Avalonia.Controls;

namespace Sunder.Package.Agent.Shared.Presentation;

// Three slots: destructive action, optional notice, and an indivisible Back/Save group.
// Unlike form rows, editor actions must not become a separate row per button.
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.App)]
internal sealed class EditorFooter : Grid
{
    private Control? _left;
    private Control? _notice;
    private Control? _actions;
    private int _mode = -1;

    public EditorFooter()
    {
        ColumnDefinitions = new("Auto,*,Auto");
        Bind(ColumnSpacingProperty, this.GetResourceObservable("Sunder.Spacing.Small"));
        Bind(RowSpacingProperty, this.GetResourceObservable("Sunder.Spacing.Small"));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_mode < 0)
        {
            _left = Children.FirstOrDefault(child => GetColumn(child) == 0);
            _notice = Children.FirstOrDefault(child => GetColumn(child) == 1);
            _actions = Children.FirstOrDefault(child => GetColumn(child) == 2);
        }
        foreach (var child in Children) child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        var left = _left?.DesiredSize.Width ?? 0;
        var actions = _actions?.DesiredSize.Width ?? 0;
        var notice = _notice?.DesiredSize.Width ?? 0;
        // The empty star column still separates the outer slots by two gaps.
        var mode = left + actions + 2 * ColumnSpacing > availableSize.Width ? 2
            : notice > 0 && left + notice + actions + 2 * ColumnSpacing > availableSize.Width ? 1 : 0;
        if (_mode != mode)
        {
            _mode = mode;
            RowDefinitions = new(mode == 2 ? "Auto,Auto,Auto" : mode == 1 ? "Auto,Auto" : "Auto");
            if (_left is { } destructive)
            {
                SetRow(destructive, mode == 0 ? 0 : 1);
                SetColumnSpan(destructive, mode == 2 ? 3 : 1);
            }
            if (_notice is { } status)
            {
                SetRow(status, 0);
                SetColumn(status, mode == 0 ? 1 : 0);
                SetColumnSpan(status, mode == 0 ? 1 : 3);
            }
            if (_actions is { } group)
            {
                SetRow(group, mode == 2 ? 2 : mode == 1 ? 1 : 0);
                SetColumn(group, mode == 2 ? 0 : 2);
                SetColumnSpan(group, mode == 2 ? 3 : 1);
            }
        }
        return base.MeasureOverride(availableSize);
    }
}
