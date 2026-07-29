extern alias AgentCore;

using Sunder.Package.Agent.Contracts.Models;
using Xunit;
using CorePresentation = AgentCore::Sunder.Package.Agent.Shared.PackageViews;
using CoreViews = AgentCore::Sunder.Package.Agent.PackageViews;

namespace Sunder.Package.Agent.Tests;

public sealed class TranscriptPresentationStateTests
{
    [Fact]
    public void Timeline_AnchoredInitialLoadRetainsTargetAndEnablesBidirectionalPaging()
    {
        using var timeline = CreateTimeline(initialLimit: 10, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var turns = CreateTurns(sessionId, 0, 7);
        var targetAnchorKey = CorePresentation.TranscriptRowAnchorKey.Text(turns[3].TurnId);
        var load = timeline.BeginAnchoredInitialLoad(sessionId);

        Assert.True(timeline.TryCompleteAnchoredInitialLoad(
            load,
            turns,
            hasOlderRows: true,
            hasNewerRows: true,
            targetAnchorKey));

        Assert.False(timeline.IsFollowingLatest);
        Assert.True(timeline.CanLoadOlder);
        Assert.True(timeline.CanLoadNewer);
        Assert.Equal(targetAnchorKey, timeline.SelectedAnchorKey);
        Assert.Equal(targetAnchorKey, timeline.ViewportAnchor?.AnchorKey);
        Assert.Contains(timeline.Projector.Rows, row => Equals(row.AnchorKey, targetAnchorKey));

        var live = CreateTurns(sessionId, 20, 1)[0];
        Assert.Equal(CorePresentation.TranscriptLiveTurnResult.Buffered, timeline.ApplyLiveTurn(live));
        Assert.True(timeline.HasNewerRows);
    }

    [Fact]
    public void Timeline_AnchoredInitialLoadRetainsProtectedParallelToolTurnAndNewerPaging()
    {
        using var timeline = CreateTimeline(initialLimit: 60, pageSize: 30, visibleLimit: 60);
        var sessionId = Guid.NewGuid();
        var turn = CreateToolTurn(sessionId, Guid.NewGuid(), itemCount: 90);
        var newerTurns = CreateTurns(sessionId, 10, 2);
        var targetItem = turn.Items[10];
        var targetAnchorKey = CorePresentation.TranscriptRowAnchorKey.Tool(turn, targetItem);
        var load = timeline.BeginAnchoredInitialLoad(sessionId);

        Assert.True(timeline.TryCompleteAnchoredInitialLoad(
            load,
            [turn, .. newerTurns],
            hasOlderRows: true,
            hasNewerRows: false,
            targetAnchorKey));

        Assert.Equal(90, timeline.Projector.Rows.Count);
        Assert.Equal(targetAnchorKey, timeline.SelectedAnchorKey);
        Assert.Equal(targetAnchorKey, timeline.ViewportAnchor?.AnchorKey);
        Assert.All(turn.Items, item => Assert.Contains(
            timeline.Projector.Rows,
            row => Equals(row.AnchorKey, CorePresentation.TranscriptRowAnchorKey.Tool(turn, item))));
        Assert.DoesNotContain(
            timeline.Projector.Rows,
            row => newerTurns.Any(newerTurn => newerTurn.TurnId == row.RowId));
        Assert.True(timeline.HasNewerRows);
        Assert.True(timeline.CanLoadNewer);
    }

    [Fact]
    public async Task Timeline_OlderPageTrimResolvesLatestPageAnchorAuthority()
    {
        using var timeline = CreateTimeline(initialLimit: 60, pageSize: 30, visibleLimit: 60);
        var sessionId = Guid.NewGuid();
        var turns = CreateTurns(sessionId, 0, 90);
        var initial = timeline.BeginAnchoredInitialLoad(sessionId);
        var capturedKey = CorePresentation.TranscriptRowAnchorKey.Text(turns[30].TurnId);
        var currentKey = capturedKey;
        Assert.True(timeline.TryCompleteAnchoredInitialLoad(
            initial,
            turns[30..],
            hasOlderRows: true,
            hasNewerRows: false,
            capturedKey));
        var releasePage = new TaskCompletionSource<CorePresentation.TranscriptTurnPage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var load = timeline.LoadOlderAsync(
            (_, _, _, _, _) => releasePage.Task,
            new CorePresentation.TranscriptPageAnchorAuthority(capturedKey, () => currentKey));

        currentKey = CorePresentation.TranscriptRowAnchorKey.Text(turns[85].TurnId);
        releasePage.TrySetResult(new CorePresentation.TranscriptTurnPage(turns[..30], false));
        Assert.True(await load);

        Assert.Equal(60, timeline.Projector.Rows.Count);
        Assert.Contains(timeline.Projector.Rows, row => Equals(row.AnchorKey, currentKey));
        Assert.DoesNotContain(
            timeline.Projector.Rows,
            row => Equals(row.AnchorKey, CorePresentation.TranscriptRowAnchorKey.Text(turns[0].TurnId)));
    }

    [Fact]
    public async Task Timeline_NewerPageTrimResolvesLatestPageAnchorAuthority()
    {
        using var timeline = CreateTimeline(initialLimit: 60, pageSize: 30, visibleLimit: 60);
        var sessionId = Guid.NewGuid();
        var turns = CreateTurns(sessionId, 0, 90);
        var initial = timeline.BeginAnchoredInitialLoad(sessionId);
        var capturedKey = CorePresentation.TranscriptRowAnchorKey.Text(turns[55].TurnId);
        var currentKey = capturedKey;
        Assert.True(timeline.TryCompleteAnchoredInitialLoad(
            initial,
            turns[..60],
            hasOlderRows: false,
            hasNewerRows: true,
            capturedKey));
        var releasePage = new TaskCompletionSource<CorePresentation.TranscriptTurnPage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var load = timeline.LoadNewerAsync(
            (_, _, _, _, _) => releasePage.Task,
            new CorePresentation.TranscriptPageAnchorAuthority(capturedKey, () => currentKey));

        currentKey = CorePresentation.TranscriptRowAnchorKey.Text(turns[5].TurnId);
        releasePage.TrySetResult(new CorePresentation.TranscriptTurnPage(turns[60..], false));
        Assert.True(await load);

        Assert.Equal(60, timeline.Projector.Rows.Count);
        Assert.Contains(timeline.Projector.Rows, row => Equals(row.AnchorKey, currentKey));
        Assert.DoesNotContain(
            timeline.Projector.Rows,
            row => Equals(row.AnchorKey, CorePresentation.TranscriptRowAnchorKey.Text(turns[^1].TurnId)));
    }

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
    public void Timeline_AppliesRevisionedAppendAndCompletionWithoutReplacingRow()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var initialTurn = CreateMessageTurn(
            sessionId,
            turnId,
            "##",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch) with
        {
            ContentRevision = 1,
            IsStreaming = true,
        };
        var load = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(load, [initialTurn]));
        var row = Assert.Single(timeline.Projector.Rows);

        var appended = timeline.ApplyLiveMutation(new AgentTurnMutation(
            sessionId,
            turnId,
            ContentRevision: 2,
            AgentTurnMutationKind.Append,
            BaseContentLength: 2,
            Text: " Heading\n",
            DateTimeOffset.UnixEpoch.AddSeconds(1)));
        var completed = timeline.ApplyLiveMutation(new AgentTurnMutation(
            sessionId,
            turnId,
            ContentRevision: 3,
            AgentTurnMutationKind.Complete,
            BaseContentLength: "## Heading\n".Length,
            Text: null,
            DateTimeOffset.UnixEpoch.AddSeconds(2)));

