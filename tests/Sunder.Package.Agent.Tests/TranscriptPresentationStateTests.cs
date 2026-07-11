extern alias AgentCore;

using Sunder.Package.Agent.Contracts.Models;
using Xunit;
using CorePresentation = AgentCore::Sunder.Package.Agent.Shared.PackageViews;
using CoreViews = AgentCore::Sunder.Package.Agent.PackageViews;

namespace Sunder.Package.Agent.Tests;

public sealed class TranscriptPresentationStateTests
{
    [Fact]
    public void Timeline_InitialLoadSelectsLatestPageAndRejectsStaleSessionCompletion()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();
        var loadA = timeline.BeginInitialLoad(sessionA);
        var loadB = timeline.BeginInitialLoad(sessionB);

        Assert.False(timeline.TryCompleteInitialLoad(loadA, CreateTurns(sessionA, 0, 4)));
        Assert.True(timeline.TryCompleteInitialLoad(loadB, CreateTurns(sessionB, 0, 7)));

        Assert.Equal(["message-3", "message-4", "message-5", "message-6"],
            timeline.Projector.Rows.Select(row => row.Content));
        Assert.True(timeline.HasOlderRows);
        Assert.Equal(sessionB, timeline.SessionId);
    }

    [Fact]
    public async Task Timeline_LoadOlderRetainsViewportAnchorAndMarksNewerRows()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var load = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(load, CreateTurns(sessionId, 0, 10)));
        var anchor = timeline.Projector.Rows.Single(row => row.Content == "message-8").AnchorKey;

        var loaded = await timeline.LoadOlderAsync(
            (_, _, _, _, _) => Task.FromResult<IReadOnlyList<AgentTurnRecord>>(
                CreateTurns(sessionId, 3, 3)),
            anchor);

        Assert.True(loaded);
        Assert.Contains(timeline.Projector.Rows, row => Equals(row.AnchorKey, anchor));
        Assert.True(timeline.HasNewerRows);
        Assert.Equal(anchor, timeline.ViewportAnchor?.AnchorKey);
    }

    [Fact]
    public async Task Timeline_FollowTailBuffersThenResumesAndJumpRestoresFollowing()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var load = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(load, CreateTurns(sessionId, 0, 4)));
        var liveTurn = CreateTurns(sessionId, 4, 1).Single();

        timeline.DetachFromLatest();
        Assert.Equal(CorePresentation.TranscriptLiveTurnResult.Buffered, timeline.ApplyLiveTurn(liveTurn));
        Assert.False(timeline.IsFollowingLatest);
        Assert.True(timeline.HasNewerRows);

        Assert.True(await timeline.LoadNewerAsync(
            (_, _, _, _, _) => Task.FromResult<IReadOnlyList<AgentTurnRecord>>([liveTurn])));
        Assert.True(timeline.IsFollowingLatest);
        Assert.False(timeline.HasNewerRows);
        Assert.Contains(timeline.Projector.Rows, row => row.Content == "message-4");

        timeline.DetachFromLatest();
        Assert.True(timeline.RequestJumpToLatest());
        Assert.True(timeline.IsFollowingLatest);
    }

    [Fact]
    public void Timeline_OwnsSelectionExpansionAndViewportAnchorState()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var load = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(load, CreateTurns(sessionId, 0, 1)));
        var row = Assert.Single(timeline.Projector.Rows);
        var anchor = new CorePresentation.TranscriptViewportAnchorData(row.AnchorKey, 12, 30);

        timeline.SelectRow(row);
        timeline.SetRowExpanded(row, true);
        timeline.SetViewportAnchor(anchor);

        Assert.Equal(row.AnchorKey, timeline.SelectedAnchorKey);
        Assert.Contains(row.AnchorKey, timeline.ExpandedAnchorKeys);
        Assert.True(row.IsExpanded);
        Assert.Equal(anchor, timeline.ViewportAnchor);
    }

    [Fact]
    public void RowProjectorClassifiesMessageReasoningToolPermissionActivityAndErrorRows()
    {
        var sessionId = Guid.NewGuid();
        var message = CreateTurns(sessionId, 0, 1).Single();
        var toolCallItem = CreateToolItem(AgentTurnItemKind.ToolCall, isError: false);
        var toolResultItem = CreateToolItem(AgentTurnItemKind.ToolResult, isError: false);
        var errorItem = CreateToolItem(AgentTurnItemKind.ToolResult, isError: true);

        Assert.Equal(CorePresentation.TranscriptProjectedRowKind.Message,
            CorePresentation.TranscriptRowProjector<TestRow>.DescribeMessage(message).Kind);
        Assert.Equal(CorePresentation.TranscriptProjectedRowKind.ToolCall,
            CorePresentation.TranscriptRowProjector<TestRow>.DescribeTool(message, toolCallItem).Kind);
        Assert.Equal(CorePresentation.TranscriptProjectedRowKind.ToolResult,
            CorePresentation.TranscriptRowProjector<TestRow>.DescribeTool(message, toolResultItem).Kind);
        Assert.Equal(CorePresentation.TranscriptProjectedRowKind.Error,
            CorePresentation.TranscriptRowProjector<TestRow>.DescribeTool(message, errorItem).Kind);
        Assert.Equal(CorePresentation.TranscriptProjectedRowKind.Reasoning,
            CorePresentation.TranscriptRowProjector<TestRow>.DescribeActivity("Reasoning", true).Kind);
        Assert.Equal(CorePresentation.TranscriptProjectedRowKind.Activity,
            CorePresentation.TranscriptRowProjector<TestRow>.DescribeActivity("Thinking", false).Kind);
        Assert.Equal(CorePresentation.TranscriptProjectedRowKind.Permission,
            CorePresentation.TranscriptRowProjector<TestRow>.DescribePermission(
                "request-1", "Allow?", "run", null).Kind);
    }

    [Fact]
    public void Composer_RestoresOnlyUncommittedSubmissionState()
    {
        var composer = new CoreViews.AgentComposerState
        {
            Text = "retry this",
            RollbackTurnId = Guid.NewGuid(),
        };
        var sessionId = Guid.NewGuid();
        var submission = Assert.IsType<CoreViews.AgentComposerSubmission>(
            composer.TryBeginSubmission(sessionId));
        composer.ClearSubmitted(submission);

        Assert.True(composer.RestoreUncommitted(submission, sessionId));
        Assert.Equal("retry this", composer.Text);
        Assert.Equal(submission.RollbackTurnId, composer.RollbackTurnId);

        submission.Commit();
        composer.Text = string.Empty;
        composer.RollbackTurnId = null;
        Assert.False(composer.RestoreUncommitted(submission, sessionId));
        Assert.Empty(composer.Text);
    }

    private static CorePresentation.TranscriptTimelineState<TestRow> CreateTimeline(
        int initialLimit,
        int pageSize,
        int visibleLimit)
    {
        var rows = new System.Collections.ObjectModel.ObservableCollection<TestRow>();
        var projector = new CorePresentation.TranscriptRowProjector<TestRow>(
            rows,
            new TestRowFactory(),
            visibleLimit * 2);
        return new CorePresentation.TranscriptTimelineState<TestRow>(
            projector,
            initialLimit,
            pageSize,
            visibleLimit);
    }

    private static AgentTurnRecord[] CreateTurns(Guid sessionId, int start, int count)
        => Enumerable.Range(start, count)
            .Select(index =>
            {
                var turnId = Guid.NewGuid();
                var timestamp = DateTimeOffset.UnixEpoch.AddSeconds(index);
                return new AgentTurnRecord(
                    turnId,
                    sessionId,
                    AgentMessageRole.User,
                    AgentTurnKind.Message,
                    [new AgentTurnItemRecord(
                        Guid.NewGuid(),
                        turnId,
                        0,
                        AgentTurnItemKind.Text,
                        $"message-{index}",
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
                    timestamp,
                    timestamp);
            })
            .ToArray();

    private static AgentTurnItemRecord CreateToolItem(
        AgentTurnItemKind kind,
        bool isError)
        => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            kind,
            isError ? "failed" : "done",
            "call-1",
            "read_file",
            "{\"path\":\"README.md\"}",
            null,
            null,
            null,
            false,
            isError,
            isError ? "failed" : null,
            null);

    private sealed class TestRow(
        Guid rowId,
        object anchorKey,
        string content,
        Guid? resultTurnId = null)
    {
        public Guid RowId { get; } = rowId;
        public object AnchorKey { get; } = anchorKey;
        public string Content { get; set; } = content;
        public Guid? ResultTurnId { get; set; } = resultTurnId;
        public bool IsExpanded { get; set; }
    }

    private sealed class TestRowFactory : CorePresentation.ITranscriptRowFactory<TestRow>
    {
        public TestRow? CreateMessage(
            AgentTurnRecord turn,
            CorePresentation.TranscriptMessageProjection projection)
            => new(turn.TurnId, projection.AnchorKey, projection.Content);

        public void UpdateMessage(
            TestRow row,
            AgentTurnRecord turn,
            CorePresentation.TranscriptMessageProjection projection)
            => row.Content = projection.Content;

        public TestRow CreateTool(
            AgentTurnRecord turn,
            AgentTurnItemRecord item,
            CorePresentation.TranscriptToolProjection projection)
            => new(
                turn.TurnId,
                projection.AnchorKey,
                projection.ToolLabel,
                item.Kind == AgentTurnItemKind.ToolResult ? turn.TurnId : null);

        public void ApplyToolResult(
            TestRow row,
            AgentTurnRecord turn,
            AgentTurnItemRecord item,
            CorePresentation.TranscriptToolProjection projection)
            => row.ResultTurnId = turn.TurnId;

        public TestRow CreateActivity(CorePresentation.TranscriptActivityProjection projection)
            => new(Guid.Empty, projection.AnchorKey, projection.Text);

        public void UpdateActivity(
            TestRow row,
            CorePresentation.TranscriptActivityProjection projection)
            => row.Content = projection.Text;

        public Guid GetRowId(TestRow row) => row.RowId;
        public object GetAnchorKey(TestRow row) => row.AnchorKey;
        public Guid? GetResultTurnId(TestRow row) => row.ResultTurnId;
        public void SetExpanded(TestRow row, bool isExpanded) => row.IsExpanded = isExpanded;
        public void RefreshRelatedRows(TestRow row) { }
        public void DisposeRow(TestRow row) { }
    }
}
