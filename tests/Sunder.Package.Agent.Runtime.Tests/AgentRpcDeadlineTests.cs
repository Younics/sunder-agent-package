using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentRpcDeadlineTests
{
    [Theory]
    [InlineData(SunderRpcDispatchState.NotDispatched, SunderRpcRetrySafety.Safe, true)]
    [InlineData(SunderRpcDispatchState.PossiblyDispatched, SunderRpcRetrySafety.Unsafe, false)]
    [InlineData(SunderRpcDispatchState.PossiblyDispatched, SunderRpcRetrySafety.Safe, false)]
    [InlineData(SunderRpcDispatchState.NotDispatched, SunderRpcRetrySafety.Unsafe, false)]
    public void RpcDeadlineUsesStructuredRetrySafetyAndAnInterruptedPresentation(
        SunderRpcDispatchState dispatch, SunderRpcRetrySafety safety, bool retry)
    {
        var failure = new SunderRpcException(new(SunderRpcErrorKind.DeadlineExceeded,
            "rpc.call.deadline-exceeded", "No timeout keywords here.", dispatch, safety));
        Assert.Equal(retry, AgentProviderResilience.IsTransient(failure));
        Assert.Equal(retry, AgentProviderResilience.IsTransient(new InvalidOperationException("connection timeout", failure)));
        var presentation = AgentFailurePresentationPolicy.Create(new InvalidOperationException("Wrapper", failure));
        Assert.Equal(AgentRunStatus.Interrupted, presentation.Status);
        Assert.Equal(AgentFailurePresentationPolicy.DeadlineInterruptedMarkdown, presentation.Markdown);
    }

    [Theory]
    [InlineData(SunderRpcErrorKind.PermissionDenied)]
    [InlineData(SunderRpcErrorKind.Protocol)]
    [InlineData(SunderRpcErrorKind.StaleEndpoint)]
    public void OtherRpcFailuresAreNotRetriedBecauseTheirMessageMentionsTimeout(SunderRpcErrorKind kind)
    {
        var failure = new SunderRpcException(new(kind, "not-a-transient-deadline", "connection timeout",
            SunderRpcDispatchState.NotDispatched, SunderRpcRetrySafety.Safe));
        Assert.False(AgentProviderResilience.IsTransient(failure));
        Assert.False(AgentProviderResilience.IsTransient(new InvalidOperationException("connection timeout", failure)));
        Assert.Equal(AgentRunStatus.Failed, AgentFailurePresentationPolicy.Create(failure).Status);
    }
}
