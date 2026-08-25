using Avalonia.Threading;

namespace Sunder.Package.Agent.TestSupport.Avalonia;

public static class HeadlessDispatcherDiagnostics
{
    public static async Task DrainAsync(DispatcherPriority priority = default)
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, priority);
    }
}
