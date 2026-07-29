using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Sunder.Package.Agent.Shared.Presentation;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class AdaptiveMasterDetailTests
{
    [AvaloniaFact]
    public void Helper_PreservesWideAndCompactPaneGeometry()
    {
        var owner = new UserControl();
        var layout = new Grid();
        var master = new Border();
        var detail = new Border();
        bool? isCompact = null;
        using var behavior = new AdaptiveMasterDetail(owner, layout, master, detail, value => isCompact = value);

        behavior.ApplyForWidth(819);
        Assert.True(isCompact);
        Assert.Equal(0, layout.ColumnSpacing);
        Assert.Equal(0, Grid.GetColumn(detail));
        Assert.Equal(2, Grid.GetColumnSpan(master));
        Assert.Equal(new Thickness(0), master.BorderThickness);

        behavior.ApplyForWidth(820);
        Assert.False(isCompact);
        Assert.Equal(4, layout.ColumnSpacing);
        Assert.Equal(1, Grid.GetColumn(detail));
        Assert.Equal(1, Grid.GetColumnSpan(master));
        Assert.Equal(new Thickness(0, 0, 1, 0), master.BorderThickness);
    }

    [AvaloniaFact]
    public void Helper_OnlyReportsGeometryBreakpointState()
    {
        var owner = new UserControl();
        var layout = new Grid();
        var master = new Border { IsVisible = false };
        var detail = new Border { IsVisible = true };
        var states = new List<bool>();
        using var behavior = new AdaptiveMasterDetail(
            owner,
            layout,
            master,
            detail,
            states.Add,
            wideMinimumWidth: 600);

        behavior.ApplyForWidth(599);
        behavior.ApplyForWidth(600);

        Assert.Equal([true, false], states);
        Assert.False(master.IsVisible);
        Assert.True(detail.IsVisible);
    }
}
