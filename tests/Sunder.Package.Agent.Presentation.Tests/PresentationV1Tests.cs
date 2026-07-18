using Avalonia.Headless.XUnit;
using Sunder.Package.Agent.Shared.PackageViews;
using Sunder.Package.Agent.Shared.Presentation;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class PresentationV1Tests
{
    [Fact]
    public async Task AsyncOnce_SharesOneOperationAndAllowsPerCallerCancellation()
    {
        using var once = new AsyncOnce();
        var started = 0;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callerCancellation = new CancellationTokenSource();

        var canceledWait = once.RunAsync(async cancellationToken =>
        {
            Interlocked.Increment(ref started);
            await completion.Task.WaitAsync(cancellationToken);
        }, callerCancellation.Token);
        var sharedWait = once.RunAsync(
            _ => throw new InvalidOperationException("must not run"),
            TestContext.Current.CancellationToken);
        callerCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWait);
        Assert.Equal(1, Volatile.Read(ref started));
        completion.SetResult();
        await sharedWait;
    }

    [Fact]
    public void TypedOperationState_TracksKindAndRejectsStaleCompletion()
    {
        using var state = new OperationState<TestOperation>();
        var first = state.Begin(TestOperation.Load);
        var second = state.Begin(TestOperation.Save);

        Assert.Equal(TestOperation.Save, state.Current);
        Assert.False(state.IsCurrent(first));
        Assert.False(state.TryComplete(first));
        Assert.True(state.TryComplete(second));
        Assert.Null(state.Current);
    }

    [Fact]
    public void AdaptiveEditorStateCache_RestoresEachBreakpointIndependently()
    {
        var cache = new AdaptiveEditorStateCache<string>();
        const string narrow = "narrow-state";
        const string wide = "wide-state";

        cache.Save(wide: false, expanded: false, narrow);
        cache.Save(wide: true, expanded: false, wide);

        Assert.True(cache.TryRestore(wide: false, expanded: false, out var restoredNarrow));
        Assert.True(cache.TryRestore(wide: true, expanded: false, out var restoredWide));
        Assert.Equal(narrow, restoredNarrow);
        Assert.Equal(wide, restoredWide);
        Assert.False(cache.TryRestore(wide: true, expanded: true, out _));
    }

    [AvaloniaFact]
    public async Task ActivityTicker_UsesOneOwnedClockAndStopsOnDisposal()
    {
        using var ticker = new ActivityTicker(TimeSpan.FromMilliseconds(5));
        var ticks = 0;
        var firstTick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource? resumedTick = null;
        ticker.Tick += () =>
        {
            Interlocked.Increment(ref ticks);
            firstTick.TrySetResult();
            resumedTick?.TrySetResult();
        };

        await firstTick.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        ticker.SetEnabled(false);
        var ticksWhilePaused = Volatile.Read(ref ticks);
        await Task.Delay(30, TestContext.Current.CancellationToken);
        Assert.Equal(ticksWhilePaused, Volatile.Read(ref ticks));

        resumedTick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ticker.SetEnabled(true);
        await resumedTick.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.True(Volatile.Read(ref ticks) > ticksWhilePaused);

        ticker.Dispose();
        var ticksAtDisposal = Volatile.Read(ref ticks);
        await Task.Delay(30, TestContext.Current.CancellationToken);

        Assert.Equal(ticksAtDisposal, Volatile.Read(ref ticks));
    }

    private enum TestOperation
    {
        Load,
        Save,
    }
}
