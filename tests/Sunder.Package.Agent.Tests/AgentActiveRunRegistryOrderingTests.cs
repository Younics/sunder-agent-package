using Sunder.Package.Agent.Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentActiveRunRegistryOrderingTests
{
    [Fact]
    public async Task Activate_WhenOlderPreflightFinishesLateRejectsOlderRun()
    {
        var registry = new AgentActiveRunRegistry();
        var sessionId = Guid.NewGuid();
        var olderRun = CreateHandle(revision: 1);
        var newerRun = CreateHandle(revision: 2);
        using var startBarrier = new Barrier(3);
        using var newerActivatedBarrier = new Barrier(2);

        var newerActivationTask = Task.Run(() =>
        {
            startBarrier.SignalAndWait();
            var activation = registry.Activate(sessionId, newerRun);
            newerActivatedBarrier.SignalAndWait();
            return activation;
        });
        var olderActivationTask = Task.Run(() =>
        {
            startBarrier.SignalAndWait();
            newerActivatedBarrier.SignalAndWait();
            return registry.Activate(sessionId, olderRun);
        });

        startBarrier.SignalAndWait();
        var newerActivation = await newerActivationTask;
        var olderActivation = await olderActivationTask;

        Assert.Equal(AgentRunActivationOutcome.Activated, newerActivation.Outcome);
        Assert.Equal(AgentRunActivationOutcome.Rejected, olderActivation.Outcome);
        Assert.Same(newerRun, olderActivation.CurrentRun);
        Assert.True(registry.IsCurrent(sessionId, newerRun.RunId, newerRun.RunRevision));
        Assert.False(registry.IsCurrent(sessionId, olderRun.RunId, newerRun.RunRevision));
        Assert.True(registry.IsCurrent(sessionId, Guid.Empty, newerRun.RunRevision));

        registry.CleanupCurrent(sessionId, olderRun.RunId, newerRun.RunRevision);
        Assert.True(registry.IsActive(sessionId));
        registry.CleanupCurrent(sessionId, newerRun.RunId, newerRun.RunRevision);
        Assert.False(registry.IsActive(sessionId));
        olderRun.CancellationTokenSource.Dispose();
    }

    private static AgentActiveRunHandle CreateHandle(long revision)
        => new(
            Guid.NewGuid(),
            revision,
            DateTimeOffset.UtcNow,
            "profile.test",
            $"message-{revision}",
            new CancellationTokenSource());
}
