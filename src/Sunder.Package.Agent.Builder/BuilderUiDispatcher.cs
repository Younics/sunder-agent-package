using Avalonia;
using Avalonia.Threading;

namespace Sunder.Package.Agent.Builder;

public interface IBuilderUiDispatcher
{
    bool CheckAccess();

    Task InvokeAsync(Action action);

    Task<T> InvokeAsync<T>(Func<T> action);
}

public sealed class AvaloniaBuilderUiDispatcher : IBuilderUiDispatcher
{
    public bool CheckAccess()
        => Application.Current is null || Dispatcher.UIThread.CheckAccess();

    public Task InvokeAsync(Action action)
        => InvokeAsync(() =>
        {
            action();
            return true;
        });

    public async Task<T> InvokeAsync<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (CheckAccess())
        {
            return action();
        }

        return await Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Background);
    }
}
