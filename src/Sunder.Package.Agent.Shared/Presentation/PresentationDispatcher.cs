using Avalonia;
using Avalonia.Threading;

namespace Sunder.Package.Agent.Shared.Presentation;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.App)]
internal interface IPresentationDispatcher
{
    bool CheckAccess();

    Task InvokeAsync(Action action);
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.App)]
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
