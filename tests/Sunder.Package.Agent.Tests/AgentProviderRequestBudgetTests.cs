using Microsoft.Extensions.AI;
using System.Text;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentProviderRequestBudgetTests
{
    [Fact]
    public void Resolve_ReservesOutputAndCompactionHeadroom()
    {
        var limits = AgentProviderRequestLimits.Resolve(new AgentProviderRunCapabilities(
            SupportsNativeToolCalling: true,
            SupportsStreamingToolCalls: true,
            SupportsMultipleToolCalls: true,
            Summary: "test",
            ContextWindowTokens: 100_000,
            MaxOutputTokens: 32_000));

        Assert.Equal(100_000, limits.ContextWindowTokens);
        Assert.Equal(32_000, limits.OutputReserveTokens);
        Assert.Equal(17_000, limits.CompactionBufferTokens);
        Assert.Equal(68_000, limits.HardInputLimitTokens);
        Assert.Equal(51_000, limits.ProactiveInputLimitTokens);
    }

    [Fact]
    public void Assess_CountsTextToolsAndBinaryOnce()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.User,
            [
                new TextContent(new string('x', 400)),
                new DataContent(new byte[300], "image/png"),
            ]),
        };
        var tools = new[]
        {
            new AgentToolDescriptor("read", "Read", "Read a file", ArgumentsJsonSchema: "{\"type\":\"object\"}"),
        };

        var assessment = AgentProviderRequestBudget.Assess(
            messages,
            "system",
            tools,
            new AgentProviderRunCapabilities(
                SupportsNativeToolCalling: true,
                SupportsStreamingToolCalls: true,
                SupportsMultipleToolCalls: true,
                Summary: "test",
                ContextWindowTokens: 10_000,
                MaxOutputTokens: 1_000));

        Assert.Equal(300, assessment.Estimate.BinaryBytes);
        Assert.Equal(100, assessment.Estimate.EstimatedMediaTokens);
        Assert.True(assessment.Estimate.EstimatedTextTokens > 100);
        Assert.True(assessment.Estimate.EstimatedInputTokens > assessment.Estimate.EstimatedMediaTokens);
        Assert.True(assessment.FitsHardLimit);
    }

    [Fact]
    public void Assess_TracksMediaBytesWithoutTreatingRawBytesAsTextTokens()
    {
        var withoutMedia = AgentProviderRequestBudget.Assess(
            [new ChatMessage(ChatRole.User, "inspect")],
            null,
            [],
            DefaultCapabilities());
        var withMedia = AgentProviderRequestBudget.Assess(
            [new ChatMessage(ChatRole.User, [new TextContent("inspect"), new DataContent(new byte[2_000_000], "image/png")])],
            null,
            [],
            DefaultCapabilities());

        Assert.Equal(2_000_000, withMedia.Estimate.BinaryBytes);
        Assert.InRange(
            withMedia.Estimate.EstimatedInputTokens - withoutMedia.Estimate.EstimatedInputTokens,
            0,
            10);
        Assert.True(withMedia.Estimate.EstimatedMediaTokens > withMedia.Estimate.EstimatedInputTokens);
    }

    [Fact]
    public void Assess_UsesHardLimitForCompactionAdmission()
    {
        var capabilities = new AgentProviderRunCapabilities(
            SupportsNativeToolCalling: true,
            SupportsStreamingToolCalls: true,
            SupportsMultipleToolCalls: true,
            Summary: "test",
            ContextWindowTokens: 10_000,
            MaxOutputTokens: 1_000);
        var proactive = AgentProviderRequestBudget.Assess(
            [new ChatMessage(ChatRole.User, new string('x', 33_000))],
            null,
            [],
            capabilities);
        var overflow = AgentProviderRequestBudget.Assess(
            [new ChatMessage(ChatRole.User, new string('x', 40_000))],
            null,
            [],
            capabilities);

        Assert.False(proactive.NeedsCompaction);
        Assert.True(proactive.FitsHardLimit);
        Assert.True(overflow.NeedsCompaction);
        Assert.False(overflow.FitsHardLimit);
    }

    [Fact]
    public void Assess_RejectsBinaryPayloadBeyondRequestLimitWithoutCountingItAsText()
    {
        var assessment = AgentProviderRequestBudget.Assess(
            [new ChatMessage(ChatRole.User,
            [
                new DataContent(new byte[25 * 1024 * 1024], "image/png"),
            ])],
            null,
            [],
            DefaultCapabilities());

        Assert.Equal(25L * 1024 * 1024, assessment.Estimate.BinaryBytes);
        Assert.True(assessment.Estimate.EstimatedPayloadBytes > 32L * 1024 * 1024);
        Assert.False(assessment.FitsPayloadLimit);
        Assert.False(assessment.FitsHardLimit);
        Assert.True(assessment.NeedsCompaction);
        Assert.True(assessment.TargetReductionTokens > 0);
    }

    [Fact]
    public void EstimateTurnTokens_UsesUtf8UnitsForNonAsciiText()
    {
        var text = string.Concat(Enumerable.Repeat("\u754c", 4_000));
        var turnId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var turn = new AgentTurnRecord(
            turnId,
            Guid.NewGuid(),
            AgentMessageRole.User,
            AgentTurnKind.Message,
            [new AgentTurnItemRecord(
                Guid.NewGuid(),
                turnId,
                0,
                AgentTurnItemKind.Text,
                text,
                null,
                null,
                null,
                null,
                null,
                null,
                false,
                false,
                null,
                null)],
            now,
            now);

        var expected = 16 + ((Encoding.UTF8.GetByteCount(text) + 3) / 4);

        Assert.Equal(expected, AgentProviderRequestBudget.EstimateTurnTokens(turn));
    }

    [Fact]
    public void Assess_IncludesEscapedToolCallAndResultIdsInPayloadSize()
    {
        var shortId = AssessToolExchangePayload("call-1");
        var escapedLongId = AssessToolExchangePayload(new string('"', 512));

        Assert.True(escapedLongId > shortId + 1_000);
    }


    private static AgentProviderRunCapabilities DefaultCapabilities()
        => new(
            SupportsNativeToolCalling: true,
            SupportsStreamingToolCalls: true,
            SupportsMultipleToolCalls: true,
            Summary: "test",
            ContextWindowTokens: 128_000,
            MaxOutputTokens: 8_192);

    private static long AssessToolExchangePayload(string callId)
        => AgentProviderRequestBudget.Assess(
            [
                new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent(callId, "test-tool", new Dictionary<string, object?>())]),
                new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(callId, "complete")]),
            ],
            null,
            [],
            DefaultCapabilities()).Estimate.EstimatedPayloadBytes;
}
