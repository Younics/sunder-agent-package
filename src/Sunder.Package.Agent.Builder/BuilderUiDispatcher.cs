using Avalonia;
using Avalonia.Threading;

namespace Sunder.Package.Agent.Builder;

public interface IBuilderUiDispatcher
{
    bool CheckAccess();

    void Post(Action action);

    Task InvokeAsync(Action action);

    Task<T> InvokeAsync<T>(Func<T> action);
}

public sealed class AvaloniaBuilderUiDispatcher : IBuilderUiDispatcher
{
    public bool CheckAccess()
        => Application.Current is null || Dispatcher.UIThread.CheckAccess();

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
    }

    public Task InvokeAsync(Action action)
        => InvokeAsync(() =>
        {
            action();
            return true;
        });

    public Task<T> InvokeAsync<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (CheckAccess())
        {
            return Task.FromResult(action());
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }
}
