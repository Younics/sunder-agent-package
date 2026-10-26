extern alias AgentCore;
using AgentCore::Sunder.Package.Agent.Contracts.Models;
using Xunit;
using Presentation = AgentCore::Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.Tests;

public sealed partial class TranscriptPresentationStateTests
{
    [Fact]
    public void OperationHeaderCacheIsBoundedEvenWhenOneRetainedTurnChangesReferences()
    {
        using var timeline = CreateTimeline(4, 2, 4);
        var session = Guid.NewGuid();
        var turn = CreateToolResultTurn(session, Guid.NewGuid(), "one-call");
        Assert.True(timeline.TryCompleteInitialLoad(timeline.BeginInitialLoad(session), [turn]));
        for (var revision = 2; revision < 50; revision++)
            timeline.ApplyLiveTurn(turn with { ContentRevision = revision, Items = [turn.Items[0] with
            {
                PresentationPayloadJson = $"{{\"operationStatus\":\"unknown\",\"operationRef\":\"o-{revision}\"}}",
            }] });
        var cache = typeof(Presentation.TranscriptRowProjector<TestRow>).GetField("_operationHeaders",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(timeline.Projector);
        Assert.Single(Assert.IsAssignableFrom<System.Collections.IDictionary>(cache));
        Assert.Single(timeline.Projector.Rows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OperationChecksRefreshVisibleHeadersWithoutRewritingReceiptsOrMovingDetachedReaders(bool detached)
    {
        using var timeline = CreateTimeline(8, 4, 8);
        var session = Guid.NewGuid();
        var original = Receipt("apply", "maker_apply", "running", 1);
        Assert.True(timeline.TryCompleteInitialLoad(timeline.BeginInitialLoad(session), [original]));
        var row = Assert.Single(timeline.Projector.Rows);
        if (detached) Assert.True(timeline.DetachFromLatest());
        timeline.ApplyLiveTurn(Receipt("check", "maker_operation", "applied", 2));
        Assert.Equal("Applied", row.Status);
        Assert.Equal(!detached, timeline.IsFollowingLatest);
        timeline.ApplyLiveTurn(Receipt("unavailable", "maker_operation", "unknown", 3));
        Assert.Equal("Applied", row.Status); // A later outage cannot erase an observed effect.
        timeline.ApplyLiveTurn(Receipt("foreign", "maker_operation", "failed", 4) with { SessionId = Guid.NewGuid() });
        Assert.Equal("Applied", row.Status);
        Assert.Contains("running", Assert.Single(timeline.Projector.TurnWindow.OrderedTurns().First().Items).PresentationPayloadJson);
        Assert.False(timeline.IsInitialLoading);

        AgentTurnRecord Receipt(string call, string tool, string status, int seconds)
        {
            var turn = CreateToolResultTurn(session, Guid.NewGuid(), call);
            return turn with { CreatedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(seconds), Items = [turn.Items[0] with
            {
                ToolId = tool, ToolExecutionStatus = AgentToolExecutionStatus.Completed,
                PresentationPayloadJson = $"{{\"operationStatus\":\"{status}\",\"operationRef\":\"o-original\"}}",
            }] };
        }
    }
}
