using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Builder;

public interface IBuilderUiDispatcher
{
    bool CheckAccess();

    Task InvokeAsync(Action action);

    Task<T> InvokeAsync<T>(Func<T> action);

    CancellationToken ShutdownToken => default;

    Task InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        PresentationCancellation.ThrowIfStopped(cancellationToken, ShutdownToken);
        return InvokeAsync(() =>
        {
            PresentationCancellation.ThrowIfStopped(cancellationToken, ShutdownToken);
            AvaloniaBuilderUiDispatcher.InvokeCallback(action);
        });
    }

    Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        PresentationCancellation.ThrowIfStopped(cancellationToken, ShutdownToken);
        return InvokeAsync(() =>
        {
            PresentationCancellation.ThrowIfStopped(cancellationToken, ShutdownToken);
            try { return action(); }
            catch (Exception error) { throw new PresentationCallbackException(error); }
        });
    }
}

public sealed class AvaloniaBuilderUiDispatcher : IBuilderUiDispatcher
{
    private readonly IPresentationDispatcher _dispatcher = PresentationDispatcher.Capture();

    public CancellationToken ShutdownToken => _dispatcher.ShutdownToken;

    public bool CheckAccess()
        => _dispatcher.CheckAccess();

    public Task InvokeAsync(Action action)
        => InvokeAsync(action, default);

    public Task InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _dispatcher.InvokeAsync(() => InvokeCallback(action), cancellationToken);
    }

    internal static void InvokeCallback(Action action)
    {
        // Builder's job recovery must distinguish UI callback faults from backend failures.
        // Keep that operation-specific boundary out of the shared presentation dispatcher.
        try { action(); }
        catch (Exception error) { throw new PresentationCallbackException(error); }
    }

    public Task<T> InvokeAsync<T>(Func<T> action) => InvokeAsync(action, default);

    public async Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        T result = default!;
        await InvokeAsync(() => { result = action(); }, cancellationToken).ConfigureAwait(false);
        return result;
    }
}
