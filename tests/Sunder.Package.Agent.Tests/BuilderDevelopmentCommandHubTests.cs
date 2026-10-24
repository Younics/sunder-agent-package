extern alias BuilderWorker;

using BuilderWorker::Sunder.Package.Agent.Builder;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class BuilderDevelopmentCommandHubTests
{
    [Fact]
    public async Task CancelledDispatchedRepliesReleaseCapacityWithoutReplayingAndLateCompletionIsIdempotent()
    {
        using var hub = new BuilderDevelopmentCommandHub();
        var app = Guid.NewGuid().ToString("N"); hub.Connect(app);
        for (var index = 0; index < 40; index++)
        {
            using var cancellation = new CancellationTokenSource();
            var response = hub.InvokeAsync("build", index.ToString(), "{}", app, cancellation.Token);
            var command = (await hub.ReadAsync(app, default))!;
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => response);
            // This is only a reply acknowledgement. Neither a late reply nor a
            // duplicate acknowledgement can create another operation or read.
            hub.Complete(app, command.RequestId, "{}", null);
            hub.Complete(app, command.RequestId, "{}", null);
        }
        var final = hub.InvokeAsync("observe", "original-operation", "{}", app, default);
        var read = (await hub.ReadAsync(app, default))!;
        Assert.Equal("observe", read.Operation);
        Assert.Equal("original-operation", read.OperationId);
        hub.Complete(app, read.RequestId, "{\"state\":\"running\"}", null);
        Assert.Equal(app, (await final).AppId);
    }

    [Fact]
    public async Task ExpiredObserverCannotConsumeRequestsForAReplacementApp()
    {
        var clock = new Clock(); using var hub = new BuilderDevelopmentCommandHub(clock);
        var first = Guid.NewGuid().ToString("N"); var second = Guid.NewGuid().ToString("N");
        hub.Connect(first);
        var oldRead = hub.ReadAsync(first, default);
        clock.Advance(TimeSpan.FromSeconds(16)); hub.Connect(second);
        var response = hub.InvokeAsync("build", "operation", "{}", second, default);
        await Assert.ThrowsAsync<SunderRpcException>(() => oldRead);
        var command = Assert.IsType<BuilderDevelopmentCommand>(await hub.ReadAsync(second, default));
        Assert.Equal("operation", command.OperationId);
        hub.Complete(second, command.RequestId, "{}", null);
        Assert.Equal(second, (await response).AppId);
    }

    [Fact]
    public async Task DispatchedRequestRemainsBoundToItsOriginalAppEvenAfterReplacement()
    {
        var clock = new Clock(); using var hub = new BuilderDevelopmentCommandHub(clock);
        var first = Guid.NewGuid().ToString("N"); var second = Guid.NewGuid().ToString("N");
        hub.Connect(first);
        var response = hub.InvokeAsync("hotreload", "operation", "{}", first, default);
        var command = (await hub.ReadAsync(first, default))!;
        clock.Advance(TimeSpan.FromSeconds(16)); hub.Connect(second);
        var rejected = await Assert.ThrowsAsync<BuilderDevelopmentRequestException>(() => hub.InvokeAsync("hotreload", "operation", "{}", first, default));
        Assert.False(rejected.Failure.OutcomeUnknown);
        Assert.Throws<SunderRpcException>(() => hub.Complete(second, command.RequestId, "{}", null));
        hub.Complete(first, command.RequestId, "{\"recorded\":true}", null);
        Assert.Equal(first, (await response).AppId);
    }

    [Fact]
    public async Task CallerCancellationBeforeReadCannotDispatchLaterAndCapacityStaysBounded()
    {
        using var hub = new BuilderDevelopmentCommandHub();
        var app = Guid.NewGuid().ToString("N"); hub.Connect(app);
        using var cancellation = new CancellationTokenSource();
        var request = hub.InvokeAsync("build", "cancelled", "{}", app, cancellation.Token);
        cancellation.Cancel();
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(cancellation.Token, cancelled.CancellationToken);
        Assert.Null(await hub.ReadAsync(app, default));
        var pending = Enumerable.Range(0, 32).Select(index => hub.InvokeAsync("build", index.ToString(), "{}", app, default)).ToArray();
        var overflow = await Assert.ThrowsAsync<BuilderDevelopmentRequestException>(() => hub.InvokeAsync("build", "overflow", "{}", app, default));
        Assert.False(overflow.Failure.OutcomeUnknown);
        hub.Dispose();
        foreach (var work in pending)
            Assert.False((await Assert.ThrowsAsync<BuilderDevelopmentRequestException>(() => work)).Failure.OutcomeUnknown);
    }

    [Fact]
    public async Task WorkerStopAfterAppDispatchPreservesMutationUncertaintyAndTheOriginalRequest()
    {
        using var hub = new BuilderDevelopmentCommandHub();
        var app = Guid.NewGuid().ToString("N"); hub.Connect(app);
        var response = hub.InvokeAsync("hotreload", "original-operation", "{}", app, default);
        var command = (await hub.ReadAsync(app, default))!;
        Assert.Equal("original-operation", command.OperationId);
        hub.Dispose();
        var error = await Assert.ThrowsAsync<BuilderDevelopmentRequestException>(() => response);
        Assert.True(error.Failure.OutcomeUnknown);
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan value) => _ticks += value.Ticks;
    }
}
