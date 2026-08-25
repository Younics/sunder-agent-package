using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Package.Agent.TestSupport;

namespace Sunder.Package.Agent.Presentation.Tests;

internal sealed class DedicatedPresentationDispatcher : IPresentationDispatcher, IDisposable
{
    private readonly DedicatedThreadDispatcher _dispatcher = new("Presentation test dispatcher");
    private int _observedPosts;

    public int ThreadId => _dispatcher.ThreadId;

    public bool CheckAccess() => _dispatcher.CheckAccess();

    public Task InvokeAsync(Action action) => _dispatcher.InvokeAsync(action);

    public void Pause() => _dispatcher.Pause();

    public void Resume() => _dispatcher.Resume();

    public Task WaitForEnqueuedAsync()
        => _dispatcher.WaitForPostedAsync(Interlocked.Increment(ref _observedPosts));

    public Task WaitForIdleAsync() => _dispatcher.WaitForIdleAsync();

    public void Dispose() => _dispatcher.Dispose();
}
