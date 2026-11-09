using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentToolExchangeAnalysisTests
{
    [Fact]
    public void Analyze_ScopesReusedCallIdsByRunRevision()
    {
        var firstRunId = Guid.NewGuid();
        var secondRunId = Guid.NewGuid();
        var firstCall = CreateItem(AgentTurnItemKind.ToolCall, "call-1");
        var secondCall = CreateItem(AgentTurnItemKind.ToolCall, "call-1");
        var secondResult = CreateItem(AgentTurnItemKind.ToolResult, "call-1");
        var firstResult = CreateItem(AgentTurnItemKind.ToolResult, "call-1");
        AgentTurnRecord[] turns =
        [
            CreateTurn(0, firstRunId, 1, firstCall),
            CreateTurn(1, secondRunId, 2, secondCall),
            CreateTurn(2, secondRunId, 2, secondResult),
            CreateTurn(3, firstRunId, 1, firstResult),
        ];

        var analysis = AgentToolExchangeAnalyzer.Analyze(turns);

        Assert.Contains(
            analysis.Pairs,
            pair => pair.CallItemId == firstCall.ItemId && pair.ResultItemId == firstResult.ItemId);
        Assert.Contains(
            analysis.Pairs,
            pair => pair.CallItemId == secondCall.ItemId && pair.ResultItemId == secondResult.ItemId);
        Assert.Equal(4, analysis.PairedItemIds.Count);
        Assert.Empty(analysis.UnpairedItemIds);
    }

    [Fact]
    public void Analyze_PrefersDurableExecutionIdentityForInterleavedCalls()
    {
        var runId = Guid.NewGuid();
        var firstExecutionId = Guid.NewGuid();
        var secondExecutionId = Guid.NewGuid();
        var firstCall = CreateItem(AgentTurnItemKind.ToolCall, "reused") with { ToolExecutionId = firstExecutionId };
        var secondCall = CreateItem(AgentTurnItemKind.ToolCall, "reused") with { ToolExecutionId = secondExecutionId };
        var secondResult = CreateItem(AgentTurnItemKind.ToolResult, "reused") with { ToolExecutionId = secondExecutionId };
        var firstResult = CreateItem(AgentTurnItemKind.ToolResult, "reused") with { ToolExecutionId = firstExecutionId };
        AgentTurnRecord[] turns =
        [
            CreateTurn(0, runId, 1, firstCall),
            CreateTurn(1, runId, 1, secondCall),
            CreateTurn(2, runId, 1, secondResult),
            CreateTurn(3, runId, 1, firstResult),
        ];

        var analysis = AgentToolExchangeAnalyzer.Analyze(turns);

        Assert.Contains(
            analysis.Pairs,
            pair => pair.CallItemId == firstCall.ItemId && pair.ResultItemId == firstResult.ItemId);
        Assert.Contains(
            analysis.Pairs,
            pair => pair.CallItemId == secondCall.ItemId && pair.ResultItemId == secondResult.ItemId);
        Assert.Equal(4, analysis.PairedItemIds.Count);
        Assert.Empty(analysis.UnpairedItemIds);
    }

    [Fact]
    public void Analyze_PairsLegacyRunlessCallWithCompatibleStampedResult()
    {
        var runId = Guid.NewGuid();
        var call = CreateItem(AgentTurnItemKind.ToolCall, "legacy-call");
        var result = CreateItem(AgentTurnItemKind.ToolResult, "legacy-call");
        AgentTurnRecord[] turns =
        [
            CreateTurn(0, runId, 1, call) with { RunId = null, RunRevision = null },
            CreateTurn(1, runId, 1, result),
        ];

        var analysis = AgentToolExchangeAnalyzer.Analyze(turns);

        var pair = Assert.Single(analysis.Pairs);
        Assert.Equal(call.ItemId, pair.CallItemId);
        Assert.Equal(result.ItemId, pair.ResultItemId);
        Assert.Empty(analysis.UnpairedItemIds);
    }

    [Fact]
    public void Analyze_PairsAmbiguousLegacyResultWithNearestPrecedingCall()
    {
        var runId = Guid.NewGuid();
        var firstCall = CreateItem(AgentTurnItemKind.ToolCall, "legacy-call");
        var nearestCall = CreateItem(AgentTurnItemKind.ToolCall, "legacy-call");
        var result = CreateItem(AgentTurnItemKind.ToolResult, "legacy-call");
        AgentTurnRecord[] turns =
        [
            CreateTurn(0, runId, 1, firstCall) with { RunId = null, RunRevision = null },
            CreateTurn(1, runId, 1, nearestCall) with { RunId = null, RunRevision = null },
            CreateTurn(2, runId, 1, result) with { RunId = null, RunRevision = null },
        ];

        var analysis = AgentToolExchangeAnalyzer.Analyze(turns);

        var pair = Assert.Single(analysis.Pairs);
        Assert.Equal(nearestCall.ItemId, pair.CallItemId);
        Assert.Equal(result.ItemId, pair.ResultItemId);
        Assert.Contains(firstCall.ItemId, analysis.UnpairedItemIds);
    }

    [Fact]
    public void Analyze_PairsNearestRunScopedCallWithMatchingToolIdentity()
    {
        var runId = Guid.NewGuid();
        var olderCall = CreateItem(AgentTurnItemKind.ToolCall, "reused") with { ToolId = "older-tool" };
        var matchingCall = CreateItem(AgentTurnItemKind.ToolCall, "reused") with { ToolId = "matching-tool" };
        var result = CreateItem(AgentTurnItemKind.ToolResult, "reused") with { ToolId = "matching-tool" };
        AgentTurnRecord[] turns =
        [
            CreateTurn(0, runId, 1, olderCall),
            CreateTurn(1, runId, 1, matchingCall),
            CreateTurn(2, runId, 1, result),
        ];

        var analysis = AgentToolExchangeAnalyzer.Analyze(turns);

        var pair = Assert.Single(analysis.Pairs);
        Assert.Equal(matchingCall.ItemId, pair.CallItemId);
        Assert.Equal(result.ItemId, pair.ResultItemId);
        Assert.Contains(olderCall.ItemId, analysis.UnpairedItemIds);
    }

    [Fact]
    public void Analyze_DoesNotCrossConflictingKnownRunIdsWhenRevisionsAreMissing()
    {
        var call = CreateItem(AgentTurnItemKind.ToolCall, "reused");
        var result = CreateItem(AgentTurnItemKind.ToolResult, "reused");
        AgentTurnRecord[] turns =
        [
            CreateTurn(0, Guid.NewGuid(), 1, call) with { RunRevision = null },
            CreateTurn(1, Guid.NewGuid(), 1, result) with { RunRevision = null },
        ];

        var analysis = AgentToolExchangeAnalyzer.Analyze(turns);

        Assert.Empty(analysis.Pairs);
        Assert.Contains(call.ItemId, analysis.UnpairedItemIds);
        Assert.Contains(result.ItemId, analysis.UnpairedItemIds);
    }

    private static AgentTurnRecord CreateTurn(
        int index,
        Guid runId,
        long runRevision,
        AgentTurnItemRecord item)
    {
        var timestamp = DateTimeOffset.Parse("2026-07-30T12:00:00Z").AddSeconds(index);
        return new AgentTurnRecord(
            item.TurnId,
            Guid.NewGuid(),
            item.Kind == AgentTurnItemKind.ToolCall ? AgentMessageRole.Assistant : AgentMessageRole.Tool,
            item.Kind == AgentTurnItemKind.ToolCall ? AgentTurnKind.ToolCall : AgentTurnKind.ToolResult,
            [item],
            timestamp,
            timestamp)
        {
            RunId = runId,
            RunRevision = runRevision,
        };
    }

    private static AgentTurnItemRecord CreateItem(AgentTurnItemKind kind, string callId)
    {
        var turnId = Guid.NewGuid();
        return new AgentTurnItemRecord(
            Guid.NewGuid(),
            turnId,
            0,
            kind,
            TextContent: kind == AgentTurnItemKind.ToolResult ? "result" : null,
            CallId: callId,
            ToolId: "test-tool",
            ArgumentsJson: kind == AgentTurnItemKind.ToolCall ? "{}" : null,
            ResultSummary: kind == AgentTurnItemKind.ToolResult ? "complete" : null,
            StructuredPayloadJson: null,
            SourcesJson: null,
            WasTruncated: false,
            IsError: false,
            ErrorCode: null,
            BackendId: null);
    }
}
