extern alias AgentApp;
extern alias BuilderApp;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class PresentationDispatcherTests
{
    [AvaloniaFact]
    public async Task PresentationDispatcher_AbortedFailureHandlerAndLateCaptureDrainOnIsolatedDispatcher()
    {
        // A dedicated dispatcher can really shut down without killing the suite's shared UIThread.
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new ManualResetEventSlim();
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                ready.SetResult(dispatcher);
                stop.Wait();
                dispatcher.InvokeShutdown();
                // First capture in this independently compiled package, on the native owner thread.
                // An inline CheckAccess shortcut would incorrectly execute the callback here.
                var late = BuilderApp::Sunder.Package.Agent.Shared.Presentation.PresentationDispatcher.For(dispatcher);
                Exception? failure = null;
                try { late.InvokeAsync(() => callbacks++).GetAwaiter().GetResult(); }
                catch (Exception error) { failure = error; }
                stopped.TrySetResult(failure);
            }
            catch (Exception error) { ready.TrySetException(error); stopped.TrySetException(error); }
        }) { IsBackground = true };
        thread.Start();
        try
        {
            var native = await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var dispatcher = AgentApp::Sunder.Package.Agent.Shared.Presentation.PresentationDispatcher.For(native);
            await using var scope = new AgentApp::Sunder.Package.Agent.Shared.Presentation.PresentationTaskScope(
                (_, token) => dispatcher.InvokeAsync(() => callbacks++, token), dispatcher.ShutdownToken);
            var observation = scope.RunAsync(_ => Task.FromException(new IOException("original failure")));
            Assert.False(observation.IsCompleted);
            stop.Set();
            var aborted = Assert.IsType<BuilderApp::Sunder.Package.Agent.Shared.Presentation.PresentationDispatchCanceledException>(
                await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            await observation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(scope.CancellationToken.IsCancellationRequested);
            Assert.Equal(0, callbacks);

            // Only DispatcherOperation.Status proved that late capture's tokenless task abort.
            var late = BuilderApp::Sunder.Package.Agent.Shared.Presentation.PresentationDispatcher.For(native);
            Assert.False(late.ShutdownToken.IsCancellationRequested);
            Assert.Equal(BuilderApp::Sunder.Package.Agent.Shared.Presentation.PresentationDispatchCancellationReason.DispatcherAborted, aborted.Reason);
            await using var lateScope = new BuilderApp::Sunder.Package.Agent.Shared.Presentation.PresentationTaskScope(
                (_, token) => late.InvokeAsync(() => callbacks++, token));
            await lateScope.RunAsync(_ => Task.FromException(new IOException("failure after dispatcher shutdown")))
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(lateScope.CancellationToken.IsCancellationRequested);
            Assert.Equal(0, callbacks);
        }
        finally { stop.Set(); thread.Join(TimeSpan.FromSeconds(2)); }
    }

    [AvaloniaFact]
    public async Task PresentationDispatcher_QueuedFocusRemainsDeferredAndCanBeAbortedByOwner()
    {
        using var owner = new CancellationTokenSource();
        var calls = 0;
        var queued = AgentApp::Sunder.Package.Agent.Shared.Presentation.PresentationDispatcher.QueueAsync(
            () => calls++, DispatcherPriority.Background, owner.Token);
        Assert.False(queued.IsCompleted);
        Assert.Equal(0, calls);
        owner.Cancel();
        var stopped = await Assert.ThrowsAsync<AgentApp::Sunder.Package.Agent.Shared.Presentation.PresentationDispatchCanceledException>(() => queued);
        Assert.Equal(owner.Token, stopped.CancellationToken);
        Assert.Equal(0, calls);
        var dispatcher = AgentApp::Sunder.Package.Agent.Shared.Presentation.PresentationDispatcher.For(Dispatcher.UIThread);
        Assert.False(dispatcher.ShutdownToken.IsCancellationRequested);
        await dispatcher.InvokeAsync(() => calls++);
        Assert.Equal(1, calls);
    }
}
