using Sunder.Package.Agent.Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentBackgroundWorkServiceTests
{
    [Fact]
    public async Task StopAsync_WaitsForOwnedWorkRegisteredBeforeDelegateStarts()
    {
        await using var service = new AgentBackgroundWorkService();
        await service.StartAsync();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var owned = Task.Run(() => service.RunOwnedAsync(_ =>
        {
            entered.Set();
            release.Wait();
            return Task.FromResult(42);
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));

        var stop = service.StopAsync();
        await Task.Delay(50);

        Assert.False(stop.IsCompleted);
        release.Set();
        Assert.Equal(42, await owned);
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task StopAsync_CancelsOwnedWorkAndWaitsForItsCleanup()
    {
        await using var service = new AgentBackgroundWorkService();
        await service.StartAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owned = service.RunOwnedAsync<int>(async cancellationToken =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 1;
            }
            finally
            {
                cleaned.TrySetResult();
            }
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(cleaned.Task.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owned);
    }
}
