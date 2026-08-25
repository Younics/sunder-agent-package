using Sunder.Package.Agent.Shared.Presentation;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class PresentationStateTests
{
    [Fact]
    public void OperationState_StaleGenerationCannotOverwriteCurrentOperation()
    {
        using var state = new OperationState();
        var first = state.Begin("First");
        var second = state.Begin("Second", progress: 25);

        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.False(state.TryComplete(first, "Stale"));
        Assert.Equal("Second", state.Message);
        Assert.Equal(25, state.Progress);
        Assert.True(state.TryComplete(second, "Current complete"));
        Assert.False(state.IsBusy);
        Assert.Equal(OperationSeverity.Success, state.Severity);
    }

    [Fact]
    public async Task TimedStatusController_ReplacingStatusCancelsEarlierClear()
    {
        using var controller = new TimedStatusController();
        var firstCleared = false;
        var secondCleared = false;

        var first = controller.ScheduleAsync(TimeSpan.FromMinutes(1), () => firstCleared = true);
        var second = controller.ScheduleAsync(TimeSpan.Zero, () => secondCleared = true);

        await Task.WhenAll(first, second);
        Assert.False(firstCleared);
        Assert.True(secondCleared);
    }

    [Fact]
    public async Task TimedStatusController_RunsCallbackOnCapturedDispatcher()
    {
        using var dispatcher = new DedicatedPresentationDispatcher();
        using var controller = new TimedStatusController(dispatcher: dispatcher);
        var callbackThread = 0;

        await controller.ScheduleAsync(
            TimeSpan.Zero,
            () => callbackThread = Environment.CurrentManagedThreadId);

        Assert.Equal(dispatcher.ThreadId, callbackThread);
    }

    [Fact]
    public async Task TimedStatusController_DisposalSuppressesQueuedCallback()
    {
        using var dispatcher = new DedicatedPresentationDispatcher();
        dispatcher.Pause();
        var controller = new TimedStatusController(dispatcher: dispatcher);
        var invoked = false;

        var scheduled = controller.ScheduleAsync(TimeSpan.Zero, () => invoked = true);
        await dispatcher.WaitForEnqueuedAsync();
        controller.Dispose();
        dispatcher.Resume();
        await scheduled;

        Assert.False(invoked);
    }

    [Fact]
    public void EditableDocumentState_TracksCleanAndRevertContract()
    {
        var state = new EditableDocumentState<string>("original", StringComparer.Ordinal);

        state.Value = "edited";
        Assert.True(state.IsDirty);

        state.Revert();
        Assert.Equal("original", state.Value);
        Assert.False(state.IsDirty);

        state.Value = "saved";
        state.MarkClean();
        Assert.False(state.IsDirty);
    }

}