        Assert.Equal(CorePresentation.TranscriptLiveTurnResult.Applied, appended);
        Assert.Equal(CorePresentation.TranscriptLiveTurnResult.Applied, completed);
        Assert.Same(row, Assert.Single(timeline.Projector.Rows));
        Assert.Equal("## Heading\n", row.Content);
        Assert.False(timeline.Projector.TurnWindow.OrderedTurns().Single().IsStreaming);
    }

    [Fact]
    public void Timeline_StreamingAppendAndCompletionDoNotPublishStructuralRowChanges()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var initialTurn = CreateMessageTurn(
            sessionId,
            turnId,
            "start",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch) with
        {
            ContentRevision = 1,
            IsStreaming = true,
        };
        var load = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(load, [initialTurn]));
        var row = Assert.Single(timeline.Projector.Rows);
        var changingCount = 0;
        var changedCount = 0;
        timeline.RowsChanging += _ => changingCount++;
        timeline.RowsChanged += () => changedCount++;

        Assert.Equal(CorePresentation.TranscriptLiveTurnResult.Applied, timeline.ApplyLiveMutation(
            new AgentTurnMutation(
                sessionId,
                turnId,
                ContentRevision: 2,
                AgentTurnMutationKind.Append,
                BaseContentLength: 5,
                Text: " appended",
                DateTimeOffset.UnixEpoch.AddSeconds(1))));
        Assert.Equal(CorePresentation.TranscriptLiveTurnResult.Applied, timeline.ApplyLiveMutation(
            new AgentTurnMutation(
                sessionId,
                turnId,
                ContentRevision: 3,
                AgentTurnMutationKind.Complete,
                BaseContentLength: "start appended".Length,
                Text: null,
                DateTimeOffset.UnixEpoch.AddSeconds(2))));

        Assert.Same(row, Assert.Single(timeline.Projector.Rows));
        Assert.Equal("start appended", row.Content);
        Assert.Equal(0, changingCount);
        Assert.Equal(0, changedCount);
    }

    [Fact]
    public void Timeline_FirstStreamingAppendPublishesPairedStructuralRowChange()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var initialTurn = CreateMessageTurn(
            sessionId,
            turnId,
            string.Empty,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch) with
        {
            ContentRevision = 1,
            IsStreaming = true,
        };
        var load = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(load, [initialTurn]));
        Assert.Empty(timeline.Projector.Rows);
        var changingCount = 0;
        var changedCount = 0;
        timeline.RowsChanging += _ => changingCount++;
        timeline.RowsChanged += () => changedCount++;

        var result = timeline.ApplyLiveMutation(new AgentTurnMutation(
            sessionId,
            turnId,
            ContentRevision: 2,
            AgentTurnMutationKind.Append,
            BaseContentLength: 0,
            Text: "first token",
            DateTimeOffset.UnixEpoch.AddSeconds(1)));

        Assert.Equal(CorePresentation.TranscriptLiveTurnResult.Applied, result);
        Assert.Equal("first token", Assert.Single(timeline.Projector.Rows).Content);
        Assert.Equal(1, changingCount);
        Assert.Equal(1, changedCount);
    }

    [Fact]
    public void Timeline_ProjectionCallbackStartsIndependentReplacementLoad()
    {
        using var timeline = CreateTimeline(initialLimit: 10, pageSize: 5, visibleLimit: 10);
        var sessionId = Guid.NewGuid();
        var turn = CreateTurns(sessionId, 0, 1)[0];
        CorePresentation.TranscriptLoadTicket? replacementTicket = null;
        timeline.TurnProjected += (_, _, _) =>
        {
            replacementTicket ??= timeline.BeginInitialLoad(sessionId, forceReplacement: true);
        };
        var initialTicket = timeline.BeginInitialLoad(sessionId);

        Assert.True(timeline.TryCompleteInitialLoad(initialTicket, [turn]));

        Assert.NotNull(replacementTicket);
        Assert.True(timeline.IsInitialLoading);
        Assert.True(timeline.TryCompleteInitialLoad(replacementTicket.Value, [turn]));
    }

    [Fact]
    public void Timeline_ReapplyingIdenticalActivityDoesNotPublishRowChanges()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var load = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(load, []));
        timeline.ApplyActivity("Thinking", isReasoning: false, isVisible: true);
        var activityRow = Assert.Single(timeline.Projector.Rows);
        var changingCount = 0;
        var changedCount = 0;
        timeline.RowsChanging += _ => changingCount++;
        timeline.RowsChanged += () => changedCount++;

        timeline.ApplyActivity("Thinking", isReasoning: false, isVisible: true);

        Assert.Same(activityRow, Assert.Single(timeline.Projector.Rows));
        Assert.Equal(0, changingCount);
        Assert.Equal(0, changedCount);
    }

    [Fact]
    public void Timeline_DetachingRemovesTransientActivityRow()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var load = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(load, []));
        timeline.ApplyActivity("Thinking", isReasoning: false, isVisible: true);
        Assert.Single(timeline.Projector.Rows);

        Assert.True(timeline.DetachFromLatest());
        timeline.ApplyActivity("Thinking", isReasoning: false, isVisible: false);

        Assert.Empty(timeline.Projector.Rows);
    }

    [Fact]
    public void Timeline_RequiresReloadWhenMutationRevisionHasGap()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var initialTurn = CreateMessageTurn(
            sessionId,
            turnId,
            "start",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch) with
        {
            ContentRevision = 1,
            IsStreaming = true,
        };
        var load = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(load, [initialTurn]));

        var result = timeline.ApplyLiveMutation(new AgentTurnMutation(
            sessionId,
            turnId,
            ContentRevision: 3,
            AgentTurnMutationKind.Append,
            BaseContentLength: 5,
            Text: " skipped",
            DateTimeOffset.UnixEpoch.AddSeconds(1)));

        Assert.Equal(CorePresentation.TranscriptLiveTurnResult.ReloadRequired, result);
        Assert.Equal("start", Assert.Single(timeline.Projector.Rows).Content);
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
            (_, _, _, _, _) => Task.FromResult(new CorePresentation.TranscriptTurnPage(
                CreateTurns(sessionId, 3, 3),
                true)),
            anchor);

        Assert.True(loaded);
        Assert.Contains(timeline.Projector.Rows, row => Equals(row.AnchorKey, anchor));
        Assert.True(timeline.HasNewerRows);
        Assert.Equal(anchor, timeline.ViewportAnchor?.AnchorKey);
    }

    [Fact]
    public async Task Timeline_InitialContinuationDrivesFirstOlderRequest()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 30, visibleLimit: 60);
        var sessionId = Guid.NewGuid();
        var turns = CreateTurns(sessionId, 0, 1);
        var continuation = new CorePresentation.TranscriptPageCursor(
            DateTimeOffset.UnixEpoch.AddSeconds(-10),
            Guid.NewGuid());
        var initial = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(
            initial,
            turns,
            hasOlderRows: true,
            olderContinuation: continuation));

        Assert.True(await timeline.LoadOlderAsync((_, createdAtUtc, turnId, _, _) =>
        {
            Assert.Equal(continuation.CreatedAtUtc, createdAtUtc);
            Assert.Equal(continuation.TurnId, turnId);
            return Task.FromResult(new CorePresentation.TranscriptTurnPage([], HasMore: false));
        }));
    }

    [Fact]
    public async Task Timeline_TransportFittedShortPagesPreserveExplicitContinuation()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 30, visibleLimit: 60);
        var sessionId = Guid.NewGuid();
        var turns = CreateTurns(sessionId, 0, 4);
        var initial = timeline.BeginAnchoredInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteAnchoredInitialLoad(
            initial,
            turns,
            hasOlderRows: true,
            hasNewerRows: true,
            CorePresentation.TranscriptRowAnchorKey.Text(turns[1].TurnId)));

        var olderContinuation = new CorePresentation.TranscriptPageCursor(
            DateTimeOffset.UnixEpoch.AddSeconds(-10),
            Guid.NewGuid());
        Assert.True(await timeline.LoadOlderAsync((_, _, _, limit, _) =>
        {
            Assert.Equal(30, limit);
            return Task.FromResult(new CorePresentation.TranscriptTurnPage(
                CreateTurns(sessionId, -1, 1),
                HasMore: true,
                Continuation: olderContinuation));
        }));
        Assert.True(timeline.HasOlderRows);
        Assert.True(await timeline.LoadOlderAsync((_, createdAtUtc, turnId, _, _) =>
        {
            Assert.Equal(olderContinuation.CreatedAtUtc, createdAtUtc);
            Assert.Equal(olderContinuation.TurnId, turnId);
            return Task.FromResult(new CorePresentation.TranscriptTurnPage([], HasMore: false));
        }));
        Assert.False(timeline.HasOlderRows);

        var newerContinuation = new CorePresentation.TranscriptPageCursor(
            DateTimeOffset.UnixEpoch.AddSeconds(10),
            Guid.NewGuid());
        Assert.True(await timeline.LoadNewerAsync((_, _, _, limit, _) =>
        {
            Assert.Equal(30, limit);
            return Task.FromResult(new CorePresentation.TranscriptTurnPage(
                CreateTurns(sessionId, 4, 1),
                HasMore: true,
                Continuation: newerContinuation));
        }));
        Assert.True(timeline.HasNewerRows);
        Assert.True(await timeline.LoadNewerAsync((_, createdAtUtc, turnId, _, _) =>
        {
            Assert.Equal(newerContinuation.CreatedAtUtc, createdAtUtc);
            Assert.Equal(newerContinuation.TurnId, turnId);
            return Task.FromResult(new CorePresentation.TranscriptTurnPage([], HasMore: false));
        }));
        Assert.False(timeline.HasNewerRows);
    }

    [Fact]
    public async Task Timeline_LiveUpdateDuringBlockedPageIsNotClassifiedAsPageMutation()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var load = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(load, CreateTurns(sessionId, 0, 10)));
        var pageStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutationOrigins = new List<bool>();
        timeline.RowsChanging += isPageApplication => mutationOrigins.Add(isPageApplication);
        var page = timeline.LoadOlderAsync(async (_, _, _, _, cancellationToken) =>
        {
            pageStarted.TrySetResult();
            await releasePage.Task.WaitAsync(cancellationToken);
            return new CorePresentation.TranscriptTurnPage(
                CreateTurns(sessionId, 3, 3),
                true);
        });
        await pageStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var current = timeline.Projector.TurnWindow.OrderedTurns().Last();
        var liveUpdate = current with
        {
            Items = [current.Items[0] with { TextContent = "live update" }],
            UpdatedAtUtc = current.UpdatedAtUtc.AddSeconds(1),
            ContentRevision = current.ContentRevision + 1,
        };

        Assert.Equal(
            CorePresentation.TranscriptLiveTurnResult.Applied,
            timeline.ApplyLiveTurn(liveUpdate));
        releasePage.TrySetResult();
        Assert.True(await page.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.NotEmpty(mutationOrigins);
        Assert.False(mutationOrigins[0]);
        Assert.Contains(true, mutationOrigins);
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
            (_, _, _, _, _) => Task.FromResult(
                new CorePresentation.TranscriptTurnPage([liveTurn], false))));
        Assert.False(timeline.IsFollowingLatest);
        Assert.False(timeline.HasNewerRows);
        Assert.Contains(timeline.Projector.Rows, row => row.Content == "message-4");

        Assert.True(timeline.ResumeFollowingLatestIfCaughtUp());
        Assert.True(timeline.IsFollowingLatest);

        timeline.DetachFromLatest();
        Assert.True(timeline.RequestJumpToLatest());
        Assert.True(timeline.IsFollowingLatest);
    }

    [Fact]
    public void Timeline_DetachedPendingTurnsRemainBounded()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var load = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(load, CreateTurns(sessionId, 0, 4)));
        Assert.True(timeline.DetachFromLatest());

        foreach (var turn in CreateTurns(sessionId, 4, 100))
        {
            Assert.Equal(CorePresentation.TranscriptLiveTurnResult.Buffered, timeline.ApplyLiveTurn(turn));
        }

        Assert.Equal(8, timeline.PendingTurnCount);
        Assert.Equal(4, timeline.Projector.Rows.Count);
    }

    [Fact]
    public void Timeline_OversizedToolTurnStillHonorsVisibleRowLimit()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var timestamp = DateTimeOffset.UnixEpoch;
        var toolTurn = new AgentTurnRecord(
            turnId,
            sessionId,
            AgentMessageRole.Assistant,
            AgentTurnKind.ToolCall,
            Enumerable.Range(0, 12)
                .Select(index => new AgentTurnItemRecord(
                    Guid.NewGuid(),
                    turnId,
                    index,
                    AgentTurnItemKind.ToolCall,
                    null,
                    $"call-{index}",
                    "read_file",
                    "{}",
                    null,
                    null,
                    null,
                    false,
                    false,
                    null,
                    null))
                .ToArray(),
            timestamp,
            timestamp);
        var load = timeline.BeginInitialLoad(sessionId);

        Assert.True(timeline.TryCompleteInitialLoad(load, [toolTurn]));

        Assert.Equal(4, timeline.Projector.Rows.Count);
    }

    [Fact]
    public void Timeline_FollowingLatestUsesBoundedOverflowBeforeTrimmingLiveRows()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var load = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(load, CreateTurns(sessionId, 0, 4)));
        var removalCount = 0;
        timeline.Projector.Rows.CollectionChanged += (_, eventArgs) =>
        {
            if (eventArgs.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
            {
                removalCount++;
            }
        };

        foreach (var turn in CreateTurns(sessionId, 4, 2))
        {
            Assert.Equal(CorePresentation.TranscriptLiveTurnResult.Applied, timeline.ApplyLiveTurn(turn));
        }

        Assert.Equal(6, timeline.Projector.Rows.Count);
        Assert.Equal(0, removalCount);

        Assert.Equal(
            CorePresentation.TranscriptLiveTurnResult.Applied,
            timeline.ApplyLiveTurn(CreateTurns(sessionId, 6, 1).Single()));

        Assert.Equal(4, timeline.Projector.Rows.Count);
        Assert.True(removalCount > 0);
        Assert.Equal(
            ["message-3", "message-4", "message-5", "message-6"],
            timeline.Projector.Rows.Select(row => row.Content));
    }

    [Fact]
    public void Timeline_DetachingNormalizesBoundedLiveOverflow()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var load = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(load, CreateTurns(sessionId, 0, 4)));
        foreach (var turn in CreateTurns(sessionId, 4, 2))
        {
            Assert.Equal(CorePresentation.TranscriptLiveTurnResult.Applied, timeline.ApplyLiveTurn(turn));
        }
        var rowsChanged = 0;
        timeline.RowsChanged += () => rowsChanged++;

        Assert.True(timeline.DetachFromLatest());

        Assert.Equal(4, timeline.Projector.Rows.Count);
        Assert.Equal(1, rowsChanged);
        Assert.True(timeline.HasOlderRows);
        Assert.Equal(
            ["message-2", "message-3", "message-4", "message-5"],
            timeline.Projector.Rows.Select(row => row.Content));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Timeline_PreservedToolUpdateHonorsVisibleRowLimitWhenReloadCompletesOrFails(
        bool failReload)
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var initialLoad = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(
            initialLoad,
            [CreateToolTurn(sessionId, turnId, itemCount: 1)]));
        Assert.True(timeline.DetachFromLatest());
        var reload = timeline.BeginInitialLoad(sessionId);
        var expandedTurn = CreateToolTurn(sessionId, turnId, itemCount: 12) with
        {
            UpdatedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(1),
        };

        if (failReload)
        {
            Assert.Equal(
                CorePresentation.TranscriptLiveTurnResult.Buffered,
                timeline.ApplyLiveTurn(expandedTurn));
            Assert.True(timeline.TryFailInitialLoad(reload));
        }
        else
        {
            Assert.True(timeline.TryCompleteInitialLoad(reload, [expandedTurn]));
        }

        Assert.Equal(4, timeline.Projector.Rows.Count);
        var retainedAnchorKeys = timeline.Projector.Rows
            .Select(row => row.AnchorKey.ToString()!)
            .ToArray();
        Assert.Equal(
            [
                $"tool:{sessionId:N}:1:call-8",
                $"tool:{sessionId:N}:1:call-9",
                $"tool:{sessionId:N}:1:call-10",
                $"tool:{sessionId:N}:1:call-11",
            ],
            retainedAnchorKeys);
        Assert.Equal(
            CorePresentation.TranscriptLiveTurnResult.Applied,
            timeline.ApplyLiveTurn(expandedTurn));
        Assert.Equal(
            retainedAnchorKeys,
            timeline.Projector.Rows.Select(row => row.AnchorKey.ToString()!));
        Assert.False(timeline.IsFollowingLatest);
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
    public void Timeline_DetailInvalidationClearsExpandedAnchorBookkeeping()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var load = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(load, CreateTurns(sessionId, 0, 1)));
        var row = Assert.Single(timeline.Projector.Rows);
        timeline.SetRowExpanded(row, true);

        row.InvalidateDetail();

        Assert.False(row.IsExpanded);
        Assert.DoesNotContain(row.AnchorKey, timeline.ExpandedAnchorKeys);
    }

    [Fact]
    public void Timeline_SameSessionReloadPreservesDetachedReaderState()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var initialLoad = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(initialLoad, CreateTurns(sessionId, 0, 4)));
        var row = timeline.Projector.Rows[1];
        var anchor = new CorePresentation.TranscriptViewportAnchorData(row.AnchorKey, 24, 80);
        Assert.True(timeline.DetachFromLatest());
        timeline.SetRowExpanded(row, true);
        timeline.SetViewportAnchor(anchor);

        var reload = timeline.BeginInitialLoad(sessionId);

        Assert.False(timeline.IsFollowingLatest);
        Assert.Equal(anchor, timeline.ViewportAnchor);
        Assert.Contains(row.AnchorKey, timeline.ExpandedAnchorKeys);
        Assert.True(timeline.TryCompleteInitialLoad(reload, CreateTurns(sessionId, 0, 4)));
        Assert.False(timeline.IsFollowingLatest);
        Assert.Equal(anchor, timeline.ViewportAnchor);
    }

    [Fact]
    public void Timeline_SameSessionReloadKeepsHistoricalWindowWhenRecentPageMovesForward()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var initialTurns = CreateTurns(sessionId, 0, 4);
        var initialLoad = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(initialLoad, initialTurns));
        var historicalRows = timeline.Projector.Rows.ToArray();
        Assert.True(timeline.DetachFromLatest());

        var reload = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(reload, CreateTurns(sessionId, 10, 4)));

        Assert.Equal(historicalRows, timeline.Projector.Rows);
        Assert.True(timeline.HasNewerRows);
        Assert.Equal(4, timeline.PendingTurnCount);
        Assert.False(timeline.IsFollowingLatest);
    }

    [Fact]
    public async Task Timeline_PreservedSnapshotDoesNotOverwriteNewerBufferedLiveTurn()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var initialLoad = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(initialLoad, CreateTurns(sessionId, 0, 4)));
        Assert.True(timeline.DetachFromLatest());
        var reload = timeline.BeginInitialLoad(sessionId);
        var turnId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UnixEpoch.AddSeconds(10);
        var snapshotTurn = CreateMessageTurn(
            sessionId,
            turnId,
            "snapshot",
            createdAt,
            createdAt);
        var liveTurn = CreateMessageTurn(
            sessionId,
            turnId,
            "live",
            createdAt,
            createdAt.AddSeconds(1));
        Assert.Equal(
            CorePresentation.TranscriptLiveTurnResult.Buffered,
            timeline.ApplyLiveTurn(liveTurn));

        Assert.True(timeline.TryCompleteInitialLoad(reload, [snapshotTurn]));
        Assert.True(await timeline.LoadNewerAsync(
            (_, _, _, _, _) => Task.FromResult(
                new CorePresentation.TranscriptTurnPage([], false))));

        Assert.Contains(timeline.Projector.Rows, row => row.Content == "live");
        Assert.DoesNotContain(timeline.Projector.Rows, row => row.Content == "snapshot");
    }

    [Fact]
    public void Timeline_EqualTimestampSnapshotDoesNotOverwriteProjectedTurn()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UnixEpoch;
        var freshTurn = CreateMessageTurn(
            sessionId,
            turnId,
            "fresh",
            createdAt,
            createdAt.AddSeconds(2));
        var initialLoad = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(initialLoad, [freshTurn]));
        Assert.True(timeline.DetachFromLatest());
        var reload = timeline.BeginInitialLoad(sessionId);

        Assert.True(timeline.TryCompleteInitialLoad(reload, [CreateMessageTurn(
            sessionId,
            turnId,
            "stale",
            createdAt,
            createdAt.AddSeconds(2))]));

        Assert.Equal("fresh", Assert.Single(timeline.Projector.Rows).Content);
    }

    [Fact]
    public void Timeline_AuthoritativeTurnInsertsChronologicallyAndIgnoresMatchingLateAdd()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 6);
        var sessionId = Guid.NewGuid();
        var first = CreateMessageTurn(
            sessionId,
            Guid.NewGuid(),
            "first",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        var third = CreateMessageTurn(
            sessionId,
            Guid.NewGuid(),
            "third",
            DateTimeOffset.UnixEpoch.AddSeconds(2),
            DateTimeOffset.UnixEpoch.AddSeconds(2));
        var load = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(load, [first, third]));
        var middle = CreateMessageTurn(
            sessionId,
            Guid.NewGuid(),
            "middle",
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            DateTimeOffset.UnixEpoch.AddSeconds(1));

        Assert.Equal(
            CorePresentation.TranscriptLiveTurnResult.Applied,
            timeline.ApplyAuthoritativeTurn(middle));
        Assert.Equal(
            ["first", "middle", "third"],
            timeline.Projector.Rows.Select(row => row.Content).ToArray());
        var retainedRows = timeline.Projector.Rows.ToArray();
        var changingCount = 0;
        var changedCount = 0;
        timeline.RowsChanging += _ => changingCount++;
        timeline.RowsChanged += () => changedCount++;

        Assert.Equal(
            CorePresentation.TranscriptLiveTurnResult.Ignored,
            timeline.ApplyLiveTurn(middle));
        Assert.Equal(0, changingCount);
        Assert.Equal(0, changedCount);
        Assert.True(retainedRows.SequenceEqual(
            timeline.Projector.Rows,
            ReferenceEqualityComparer.Instance));
    }

    [Fact]
    public void Timeline_AuthoritativeTurnOlderThanRetainedWindowStaysOutsideWindow()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var load = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(
            load,
            CreateTurns(sessionId, 10, 4),
            hasOlderRows: true));
        var retainedRows = timeline.Projector.Rows.ToArray();
        var oldTurn = CreateMessageTurn(
            sessionId,
            Guid.NewGuid(),
            "outside",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

        Assert.Equal(
            CorePresentation.TranscriptLiveTurnResult.OutsideWindow,
            timeline.ApplyAuthoritativeTurn(oldTurn));
        Assert.True(retainedRows.SequenceEqual(
            timeline.Projector.Rows,
            ReferenceEqualityComparer.Instance));
        Assert.DoesNotContain(timeline.Projector.Rows, row => row.Content == "outside");
    }

    [Fact]
    public void Timeline_InitialSnapshotWinsOverOlderBufferedTurn()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UnixEpoch;
        var load = timeline.BeginInitialLoad(sessionId);
        Assert.Equal(
            CorePresentation.TranscriptLiveTurnResult.Buffered,
            timeline.ApplyLiveTurn(CreateMessageTurn(
                sessionId,
                turnId,
                "stale",
                createdAt,
                createdAt.AddSeconds(1))));

        Assert.True(timeline.TryCompleteInitialLoad(load, [CreateMessageTurn(
            sessionId,
            turnId,
            "fresh",
            createdAt,
            createdAt.AddSeconds(2))]));

        Assert.Equal("fresh", Assert.Single(timeline.Projector.Rows).Content);
    }

    [Fact]
    public void Timeline_EqualTimestampLiveUpdatesUseArrivalOrderWhileLoading()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var timestamp = DateTimeOffset.UnixEpoch;
        var load = timeline.BeginInitialLoad(sessionId);
        Assert.Equal(
            CorePresentation.TranscriptLiveTurnResult.Buffered,
            timeline.ApplyLiveTurn(CreateMessageTurn(
                sessionId,
                turnId,
                "partial",
                timestamp,
                timestamp)));
        Assert.Equal(
            CorePresentation.TranscriptLiveTurnResult.Buffered,
            timeline.ApplyLiveTurn(CreateMessageTurn(
                sessionId,
                turnId,
                "complete",
                timestamp,
                timestamp)));

        Assert.True(timeline.TryCompleteInitialLoad(load, []));

        Assert.Equal("complete", Assert.Single(timeline.Projector.Rows).Content);
    }

    [Fact]
    public async Task Timeline_LoadNewerMergesBufferedTurnsChronologicallyAndKeepsFreshestVersion()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 4, visibleLimit: 10);
        var sessionId = Guid.NewGuid();
        var initialLoad = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(initialLoad, CreateTurns(sessionId, 0, 4)));
        Assert.True(timeline.DetachFromLatest());
        var sharedTurnId = Guid.NewGuid();
        var sharedCreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(12);
        var delayedTurnId = Guid.NewGuid();
        var projectedTurnIds = new List<Guid>();
        timeline.TurnProjected += (turn, _, _) => projectedTurnIds.Add(turn.TurnId);
        Assert.Equal(
            CorePresentation.TranscriptLiveTurnResult.Buffered,
            timeline.ApplyLiveTurn(CreateMessageTurn(
                sessionId,
                delayedTurnId,
                "delayed-2.5",
                DateTimeOffset.UnixEpoch.AddSeconds(2.5),
                DateTimeOffset.UnixEpoch.AddSeconds(14))));
        Assert.Equal(
            CorePresentation.TranscriptLiveTurnResult.Buffered,
            timeline.ApplyLiveTurn(CreateMessageTurn(
                sessionId,
                sharedTurnId,
                "live-12",
                sharedCreatedAt,
                sharedCreatedAt.AddSeconds(2))));

        Assert.True(await timeline.LoadNewerAsync(
            (_, _, _, _, _) => Task.FromResult(new CorePresentation.TranscriptTurnPage(
            [
                CreateMessageTurn(
                    sessionId,
                    Guid.NewGuid(),
                    "server-11",
                    DateTimeOffset.UnixEpoch.AddSeconds(11),
                    DateTimeOffset.UnixEpoch.AddSeconds(11)),
                CreateMessageTurn(
                    sessionId,
                    sharedTurnId,
                    "stale-12",
                    sharedCreatedAt,
                    sharedCreatedAt.AddSeconds(1)),
                CreateMessageTurn(
                    sessionId,
                    Guid.NewGuid(),
                    "server-13",
                    DateTimeOffset.UnixEpoch.AddSeconds(13),
                    DateTimeOffset.UnixEpoch.AddSeconds(13)),
            ], false))));

        Assert.Equal(
            ["message-0", "message-1", "message-2", "delayed-2.5", "message-3", "server-11", "live-12", "server-13"],
            timeline.Projector.Rows.Select(row => row.Content));
        Assert.Equal([delayedTurnId, sharedTurnId], projectedTurnIds);
    }

    [Fact]
    public async Task Timeline_PagingPreservesCapturedViewportAnchorGeometry()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var initialLoad = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(
            initialLoad,
            CreateTurns(sessionId, 0, 4),
            hasOlderRows: true));
        Assert.True(timeline.DetachFromLatest());
        var anchorKey = timeline.Projector.Rows[1].AnchorKey;
        var anchor = new CorePresentation.TranscriptViewportAnchorData(
            anchorKey,
            OffsetY: 42,
            DistanceFromBottom: 84,
            AnchorViewportTop: 12);
        timeline.SetViewportAnchor(anchor);

        Assert.True(await timeline.LoadOlderAsync(
            (_, _, _, _, _) => Task.FromResult(new CorePresentation.TranscriptTurnPage(
                CreateTurns(sessionId, -3, 3),
                true)),
            anchorKey));

        Assert.Equal(anchor, timeline.ViewportAnchor);
    }

    [Fact]
    public void Timeline_QueuePressurePreservesBufferedHistoricalUpdate()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UnixEpoch;
        var initialLoad = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(initialLoad, [CreateMessageTurn(
            sessionId,
            turnId,
            "initial",
            createdAt,
            createdAt)]));
        Assert.True(timeline.DetachFromLatest());
        var reload = timeline.BeginInitialLoad(sessionId);
        Assert.Equal(
            CorePresentation.TranscriptLiveTurnResult.Buffered,
            timeline.ApplyLiveTurn(CreateMessageTurn(
                sessionId,
                turnId,
                "historical-update",
                createdAt,
                createdAt.AddSeconds(1))));
        foreach (var turn in CreateTurns(sessionId, 1, 100))
        {
            timeline.ApplyLiveTurn(turn);
        }

        Assert.True(timeline.TryFailInitialLoad(reload));

        Assert.Equal("historical-update", Assert.Single(timeline.Projector.Rows).Content);
        Assert.Equal(7, timeline.PendingTurnCount);
    }

    [Fact]
    public async Task Timeline_OverflowDuringNewerLoadRejectsStalePageCompletion()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var initialLoad = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(initialLoad, CreateTurns(sessionId, 0, 4)));
        Assert.True(timeline.DetachFromLatest());
        timeline.ApplyLiveTurn(CreateTurns(sessionId, 4, 1).Single());
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadTask = timeline.LoadNewerAsync(async (_, _, _, _, cancellationToken) =>
        {
            loadStarted.TrySetResult();
            await releaseLoad.Task.WaitAsync(cancellationToken);
            return new CorePresentation.TranscriptTurnPage([], false);
        });
        await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        foreach (var turn in CreateTurns(sessionId, 5, 100))
        {
            timeline.ApplyLiveTurn(turn);
        }
        releaseLoad.TrySetResult();

        Assert.False(await loadTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(
            ["message-0", "message-1", "message-2", "message-3"],
            timeline.Projector.Rows.Select(row => row.Content));
        Assert.True(timeline.HasNewerRows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Timeline_OverflowedEmptyDetachedReplacementRetainsRecoveryState(bool failLoad)
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var initialLoad = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(initialLoad, CreateTurns(sessionId, 0, 1)));
        Assert.True(timeline.DetachFromLatest());
        var replacement = timeline.BeginInitialLoad(sessionId, forceReplacement: true);
        foreach (var turn in CreateTurns(sessionId, 1, 100))
        {
            timeline.ApplyLiveTurn(turn);
        }

        Assert.True(failLoad
            ? timeline.TryFailInitialLoad(replacement)
            : timeline.TryCompleteInitialLoad(replacement, []));

        if (failLoad)
        {
            Assert.Equal("message-0", Assert.Single(timeline.Projector.Rows).Content);
        }
        else
        {
            Assert.Empty(timeline.Projector.Rows);
        }
        Assert.Equal(8, timeline.PendingTurnCount);
        Assert.True(timeline.HasNewerRows);
        Assert.False(timeline.IsFollowingLatest);
    }

    [Fact]
    public void Timeline_AuthoritativeReloadReusesExistingMessageRow()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UnixEpoch;
        var initialTurn = CreateMessageTurn(sessionId, turnId, "initial", createdAt, createdAt) with
        {
            ContentRevision = 1,
        };
        var initialLoad = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(initialLoad, [initialTurn]));
        var retainedRow = Assert.Single(timeline.Projector.Rows);
        var replacement = timeline.BeginInitialLoad(sessionId, forceReplacement: true);
        var updatedTurn = initialTurn with
        {
            Items = [initialTurn.Items[0] with { TextContent = "updated" }],
            UpdatedAtUtc = createdAt.AddSeconds(1),
            ContentRevision = 2,
        };

        Assert.True(timeline.TryCompleteInitialLoad(replacement, [updatedTurn]));

        Assert.Same(retainedRow, Assert.Single(timeline.Projector.Rows));
        Assert.Equal("updated", retainedRow.Content);
    }

    [Fact]
    public void Timeline_VisibleRowRetainsBackingTurnAcrossNonProjectingUpdates()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UnixEpoch;
        var initialLoad = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(initialLoad, [CreateMessageTurn(
            sessionId,
            turnId,
            "initial",
            createdAt,
            createdAt)]));
        var retainedRow = Assert.Single(timeline.Projector.Rows);
        for (var index = 1; index <= 20; index++)
        {
            var timestamp = createdAt.AddSeconds(index);
            Assert.Equal(
                CorePresentation.TranscriptLiveTurnResult.Applied,
                timeline.ApplyLiveTurn(CreateMessageTurn(
                    sessionId,
                    Guid.NewGuid(),
                    string.Empty,
                    timestamp,
                    timestamp)));
        }

        Assert.Equal(
            CorePresentation.TranscriptLiveTurnResult.Applied,
            timeline.ApplyLiveTurn(CreateMessageTurn(
                sessionId,
                turnId,
                "updated",
                createdAt,
                createdAt.AddMinutes(1))));

        Assert.Same(retainedRow, Assert.Single(timeline.Projector.Rows));
        Assert.Equal("updated", retainedRow.Content);
    }

    [Fact]
    public void Timeline_FollowingStateWithNewerRowsStillBuffersLiveTurns()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var initialLoad = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(initialLoad, CreateTurns(sessionId, 0, 4)));
        Assert.True(timeline.DetachFromLatest());
        Assert.Equal(
            CorePresentation.TranscriptLiveTurnResult.Buffered,
            timeline.ApplyLiveTurn(CreateTurns(sessionId, 4, 1).Single()));
        Assert.True(timeline.RequestJumpToLatest());

        Assert.Equal(
            CorePresentation.TranscriptLiveTurnResult.Buffered,
            timeline.ApplyLiveTurn(CreateTurns(sessionId, 5, 1).Single()));

        Assert.Equal(2, timeline.PendingTurnCount);
    }

    [Fact]
    public void Timeline_DetachedHistoricalTurnUpdatesInPlace()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var turn = CreateTurns(sessionId, 0, 1).Single();
        var initialLoad = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(initialLoad, [turn]));
        var row = Assert.Single(timeline.Projector.Rows);
        Assert.True(timeline.DetachFromLatest());
        var updatedItem = turn.Items.Single() with { TextContent = "updated while detached" };
        var updatedTurn = turn with { Items = [updatedItem] };

        var result = timeline.ApplyLiveTurn(updatedTurn);

        Assert.Equal(CorePresentation.TranscriptLiveTurnResult.Applied, result);
        Assert.Same(row, Assert.Single(timeline.Projector.Rows));
        Assert.Equal("updated while detached", row.Content);
        Assert.False(timeline.HasNewerRows);
        Assert.False(timeline.IsFollowingLatest);
    }

    [Fact]
    public void Timeline_DetachedHistoricalToolExpansionProtectsTurnAtomically()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var initialLoad = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(
            initialLoad,
            [CreateToolTurn(sessionId, turnId, itemCount: 4)]));
        var protectedAnchor = timeline.Projector.Rows[0].AnchorKey;
        timeline.SetViewportAnchor(new CorePresentation.TranscriptViewportAnchorData(
            protectedAnchor,
            OffsetY: 10,
            DistanceFromBottom: 20));
        Assert.True(timeline.DetachFromLatest());

        Assert.Equal(
            CorePresentation.TranscriptLiveTurnResult.Applied,
            timeline.ApplyLiveTurn(CreateToolTurn(sessionId, turnId, itemCount: 12)));

        Assert.Equal(12, timeline.Projector.Rows.Count);
        Assert.Contains(timeline.Projector.Rows, row => Equals(row.AnchorKey, protectedAnchor));
        Assert.False(timeline.HasOlderRows);
        Assert.False(timeline.HasNewerRows);
        Assert.False(timeline.IsFollowingLatest);
    }

    [Fact]
    public void Timeline_AuthoritativeSameSessionReplacementDropsStaleRowsButPreservesDetachment()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var initialLoad = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(initialLoad, CreateTurns(sessionId, 0, 4)));
        Assert.True(timeline.DetachFromLatest());

        var replacement = timeline.BeginInitialLoad(sessionId, forceReplacement: true);
        Assert.True(timeline.TryCompleteInitialLoad(replacement, CreateTurns(sessionId, 10, 2)));

        Assert.Equal(["message-10", "message-11"], timeline.Projector.Rows.Select(row => row.Content));
        Assert.False(timeline.IsFollowingLatest);
        Assert.False(timeline.HasNewerRows);
    }

    [Fact]
    public void Timeline_AuthoritativeReplacementPurgesPendingTurnsThatPredateTheLoad()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var initialTurns = CreateTurns(sessionId, 0, 1);
        var initialLoad = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(initialLoad, initialTurns));
        Assert.True(timeline.DetachFromLatest());
        Assert.Equal(
            CorePresentation.TranscriptLiveTurnResult.Buffered,
            timeline.ApplyLiveTurn(CreateTurns(sessionId, 1, 1).Single()));

        var replacement = timeline.BeginInitialLoad(sessionId, forceReplacement: true);
        Assert.True(timeline.TryCompleteInitialLoad(replacement, initialTurns));

        Assert.Equal("message-0", Assert.Single(timeline.Projector.Rows).Content);
        Assert.Equal(0, timeline.PendingTurnCount);
        Assert.False(timeline.HasNewerRows);
        Assert.False(timeline.IsFollowingLatest);
    }

    [Fact]
    public void Timeline_AuthoritativeReplacementPrefersHigherRevisionQueuedDuringLoad()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UnixEpoch;
        var initialTurn = CreateMessageTurn(sessionId, turnId, "initial", createdAt, createdAt) with
        {
            ContentRevision = 1,
        };
        var initialLoad = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(initialLoad, [initialTurn]));
        Assert.True(timeline.DetachFromLatest());
        var replacement = timeline.BeginInitialLoad(sessionId, forceReplacement: true);
        var liveTurn = initialTurn with
        {
            Items = [initialTurn.Items[0] with { TextContent = "live" }],
            UpdatedAtUtc = createdAt.AddSeconds(1),
            ContentRevision = 3,
        };
        Assert.Equal(
            CorePresentation.TranscriptLiveTurnResult.Buffered,
            timeline.ApplyLiveTurn(liveTurn));
        var staleSnapshotTurn = initialTurn with
        {
            Items = [initialTurn.Items[0] with { TextContent = "stale snapshot" }],
            UpdatedAtUtc = createdAt.AddSeconds(2),
            ContentRevision = 2,
        };

        Assert.True(timeline.TryCompleteInitialLoad(replacement, [staleSnapshotTurn]));

        Assert.Equal("live", Assert.Single(timeline.Projector.Rows).Content);
        Assert.Equal(0, timeline.PendingTurnCount);
    }

    [Fact]
    public async Task Timeline_RepeatedNewerPageWithoutCursorProgressStopsPaging()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var initialLoad = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(initialLoad, CreateTurns(sessionId, 0, 4)));
        Assert.True(timeline.DetachFromLatest());
        var newerTurn = CreateTurns(sessionId, 4, 1).Single();
        Assert.Equal(
            CorePresentation.TranscriptLiveTurnResult.Buffered,
            timeline.ApplyLiveTurn(newerTurn));
        var repeatedPage = new[] { newerTurn, newerTurn, newerTurn };

        Assert.True(await timeline.LoadNewerAsync((_, _, _, _, _) => Task.FromResult(
            new CorePresentation.TranscriptTurnPage(repeatedPage, true))));
        Assert.True(timeline.HasNewerRows);
        Assert.False(await timeline.LoadNewerAsync((_, _, _, _, _) => Task.FromResult(
            new CorePresentation.TranscriptTurnPage(repeatedPage, true))));
        Assert.True(timeline.HasNewerRows);
    }

    [Fact]
    public void Timeline_FailedInitialLoadProjectsBufferedLiveTurnsWhenFollowing()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var load = timeline.BeginInitialLoad(sessionId);
        var liveTurn = CreateTurns(sessionId, 0, 1).Single();
        Assert.Equal(CorePresentation.TranscriptLiveTurnResult.Buffered, timeline.ApplyLiveTurn(liveTurn));

        Assert.True(timeline.TryFailInitialLoad(load));

        Assert.Equal("message-0", Assert.Single(timeline.Projector.Rows).Content);
        Assert.Equal(0, timeline.PendingTurnCount);
        Assert.False(timeline.HasNewerRows);
    }

    [Fact]
    public void Timeline_FailedDetachedReloadAdvertisesBufferedNewerTurns()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var initialLoad = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(initialLoad, CreateTurns(sessionId, 0, 1)));
        Assert.True(timeline.DetachFromLatest());
        var reload = timeline.BeginInitialLoad(sessionId);
        var liveTurn = CreateTurns(sessionId, 1, 1).Single();
        Assert.Equal(CorePresentation.TranscriptLiveTurnResult.Buffered, timeline.ApplyLiveTurn(liveTurn));

        Assert.True(timeline.TryFailInitialLoad(reload));

        Assert.Equal("message-0", Assert.Single(timeline.Projector.Rows).Content);
        Assert.Equal(1, timeline.PendingTurnCount);
        Assert.True(timeline.HasNewerRows);
        Assert.False(timeline.IsFollowingLatest);
    }

    [Fact]
    public void Timeline_ActivityMutationPairsRowsChangingAndRowsChanged()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var load = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(load, CreateTurns(sessionId, 0, 1)));
        var changingCount = 0;
        var changedCount = 0;
        timeline.RowsChanging += _ => changingCount++;
        timeline.RowsChanged += () => changedCount++;

        timeline.ApplyActivity("Working", isReasoning: false, isVisible: true);

        Assert.Equal(1, changingCount);
        Assert.Equal(1, changedCount);
    }

    [Fact]
    public void Timeline_AuthoritativeResultOnlyRebuildReusesExpandedToolRow()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var callTurn = CreateToolTurn(sessionId, Guid.NewGuid(), itemCount: 1);
        var resultTurn = CreateToolResultTurn(sessionId, Guid.NewGuid(), "call-0");
        var laterMessage = CreateMessageTurn(
            sessionId,
            Guid.NewGuid(),
            "later",
            DateTimeOffset.UnixEpoch.AddSeconds(2),
            DateTimeOffset.UnixEpoch.AddSeconds(2));
        var initialLoad = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(initialLoad, [callTurn, resultTurn, laterMessage]));
        var row = timeline.Projector.Rows[0];
        timeline.SetRowExpanded(row, true);

        var replacement = timeline.BeginInitialLoad(sessionId, forceReplacement: true);
        Assert.True(timeline.TryCompleteInitialLoad(replacement, [resultTurn, laterMessage]));

        Assert.Equal(2, timeline.Projector.Rows.Count);
        Assert.Same(row, timeline.Projector.Rows[0]);
        Assert.Equal("later", timeline.Projector.Rows[1].Content);
        Assert.True(row.IsExpanded);
        Assert.Equal(resultTurn.TurnId, row.ResultTurnId);
    }

    [Fact]
    public void Timeline_AuthoritativeCallAndResultRebuildDoesNotReapplyCallToResultBackedRow()
    {
        var factory = new TestRowFactory();
        using var timeline = CreateTimeline(
            initialLimit: 4,
            pageSize: 2,
            visibleLimit: 4,
            factory: factory);
        var sessionId = Guid.NewGuid();
        var callTurn = CreateToolTurn(sessionId, Guid.NewGuid(), itemCount: 1);
        var resultTurn = CreateToolResultTurn(sessionId, Guid.NewGuid(), "call-0");
        var initialLoad = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(initialLoad, [callTurn, resultTurn]));
        var row = Assert.Single(timeline.Projector.Rows);
        timeline.SetRowExpanded(row, true);

        var replacement = timeline.BeginInitialLoad(sessionId, forceReplacement: true);
        Assert.True(timeline.TryCompleteInitialLoad(replacement, [callTurn, resultTurn]));

        Assert.Same(row, Assert.Single(timeline.Projector.Rows));
        Assert.True(row.IsExpanded);
        Assert.Equal(0, factory.UpdateToolCalls);
    }

    [Fact]
    public void Projector_AuthoritativeRebuildPreservesReusedRowsWithoutCollectionReset()
    {
        var rows = new CorePresentation.TranscriptObservableCollection<TestRow>();
        var factory = new TestRowFactory();
        var projector = new CorePresentation.TranscriptRowProjector<TestRow>(rows, factory, 8);
        var sessionId = Guid.NewGuid();
        var callTurn = CreateToolTurn(sessionId, Guid.NewGuid(), itemCount: 1);
        var resultTurn = CreateToolResultTurn(sessionId, Guid.NewGuid(), "call-0");
        projector.ReconcileAuthoritativeTurns([callTurn, resultTurn]);
        var row = Assert.Single(rows);
        var resetCount = 0;
        rows.CollectionChanged += (_, change) =>
        {
            if (change.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                resetCount++;
            }
        };

        projector.ReconcileAuthoritativeTurns([
            CreateMessageTurn(
                sessionId,
                Guid.NewGuid(),
                "older",
                DateTimeOffset.UnixEpoch.AddSeconds(-1),
                DateTimeOffset.UnixEpoch.AddSeconds(-1)),
            callTurn,
            resultTurn,
        ]);

        Assert.Same(row, rows[1]);
        Assert.Equal(0, resetCount);
    }

    [Fact]
    public void Timeline_ResultProjectedBeforeCallIsNotDowngradedByCallHeader()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var callTurn = CreateToolTurn(sessionId, Guid.NewGuid(), itemCount: 1);
        var resultTurn = CreateToolResultTurn(sessionId, Guid.NewGuid(), "call-0");
        var resultItem = resultTurn.Items.Single() with
        {
            ResultSummary = "file contents",
            ToolHeaderHint = "file contents",
            ToolHasDetails = true,
        };
        resultTurn = resultTurn with { Items = [resultItem] };
        var load = timeline.BeginInitialLoad(sessionId);

        Assert.True(timeline.TryCompleteInitialLoad(load, [resultTurn, callTurn]));

        var row = Assert.Single(timeline.Projector.Rows);
        Assert.Equal("Read File", row.Content);
        Assert.Equal(resultTurn.TurnId, row.ResultTurnId);
    }

    [Fact]
    public void Timeline_LiveEmptyToolResultPreservesCorrelatedCallDetails()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var callTurn = CreateToolTurn(sessionId, Guid.NewGuid(), itemCount: 1);
        callTurn = callTurn with
        {
            Items =
            [
                callTurn.Items.Single() with
                {
                    ArgumentsJson = "{\"path\":\"details.txt\"}",
                    ToolExecutionId = executionId,
                },
            ],
        };
        var resultTurn = CreateToolResultTurn(sessionId, Guid.NewGuid(), "call-0");
        resultTurn = resultTurn with
        {
            Items =
            [
                resultTurn.Items.Single() with
                {
                    TextContent = null,
                    ArgumentsJson = null,
                    ResultSummary = null,
                    StructuredPayloadJson = null,
                    SourcesJson = null,
                    PresentationPayloadJson = null,
                    ErrorCode = null,
                    BackendId = null,
                    ToolExecutionId = executionId,
                },
            ],
        };
        var load = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(load, []));

        Assert.Equal(CorePresentation.TranscriptLiveTurnResult.Applied, timeline.ApplyLiveTurn(callTurn));
        var row = Assert.Single(timeline.Projector.Rows);
        Assert.True(row.HasDetails);

        Assert.Equal(CorePresentation.TranscriptLiveTurnResult.Applied, timeline.ApplyLiveTurn(resultTurn));
        Assert.Same(row, Assert.Single(timeline.Projector.Rows));
        Assert.True(row.HasDetails);
        Assert.Equal(resultTurn.TurnId, row.ResultTurnId);
    }

    [Fact]
    public void Timeline_EmptyAuthoritativeReplacementAcceptsFirstDetachedLiveTurn()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var initialLoad = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(initialLoad, CreateTurns(sessionId, 0, 1)));
        Assert.True(timeline.DetachFromLatest());
        var replacement = timeline.BeginInitialLoad(sessionId, forceReplacement: true);
        Assert.True(timeline.TryCompleteInitialLoad(replacement, []));

        var result = timeline.ApplyLiveTurn(CreateTurns(sessionId, 1, 1).Single());

        Assert.Equal(CorePresentation.TranscriptLiveTurnResult.Applied, result);
        Assert.Equal("message-1", Assert.Single(timeline.Projector.Rows).Content);
        Assert.False(timeline.HasNewerRows);
        Assert.False(timeline.IsFollowingLatest);
    }

    [Fact]
    public void Timeline_FailedEmptyAuthoritativeReplacementSeedsCursorFromBufferedTurn()
    {
        using var timeline = CreateTimeline(initialLimit: 4, pageSize: 2, visibleLimit: 4);
        var sessionId = Guid.NewGuid();
        var initialLoad = timeline.BeginInitialLoad(sessionId);
        Assert.True(timeline.TryCompleteInitialLoad(initialLoad, CreateTurns(sessionId, 0, 1)));
        Assert.True(timeline.DetachFromLatest());
        var replacement = timeline.BeginInitialLoad(sessionId, forceReplacement: true);
        var liveTurn = CreateTurns(sessionId, 1, 1).Single();
        Assert.Equal(CorePresentation.TranscriptLiveTurnResult.Buffered, timeline.ApplyLiveTurn(liveTurn));

        Assert.True(timeline.TryFailInitialLoad(replacement));

        Assert.Equal("message-0", Assert.Single(timeline.Projector.Rows).Content);
        Assert.Equal(1, timeline.PendingTurnCount);
        Assert.True(timeline.HasNewerRows);
        Assert.False(timeline.IsFollowingLatest);
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
    public void RowProjector_PreservesMarkdownWhitespaceExactly()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        const string markdown = "    indented code\n\n## Heading\n\n- item\n";
        var turn = CreateMessageTurn(
            sessionId,
            turnId,
            markdown,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

        var projected = CorePresentation.TranscriptRowProjector<TestRow>.DescribeMessage(turn);

        Assert.Equal(markdown, projected.Content);
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

    [Fact]
    public void Composer_CommitsOnlyCorrelatedAuthoritativeUserTurn()
    {
        var composer = new CoreViews.AgentComposerState { Text = "expected message" };
        var sessionId = Guid.NewGuid();
        var submission = Assert.IsType<CoreViews.AgentComposerSubmission>(
            composer.TryBeginSubmission(sessionId));
        var unrelatedTurn = CreateMessageTurn(
            sessionId,
            Guid.NewGuid(),
            "expected message",
            submission.StartedAtUtc.AddSeconds(1),
            submission.StartedAtUtc.AddSeconds(1));

        Assert.Null(composer.CommitAuthoritativeUserTurn(unrelatedTurn));
        Assert.False(submission.IsCommitted);

        var matchingTurn = CreateMessageTurn(
            sessionId,
            submission.UserTurnId,
            "expected message",
            submission.StartedAtUtc.AddSeconds(1),
            submission.StartedAtUtc.AddSeconds(1));
        Assert.Same(submission, composer.CommitAuthoritativeUserTurn(matchingTurn));
        Assert.True(submission.IsCommitted);
    }

    [Fact]
    public void Composer_CorrelatedTurnMatchesCanonicalizedPersistedText()
    {
        var composer = new CoreViews.AgentComposerState { Text = "   " };
        var sessionId = Guid.NewGuid();
        var submission = Assert.IsType<CoreViews.AgentComposerSubmission>(
            composer.TryBeginSubmission(sessionId));
        var matchingTurn = CreateMessageTurn(
            sessionId,
            submission.UserTurnId,
            string.Empty,
            submission.StartedAtUtc.AddSeconds(1),
            submission.StartedAtUtc.AddSeconds(1));

        Assert.Same(submission, composer.CommitAuthoritativeUserTurn(matchingTurn));
    }

    [Fact]
    public void Composer_LegacySubmissionMatchesAuthoritativePayload()
    {
        var composer = new CoreViews.AgentComposerState { Text = "legacy message" };
        var sessionId = Guid.NewGuid();
        var submission = Assert.IsType<CoreViews.AgentComposerSubmission>(
            composer.TryBeginSubmission(
                sessionId,
                "legacy message",
                [],
                rollbackTurnId: null,
                existingTurnIds: new HashSet<Guid>(),
                useUserTurnCorrelation: false));
        var matchingTurn = CreateMessageTurn(
            sessionId,
            Guid.NewGuid(),
            "legacy message",
            submission.StartedAtUtc.AddSeconds(1),
            submission.StartedAtUtc.AddSeconds(1));

        Assert.Same(submission, composer.CommitAuthoritativeUserTurn(matchingTurn));
    }

    [Fact]
    public void Composer_LegacyWhitespaceMatchesCanonicalEmptyText()
    {
        var composer = new CoreViews.AgentComposerState { Text = "   " };
        var sessionId = Guid.NewGuid();
        var submission = Assert.IsType<CoreViews.AgentComposerSubmission>(
            composer.TryBeginSubmission(
                sessionId,
                "   ",
                [],
                rollbackTurnId: null,
                existingTurnIds: new HashSet<Guid>(),
                useUserTurnCorrelation: false));
        var matchingTurn = CreateMessageTurn(
            sessionId,
            Guid.NewGuid(),
            string.Empty,
            submission.StartedAtUtc.AddSeconds(1),
            submission.StartedAtUtc.AddSeconds(1));

        Assert.Same(submission, composer.CommitAuthoritativeUserTurn(matchingTurn));
    }

    [Fact]
    public void ComposerSubmission_CompletionHandshakeIsOrderIndependent()
    {
        var composer = new CoreViews.AgentComposerState { Text = "first" };
        var first = Assert.IsType<CoreViews.AgentComposerSubmission>(
            composer.TryBeginSubmission(Guid.NewGuid()));

        Assert.False(first.Commit());
        Assert.True(first.CompleteCommand());
        Assert.True(first.IsComplete);

        var second = Assert.IsType<CoreViews.AgentComposerSubmission>(
            new CoreViews.AgentComposerState { Text = "second" }
                .TryBeginSubmission(Guid.NewGuid()));

        Assert.False(second.CompleteCommand());
        Assert.True(second.Commit());
        Assert.True(second.IsComplete);

        var third = Assert.IsType<CoreViews.AgentComposerSubmission>(
            new CoreViews.AgentComposerState { Text = "third" }
                .TryBeginSubmission(Guid.NewGuid()));
        var commitObservedCompletion = false;
        var commandObservedCompletion = false;
        Parallel.Invoke(
            () => commitObservedCompletion = third.Commit(),
            () => commandObservedCompletion = third.CompleteCommand());

        Assert.True(commitObservedCompletion || commandObservedCompletion);
        Assert.True(third.IsComplete);
    }

    [Fact]
    public void Composer_ExplicitSubmissionSnapshotIgnoresLaterEdits()
    {
        var composer = new CoreViews.AgentComposerState { Text = "first draft" };
        var sessionId = Guid.NewGuid();
        var submission = Assert.IsType<CoreViews.AgentComposerSubmission>(
            composer.TryBeginSubmission(
                sessionId,
                "sent draft",
                [],
                rollbackTurnId: null,
                existingTurnIds: new HashSet<Guid>()));

        composer.Text = "edited while transcript loaded";

        Assert.Equal("sent draft", submission.Text);
        Assert.Equal("edited while transcript loaded", composer.Text);
    }

    private static CorePresentation.TranscriptTimelineState<TestRow> CreateTimeline(
        int initialLimit,
        int pageSize,
        int visibleLimit,
        TestRowFactory? factory = null)
    {
        var rows = new System.Collections.ObjectModel.ObservableCollection<TestRow>();
        var projector = new CorePresentation.TranscriptRowProjector<TestRow>(
            rows,
            factory ?? new TestRowFactory(),
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

    private static AgentTurnRecord CreateMessageTurn(
        Guid sessionId,
        Guid turnId,
        string content,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
        => new(
            turnId,
            sessionId,
            AgentMessageRole.User,
            AgentTurnKind.Message,
            [new AgentTurnItemRecord(
                Guid.NewGuid(),
                turnId,
                0,
                AgentTurnItemKind.Text,
                content,
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
            createdAt,
            updatedAt);

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

    private static AgentTurnRecord CreateToolTurn(
        Guid sessionId,
        Guid turnId,
        int itemCount)
    {
        var timestamp = DateTimeOffset.UnixEpoch;
        return new AgentTurnRecord(
            turnId,
            sessionId,
            AgentMessageRole.Assistant,
            AgentTurnKind.ToolCall,
            Enumerable.Range(0, itemCount)
                .Select(index => new AgentTurnItemRecord(
                    Guid.NewGuid(),
                    turnId,
                    index,
                    AgentTurnItemKind.ToolCall,
                    null,
                    $"call-{index}",
                    "read_file",
                    "{}",
                    null,
                    null,
                    null,
                    false,
                    false,
                    null,
                    null))
                .ToArray(),
            timestamp,
            timestamp)
        {
            RunId = sessionId,
            RunRevision = 1,
        };
    }

    private static AgentTurnRecord CreateToolResultTurn(
        Guid sessionId,
        Guid turnId,
        string callId)
    {
        var timestamp = DateTimeOffset.UnixEpoch.AddSeconds(1);
        return new AgentTurnRecord(
            turnId,
            sessionId,
            AgentMessageRole.Tool,
            AgentTurnKind.ToolResult,
            [new AgentTurnItemRecord(
                Guid.NewGuid(),
                turnId,
                0,
                AgentTurnItemKind.ToolResult,
                "result",
                callId,
                "read_file",
                "{}",
                "result",
                null,
                null,
                false,
                false,
                null,
                null)],
            timestamp,
            timestamp)
        {
            RunId = sessionId,
            RunRevision = 1,
        };
    }

    private sealed class TestRow(
        Guid rowId,
        object anchorKey,
        string content,
        Guid? resultTurnId = null)
        : CorePresentation.ITranscriptToolExpansionOwner
    {
        public Guid RowId { get; } = rowId;
        public object AnchorKey { get; } = anchorKey;
        public string Content { get; set; } = content;
        public Guid? ResultTurnId { get; set; } = resultTurnId;
        public bool IsExpanded { get; set; }
        public bool HasDetails { get; set; }

        public event Action? DetailVisualInvalidated;

        public void OnDetailVisualInvalidated() => InvalidateDetail();

        public void InvalidateDetail()
        {
            IsExpanded = false;
            DetailVisualInvalidated?.Invoke();
        }
    }

    private sealed class TestRowFactory : CorePresentation.ITranscriptRowFactory<TestRow>
    {
        public int UpdateToolCalls { get; private set; }

        public TestRow? CreateMessage(
            AgentTurnRecord turn,
            CorePresentation.TranscriptMessageProjection projection)
            => string.IsNullOrWhiteSpace(projection.Content)
                ? null
                : new(turn.TurnId, projection.AnchorKey, projection.Content);

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
                item.Kind == AgentTurnItemKind.ToolResult ? turn.TurnId : null)
            {
                HasDetails = projection.HasDetails,
            };

        public void ApplyToolResult(
            TestRow row,
            AgentTurnRecord turn,
            AgentTurnItemRecord item,
            CorePresentation.TranscriptToolProjection projection)
        {
            row.ResultTurnId = turn.TurnId;
            row.HasDetails = projection.HasDetails;
        }

        public void UpdateTool(
            TestRow row,
            AgentTurnRecord turn,
            AgentTurnItemRecord item,
            CorePresentation.TranscriptToolProjection projection)
        {
            UpdateToolCalls++;
            row.Content = projection.StatusText;
            row.HasDetails = projection.HasDetails;
        }

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
