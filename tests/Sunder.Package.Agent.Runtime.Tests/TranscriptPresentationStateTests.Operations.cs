extern alias AgentCore;
using AgentCore::Sunder.Package.Agent.Contracts.Models;
using Xunit;
using Presentation = AgentCore::Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.Tests;

public sealed partial class TranscriptPresentationStateTests
{
    [Fact]
    public void PresentationReferenceChangesDoNotCreateObservationSidecars()
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
        Assert.Null(typeof(Presentation.TranscriptRowProjector<TestRow>).GetField("_operationHeaders",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance));
        Assert.Single(timeline.Projector.Rows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PresentationReferencesNeverOverwriteAnotherExecutionOrMoveDetachedReaders(bool detached)
    {
        using var timeline = CreateTimeline(8, 4, 8);
        var session = Guid.NewGuid();
        var original = Receipt("apply", "maker_apply", "running", 1);
        Assert.True(timeline.TryCompleteInitialLoad(timeline.BeginInitialLoad(session), [original]));
        var row = Assert.Single(timeline.Projector.Rows);
        if (detached) Assert.True(timeline.DetachFromLatest());
        timeline.ApplyLiveTurn(Receipt("other", "maker_apply", "failed", 2));
        Assert.Equal("Completed", row.Status);
        Assert.Equal(!detached, timeline.IsFollowingLatest);
        timeline.ApplyLiveTurn(Receipt("another", "maker_apply", "unknown", 3));
        Assert.Equal("Completed", row.Status);
        timeline.ApplyLiveTurn(Receipt("foreign", "maker_apply", "failed", 4) with { SessionId = Guid.NewGuid() });
        Assert.Equal("Completed", row.Status);
        Assert.Null(Assert.Single(timeline.Projector.TurnWindow.OrderedTurns().First().Items).PresentationPayloadJson);
        Assert.Contains("running", original.Items[0].PresentationPayloadJson);
        Assert.False(timeline.IsInitialLoading);

        AgentTurnRecord Receipt(string call, string tool, string status, int seconds)
        {
            var turn = CreateToolResultTurn(session, Guid.NewGuid(), call);
            return turn with { CreatedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(seconds), Items = [turn.Items[0] with
            {
                ToolId = tool, ToolExecutionStatus = status == "failed" ? AgentToolExecutionStatus.Failed : AgentToolExecutionStatus.Completed,
                PresentationPayloadJson = $"{{\"operationStatus\":\"{status}\",\"operationRef\":\"o-original\"}}",
            }] };
        }
    }
}
