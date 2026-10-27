using System.Collections;
using System.Reflection;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentToolBatchLifetimeTests
{
    [Fact]
    public async Task CompletedCallsReleaseTransientStateButRetainDurableReplayProtection()
    {
        var source = new PermissionAwareMutationToolSource("mutate");
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source);
        var run = runtime.Store.GetLatestRun(runtime.Session.SessionId)!;
        using var cancellation = new CancellationTokenSource();
        Assert.True(runtime.ActiveRunRegistry.Activate(runtime.Session.SessionId,
            new AgentActiveRunHandle(run.Key.RunId, run.Key.RunRevision, run.StartedAtUtc,
                run.ProfileId, run.UserMessage, cancellation)).IsAccepted);
        runtime.PermissionService.SaveOverride("test.mutate", "test-boundary", AgentPermissionDecision.Allow);
        var call = new AgentToolCallRequest("first-call", "mutate", "{}");
        for (var index = 0; index < 20; index++)
        {
            var outcome = await runtime.Host.InvokeToolAsync(call with { CallId = index == 0 ? call.CallId : $"call-{index}" }, null);
            Assert.Equal(AgentToolCallOutcomeKind.Executed, outcome.Kind);
            foreach (var field in new[] { "_toolExecutionsByCallId", "_allowOutsideConfiguredScopeByCallId",
                         "_approvedResourceReferencesByCallId", "_approvedResourceClaimsByCallId",
                         "_approvedResourceCapabilitiesByCallId", "_executionTargetConfigurationGenerationByCallId" })
                Assert.Empty(Assert.IsAssignableFrom<IDictionary>(runtime.Host.GetType().GetField(field,
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime.Host)));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Host.InvokeToolAsync(call, null).AsTask());
        Assert.Equal(20, source.ExecutionCount);
        Assert.Equal(20, runtime.Store.ListToolExecutions(runtime.Session.SessionId).Count);
    }
}
