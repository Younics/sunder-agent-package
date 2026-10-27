using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentRunUsageTrackerTests
{
    [Fact]
    public async Task Charges_HaveNoCumulativeLimitAndRetainInitialState()
    {
        var tracker = new AgentRunUsageTracker(new AgentRunBudgetState(10_000, 20_000, 30_000));
        Assert.Equal(new AgentRunBudgetState(10_001, 20_000, 30_100), await tracker.ChargeProviderAttemptAsync(100));
        Assert.Equal(new AgentRunBudgetState(10_001, 21_000, 30_100), await tracker.ChargeToolCallsAsync(1_000));
    }

    [Fact]
    public async Task Charges_ValidateInputsAndHonorCancellation()
    {
        var tracker = new AgentRunUsageTracker();
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.ChargeProviderAttemptAsync(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.ChargeToolCallsAsync(-1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => tracker.ChargeToolCallsAsync(1, cancellation.Token));
        Assert.Equal(default, await tracker.ChargeToolCallsAsync(0));
    }

    [Fact]
    public async Task Charges_SaturateInsteadOfOverflowing()
    {
        var tracker = new AgentRunUsageTracker(new AgentRunBudgetState(long.MaxValue, long.MaxValue, long.MaxValue));
        await tracker.ChargeProviderAttemptAsync(10);
        Assert.Equal(new AgentRunBudgetState(long.MaxValue, long.MaxValue, long.MaxValue), await tracker.ChargeToolCallsAsync(10));
    }

    [Fact]
    public async Task Charges_UseDurableCounterAsSourceOfTruth()
    {
        var durableState = new AgentRunBudgetState(4, 8, 16);
        var tracker = new AgentRunUsageTracker(
            durableCharge: (charge, _) => ValueTask.FromResult(durableState = new AgentRunBudgetState(
                durableState.ProviderCycles + charge.ProviderCycles,
                durableState.ToolCalls + charge.ToolCalls,
                durableState.SubmittedContextTokens + charge.SubmittedContextTokens)));

        await tracker.ChargeToolCallsAsync(2);

        Assert.Equal(new AgentRunBudgetState(4, 10, 16), durableState);
        Assert.Equal(new AgentRunBudgetState(4, 11, 16), await tracker.ChargeToolCallsAsync(1));
    }

    [Fact]
    public async Task DurableCharge_RemainsPendingUntilAuthoritativeAdmissionCompletes()
    {
        var admitted = new TaskCompletionSource<AgentRunBudgetState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tracker = new AgentRunUsageTracker(
            durableCharge: (_, token) => new(admitted.Task.WaitAsync(token)));

        var pending = tracker.ChargeProviderAttemptAsync(10).AsTask();
        Assert.False(pending.IsCompleted);
        admitted.SetResult(new(ProviderCycles: 2, ToolCalls: 0, SubmittedContextTokens: 10));
        Assert.Equal(new AgentRunBudgetState(2, 0, 10), await pending);
    }

    [Fact]
    public void PromptOverheadEstimate_UsesExactBoundedSupplementaryPayload()
    {
        var blocks = Enumerable.Range(0, 40)
            .Select(index => new AgentPromptContextBlock(
                $"context-{index:00}",
                $"marker-{index:00}-" + new string('x', 20_000),
                Priority: 100 - index,
                SourceId: "test",
                Provenance: AgentContextProvenance.Tool,
                Trust: AgentContextTrust.Untrusted))
            .Append(new AgentPromptContextBlock(
                "required-scoped",
                "required-marker",
                Priority: -100,
                SourceId: "workspace-files")
            {
                Usage = AgentPromptContextUsage.ScopedInstruction,
                Authority = AgentPromptContextAuthority.ScopedInstruction,
                HostIdentity = "younics.ai.agent.scoped-instruction.v1",
                Scope = new AgentPromptContextScope(
                    "/workspace",
                    "/workspace",
                    "/workspace/AGENTS.md",
                    new string('a', 64),
                    new string('b', 64)),
            })
            .ToArray();

        var rendered = AgentPromptPreparationPipeline.RenderSupplementaryContext(blocks);
        var estimate = AgentPromptPreparationPipeline.EstimatePromptOverheadTokens(
            systemInstructions: null,
            blocks,
            availableTools: []);

        Assert.Equal(512 + AgentProviderRequestBudget.EstimateTextTokens([rendered]), estimate);
        Assert.Contains("marker-31-", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("marker-32-", rendered, StringComparison.Ordinal);
        Assert.Contains("required-marker", rendered, StringComparison.Ordinal);
        Assert.Equal(32, rendered.Split("[truncated]", StringSplitOptions.None).Length - 1);
    }
}
