using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

internal static class UiCapture
{
    internal static void Save(TopLevel window, string name)
    {
        if (Environment.GetEnvironmentVariable("SUNDER_UI_CAPTURE_ROOT") is not { Length: > 0 } root) return;
        Directory.CreateDirectory(root);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(root, name + ".png"), PngBitmapEncoderOptions.Default);
    }
}
