using Avalonia;
using Avalonia.Controls;

namespace Sunder.Package.Agent.Shared.Presentation;

// Reflow small form/action rows without replacing their controls or bindings.
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.App)]
internal sealed class ResponsiveGrid : Grid
{
    private string? _wideColumns;
    private string? _wideRows;
    private (Control Control, int Row, int Column, int RowSpan, int ColumnSpan)[] _cells = [];
    private bool? _compact;

    public ResponsiveGrid()
    {
        Bind(RowSpacingProperty, this.GetResourceObservable("Sunder.Spacing.Small"));
        SizeChanged += (_, _) => Apply(Bounds.Width);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_wideColumns is null)
        {
            _wideColumns = ColumnDefinitions.ToString();
            _wideRows = RowDefinitions.ToString();
            _cells = Children.Select(child => (child, GetRow(child), GetColumn(child), GetRowSpan(child), GetColumnSpan(child))).ToArray();
        }
        if (double.IsFinite(availableSize.Width)) Apply(availableSize.Width);
        return base.MeasureOverride(availableSize);
    }

    private void Apply(double availableWidth)
    {
        if (_wideColumns is null || availableWidth <= 0) return;
        var minimum = this.TryFindResource("Sunder.Agent.RowMinimumWidth", out var resource) && resource is double width ? width : 480;
        var compact = availableWidth < minimum;
        if (_compact == compact) return;
        _compact = compact;
        ColumnDefinitions = new(compact ? "*" : _wideColumns);
        RowDefinitions = new(compact ? string.Join(',', _cells.Select(_ => "Auto")) : _wideRows ?? string.Empty);
        var index = 0;
        foreach (var cell in _cells.OrderBy(cell => cell.Row).ThenBy(cell => cell.Column))
        {
            SetRow(cell.Control, compact ? index++ : cell.Row);
            SetColumn(cell.Control, compact ? 0 : cell.Column);
            SetRowSpan(cell.Control, compact ? 1 : cell.RowSpan);
            SetColumnSpan(cell.Control, compact ? 1 : cell.ColumnSpan);
        }
    }
}
