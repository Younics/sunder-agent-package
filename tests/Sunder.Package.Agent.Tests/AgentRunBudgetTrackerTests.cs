using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentRunBudgetTrackerTests
{
    [Fact]
    public void DefaultLimits_BoundTheWholeRun()
    {
        var limits = AgentRunBudgetLimits.Default;

        Assert.Equal(TimeSpan.FromMinutes(30), limits.MaxWallClock);
        Assert.Equal(64, limits.MaxProviderCycles);
        Assert.Equal(128, limits.MaxToolCalls);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveLimits()
    {
        var limits = new AgentRunBudgetLimits(TimeSpan.Zero, 1, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentRunBudgetTracker(limits));
    }

    [Fact]
    public void ChargeProviderAttempt_AppliesInitialStateAcrossContinuation()
    {
        var tracker = new AgentRunBudgetTracker(
            new AgentRunBudgetLimits(TimeSpan.FromMinutes(1), 1, 10),
            new AgentRunBudgetState(ProviderCycles: 1, ToolCalls: 0, SubmittedContextTokens: 10));

        var exception = Assert.Throws<AgentRunBudgetExceededException>(() =>
            tracker.ChargeProviderAttempt(10));

        Assert.Equal(AgentRunBudgetKind.ProviderCycles, exception.Violation.Kind);
        Assert.Equal(2, exception.Violation.Consumed);
    }

    [Fact]
    public void Charges_UseDurableCounterAsSourceOfTruth()
    {
        var durableState = new AgentRunBudgetState(4, 8, 16);
        var tracker = new AgentRunBudgetTracker(
            new AgentRunBudgetLimits(TimeSpan.FromMinutes(1), 10, 10),
            durableCharge: charge => durableState = new AgentRunBudgetState(
                durableState.ProviderCycles + charge.ProviderCycles,
                durableState.ToolCalls + charge.ToolCalls,
                durableState.SubmittedContextTokens + charge.SubmittedContextTokens));

        tracker.ChargeToolCalls(2);

        Assert.Equal(new AgentRunBudgetState(4, 10, 16), durableState);
        var exception = Assert.Throws<AgentRunBudgetExceededException>(() => tracker.ChargeToolCalls(1));
        Assert.Equal(11, exception.Violation.Consumed);
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
                HostIdentity = "sunder.host.scoped-instruction.v1",
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
