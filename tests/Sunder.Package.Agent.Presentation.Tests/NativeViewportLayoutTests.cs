using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class NativeViewportLayoutTests
{
    [AvaloniaFact]
    public void AppNativeViewport_RadiusOnlyChangeInvalidatesArrangementAndReparentingUsesTheNewShell()
    {
        var browser = new Border();
        var viewport = new Sunder.App.Services.AppNativeWebViewViewport(browser);
        var boundary = new Border { ClipToBounds = true, CornerRadius = new CornerRadius(18), Child = viewport };
        boundary.Classes.Add("shell-panel-content-clip");
        var window = new Window { Width = 300, Height = 200, Content = boundary };
        window.Show();
        try
        {
            window.UpdateLayout();
            Assert.True(viewport.IsArrangeValid);
            Assert.Equal(7, browser.Bounds.X);
            boundary.CornerRadius = new CornerRadius(60); // Border invalidates rendering, not layout.
            Assert.False(viewport.IsArrangeValid);
            window.UpdateLayout();
            Assert.Equal(19, browser.Bounds.X);

            window.Content = null;
            Assert.False(browser.IsVisible);
            boundary.Child = null;
            var replacement = new Border { ClipToBounds = true, CornerRadius = new CornerRadius(30), Child = viewport };
            replacement.Classes.Add("secondary-panel-content-clip");
            window.Content = replacement;
            window.UpdateLayout();
            Assert.True(browser.IsVisible);
            Assert.Equal(10, browser.Bounds.X);
            Assert.True(viewport.IsArrangeValid);
            boundary.CornerRadius = new CornerRadius(100); // Detached predecessor must no longer invalidate the viewport.
            Assert.True(viewport.IsArrangeValid);
            replacement.CornerRadius = new CornerRadius(60);
            Assert.False(viewport.IsArrangeValid);
            window.UpdateLayout();
            Assert.Equal(19, browser.Bounds.X);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1000, 1000, 1000, 1000, 68)]
    [InlineData(100, 0, 0, 0, 38)]
    public void AppNativeViewport_NormalizesAllCornerRadiiTogetherWithoutBlankingOrUnsafeHalfSizeClamping(
        double topLeft, double topRight, double bottomRight, double bottomLeft, double expectedWidth)
    {
        var browser = new Border();
        var viewport = new Sunder.App.Services.AppNativeWebViewViewport(browser);
        var boundary = new Border { ClipToBounds = true, CornerRadius = new CornerRadius(topLeft, topRight, bottomRight, bottomLeft), Child = viewport };
        boundary.Classes.Add("shell-panel-content-clip");
        var window = new Window { Width = 100, Height = 100, Content = boundary };
        window.Show();
        try
        {
            window.UpdateLayout();
            Assert.True(browser.IsVisible);
            Assert.Equal(expectedWidth, browser.Bounds.Width);
        }
        finally { window.Close(); }
    }
}
