using Avalonia;
using Avalonia.Threading;

namespace Sunder.Package.Agent.Shared.Presentation;

internal interface IPresentationDispatcher
{
    bool CheckAccess();

    Task InvokeAsync(Action action);
}

internal static class PresentationDispatcher
{
    public static IPresentationDispatcher Capture() => AvaloniaPresentationDispatcher.Instance;

    private sealed class AvaloniaPresentationDispatcher : IPresentationDispatcher
    {
        public static AvaloniaPresentationDispatcher Instance { get; } = new();

        public bool CheckAccess() => Application.Current is null || Dispatcher.UIThread.CheckAccess();

        public async Task InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            if (CheckAccess())
            {
                action();
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Background);
        }
    }
}
