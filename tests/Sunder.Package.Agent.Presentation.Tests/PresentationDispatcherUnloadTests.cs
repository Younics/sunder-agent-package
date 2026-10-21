extern alias BuilderApp;

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class PresentationDispatcherUnloadTests
{
    [AvaloniaTheory]
    [InlineData("capture")]
    [InlineData("completed")]
    [InlineData("owner-aborted")]
    [InlineData("callback-fault")]
    [InlineData("callback-cancellation")]
    public async Task RetiredPackageDispatcherCollectsWhileNativeDispatcherRemainsAlive(string outcome)
    {
        var native = Dispatcher.UIThread;
        var path = typeof(BuilderApp::Sunder.Package.Agent.Builder.BuilderViewModel).Assembly.Location;
        var retired = await Task.Run(() => CaptureDispatchAndRetire(path, native, outcome));

        for (var attempt = 0; retired.Context.IsAlive && attempt < 12; attempt++)
        {
            // Let native dispatch/continuation stack frames release their completed operations.
            await native.InvokeAsync(static () => { }, DispatcherPriority.Background);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (retired.Context.IsAlive) await Task.Delay(20);
        }

        Assert.False(retired.Adapter.IsAlive, $"The native dispatcher retained the {outcome} package adapter.");
        Assert.False(retired.Context.IsAlive, $"The {outcome} presentation dispatcher pinned its collectible package.");
        GC.KeepAlive(native);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static (WeakReference Context, WeakReference Adapter) CaptureDispatchAndRetire(string path, Dispatcher native, string outcome)
    {
        // A synchronous lifetime boundary keeps reflected package objects and faulted tasks out
        // of the asserting test's async state machine. Only weak references escape.
        var context = new DispatcherProbeLoadContext(path);
        try
        {
            var assembly = context.LoadFromAssemblyPath(path);
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(assembly));
            Assert.True(assembly.IsCollectible);
            var type = assembly.GetType("Sunder.Package.Agent.Shared.Presentation.PresentationDispatcher", throwOnError: true)!;
            var factory = type.GetMethod("For", BindingFlags.Static | BindingFlags.NonPublic)!;
            var dispatcher = factory.Invoke(null, [native])!;
            var shutdownProperty = dispatcher.GetType().GetProperty("ShutdownToken")!;
            var shutdown = (CancellationToken)shutdownProperty.GetValue(dispatcher)!;
            Assert.True(shutdown.CanBeCanceled);
            // Multiple captures must retain the same shutdown signal without a native-lifetime
            // cache owning a collectible adapter. This also covers token-only presentation owners.
            Assert.Equal(shutdown, (CancellationToken)shutdownProperty.GetValue(factory.Invoke(null, [native]))!);
            if (outcome != "capture")
            {
                var invoke = dispatcher.GetType().GetMethod("InvokeAsync", [typeof(Action), typeof(CancellationToken)])!;
                using var owner = new CancellationTokenSource();
                using var blocked = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                Task? blocker = null;
                var calls = 0;
                Exception callbackFailure = outcome == "callback-cancellation"
                    ? new OperationCanceledException(new CancellationToken(true))
                    : new IOException("Collectible presentation callback failed.");
                try
                {
                    if (outcome == "owner-aborted")
                    {
                        blocker = native.InvokeAsync(() => { blocked.Set(); release.Wait(); }, DispatcherPriority.Send).GetTask();
                        Assert.True(blocked.Wait(TimeSpan.FromSeconds(3)));
                    }
                    Action callback = () =>
                    {
                        calls++;
                        if (outcome is "callback-fault" or "callback-cancellation") throw callbackFailure;
                    };
                    var pending = (Task)invoke.Invoke(dispatcher, [callback, owner.Token])!;
                    if (outcome == "owner-aborted")
                    {
                        owner.Cancel();
                        var cancelled = Assert.ThrowsAny<OperationCanceledException>(() =>
                            pending.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
                        Assert.Equal(owner.Token, cancelled.CancellationToken);
                        Assert.Equal(0, calls);
                    }
                    else if (outcome == "callback-fault")
                    {
                        var error = Record.Exception(() => pending.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
                        Assert.Same(callbackFailure, error);
                        Assert.Equal(1, calls);
                    }
                    else if (outcome == "callback-cancellation")
                    {
                        var error = Record.Exception(() => pending.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
                        Assert.NotNull(error);
                        Assert.Equal("Sunder.Package.Agent.Shared.Presentation.PresentationCallbackException", error.GetType().FullName);
                        Assert.Same(callbackFailure, error.InnerException);
                        Assert.Equal(1, calls);
                    }
                    else
                    {
                        pending.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                        Assert.Equal(1, calls);
                    }
                }
                finally
                {
                    release.Set();
                    blocker?.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                }
            }
            return (new WeakReference(context, trackResurrection: true), new WeakReference(dispatcher, trackResurrection: true));
        }
        finally { context.Unload(); }
    }

    private sealed class DispatcherProbeLoadContext(string path) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);

        protected override Assembly? Load(AssemblyName name)
            => Default.Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), name))
                ?? (_resolver.ResolveAssemblyToPath(name) is { } dependency ? LoadFromAssemblyPath(dependency) : null);
    }
}
