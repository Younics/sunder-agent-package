using System.Reflection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Rpc;
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

    [Fact]
    public void RemoveIfOlderThan_PreservesCurrentAndNewerRuns()
    {
        var registry = new AgentActiveRunRegistry();
        var sessionId = Guid.NewGuid();
        var current = CreateHandle(revision: 2);
        Assert.True(registry.Activate(sessionId, current).IsAccepted);

        Assert.Null(registry.RemoveIfOlderThan(sessionId, runRevision: 2));
        Assert.Null(registry.RemoveIfOlderThan(sessionId, runRevision: 1));
        Assert.True(registry.IsCurrent(sessionId, current.RunId, current.RunRevision));

        Assert.Same(current, registry.RemoveIfOlderThan(sessionId, runRevision: 3));
        Assert.False(registry.IsActive(sessionId));
        current.CancellationTokenSource.Dispose();
    }

    [Fact]
    public void BehaviorInvocation_IsCallerBoundAndCannotSurviveRunReplacement()
    {
        var registry = new AgentActiveRunRegistry();
        var sessionId = Guid.NewGuid();
        var first = CreateHandle(1);
        var next = CreateHandle(2);
        using var firstCancellation = first.CancellationTokenSource;
        using var nextCancellation = next.CancellationTokenSource;
        var runtime = DispatchProxy.Create<IAgentBehaviorLoopRuntime, UnusedRuntime>();

        registry.Activate(sessionId, first);
        using var firstGrant = registry.OpenBehaviorInvocation(sessionId, first.RunId, first.RunRevision, runtime, "package.behavior");
        Assert.Same(runtime, registry.ResolveBehaviorInvocation(firstGrant.InvocationId, "package.behavior"));
        AssertDenied(() => registry.ResolveBehaviorInvocation(firstGrant.InvocationId, "package.foreign"));

        registry.Activate(sessionId, next);
        AssertDenied(() => registry.ResolveBehaviorInvocation(firstGrant.InvocationId, "package.behavior"));
        AssertDenied(() => registry.OpenBehaviorInvocation(sessionId, first.RunId, first.RunRevision, runtime, "package.behavior"));
        using var nextGrant = registry.OpenBehaviorInvocation(sessionId, next.RunId, next.RunRevision, runtime, "package.behavior");
        firstGrant.Dispose();
        Assert.Same(runtime, registry.ResolveBehaviorInvocation(nextGrant.InvocationId, "package.behavior"));
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("complete")]
    [InlineData("remove")]
    public void BehaviorInvocation_ClosesWithItsGrantOrOwningRun(string completion)
    {
        var registry = new AgentActiveRunRegistry();
        var sessionId = Guid.NewGuid();
        var active = CreateHandle(1);
        using var cancellation = active.CancellationTokenSource;
        var runtime = DispatchProxy.Create<IAgentBehaviorLoopRuntime, UnusedRuntime>();
        registry.Activate(sessionId, active);
        using var grant = registry.OpenBehaviorInvocation(sessionId, active.RunId, active.RunRevision, runtime, "package.behavior");
        if (completion == "grant") grant.Dispose();
        else if (completion == "complete") registry.Complete(sessionId, active.RunId, active.RunRevision);
        else registry.Remove(sessionId);
        AssertDenied(() => registry.ResolveBehaviorInvocation(grant.InvocationId, "package.behavior"));
    }

    private static void AssertDenied(Action action)
    {
        var error = Assert.Throws<SunderRpcException>(action).Error;
        Assert.Equal(SunderRpcErrorKind.PermissionDenied, error.Kind);
        Assert.Equal("agent.run-control.invalid-invocation", error.Code);
    }

    public class UnusedRuntime : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException("An authorization-only test must not invoke the runtime.");
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
