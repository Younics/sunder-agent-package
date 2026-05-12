using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;

namespace Sunder.Package.Agent.Subagents.PackageViews;

internal static class SubagentThemeBrushes
{
    public static IBrush? Resolve(string resourceKey)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return null;
        }

        var application = Application.Current;
        if (application?.Resources.TryGetResource(resourceKey, application.ActualThemeVariant, out var resource) == true
            && resource is IBrush brush)
        {
            return brush;
        }

        return null;
    }
}
