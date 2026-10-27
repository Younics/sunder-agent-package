extern alias AgentCore;

using AgentCore::Sunder.Package.Agent.Contracts.Models;
using CorePresentation = AgentCore::Sunder.Package.Agent.Shared.PackageViews;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class TranscriptPresentationStateTests
{
    [Fact]
    public async Task Compact_CountsCompleteGroupsRatherThanExpandedMembersAndProtectsTheirParentWhenPaging()
    {
        var session = Guid.NewGuid();
        var (entry, headers) = CompactGroup(session, 1003, 100);
        var factory = new TestRowFactory();
        using var timeline = CreateTimeline(45, 30, 60, factory);
        var requests = new List<AgentTranscriptActivityMembersRequest>();
        AgentCompactTranscriptRequest? outerRequest = null;
        var preceding = CreateTurns(session, 26, 30).Select(CompactMessage).ToArray();
        timeline.ConfigureCompact((request, _) =>
        {
            outerRequest = request;
            return Task.FromResult(new AgentCompactTranscriptPage(preceding, true, true));
        }, (request, _) =>
        {
            requests.Add(request);
            var start = request.After?.SequenceNumber + 1 ?? 0;
            var chunk = headers.Skip(start).Take(request.Limit).ToArray();
            return Task.FromResult(CompactMembers(chunk, start + chunk.Length < headers.Length));
        });
        timeline.SetCompactMode(true);
        var ticket = timeline.BeginInitialLoad(session);
        Assert.True(timeline.TryCompleteCompactInitialLoad(ticket,
            new([.. CreateTurns(session, 56, 44).Select(CompactMessage), entry], true, false)));
        Assert.Equal(45, timeline.Projector.Rows.Count);
        Assert.Equal(0, factory.CreatedTools);
        Assert.Empty(requests);
        var group = timeline.Projector.Rows.Single(row => row.GroupState is not null);
        Assert.Contains("1003", group.GroupState!.Summary);
        group.GroupState.IsExpanded = true;
        await timeline.PendingCompactOperations.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1048, timeline.Projector.Rows.Count);
        Assert.Equal(1003, factory.CreatedTools);
        Assert.Equal(11, requests.Count);
        Assert.All(requests, request => Assert.Equal(entry.Group!.Last, request.Through));
        var member = timeline.Projector.Rows[^7];
        Assert.True(await timeline.LoadOlderAsync((_, _, _, _, _) => throw new InvalidOperationException("Detailed path"), member.AnchorKey));
        Assert.NotNull(outerRequest);
        Assert.Equal(30, outerRequest.Limit);
        Assert.Equal(AgentCompactTranscriptDirection.Before, outerRequest.Direction);
        Assert.Equal(1063, timeline.Projector.Rows.Count); // 60 outer entries plus all 1,003 members.
        Assert.Contains(member, timeline.Projector.Rows);
        Assert.False(member.Disposed);
        Assert.False(timeline.HasNewerRows); // Protecting the last group trims the older edge instead.
        Assert.True(timeline.HasOlderRows);
        var members = timeline.Projector.Rows.Where(row => row.GroupState is null && row.HasDetails).ToArray();
        group.GroupState.IsExpanded = false;
        Assert.Equal(60, timeline.Projector.Rows.Count);
        Assert.All(members, row => Assert.True(row.Disposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compact_CollapseOrSessionReplacementCancelsMembersAndRejectsLateSuccess(bool replaceSession)
    {
        var session = Guid.NewGuid();
        var (entry, headers) = CompactGroup(session, 2);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AgentTranscriptActivityMembersPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new TestRowFactory();
        using var timeline = CreateTimeline(45, 30, 60, factory);
        timeline.ConfigureCompact((_, _) => throw new InvalidOperationException(), (_, token) =>
        {
            started.TrySetResult(token);
            return release.Task; // Deliberately ignores cancellation.
        });
        timeline.SetCompactMode(true);
        timeline.TryCompleteCompactInitialLoad(timeline.BeginInitialLoad(session), new([entry], false, false));
        var state = timeline.Projector.Rows.Single().GroupState!;
        state.IsExpanded = true;
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = timeline.PendingCompactOperations;
        if (replaceSession)
            timeline.TryCompleteCompactInitialLoad(timeline.BeginInitialLoad(Guid.NewGuid()), new([], false, false));
        else state.IsExpanded = false;
        Assert.True(token.IsCancellationRequested);
        release.SetResult(CompactMembers(headers, false));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, factory.CreatedTools);
        Assert.DoesNotContain(timeline.Projector.Rows, row => row.HasDetails);
        Assert.False(state.IsLoading);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("run")]
    [InlineData("payload")]
    [InlineData("continuation")]
    [InlineData("incomplete")]
    [InlineData("unordered")]
    [InlineData("owner")]
    [InlineData("skipped-first")]
    [InlineData("past-end")]
    public async Task Compact_InvalidMemberChunksFailWithoutPublishingHeaders(string invalid)
    {
        var session = Guid.NewGuid();
        var (entry, headers) = CompactGroup(session, 2);
        var page = CompactMembers(headers, false);
        page = invalid switch
        {
            "session" => page with { Headers = [headers[0] with { SessionId = Guid.NewGuid() }, headers[1]] },
            "run" => page with { Headers = [headers[0] with { RunRevision = 2 }, headers[1]] },
            "payload" => page with { Headers = [headers[0] with { Items = [headers[0].Items[0] with { IsToolHeaderProjection = false }] }, headers[1]] },
            "continuation" => page with { Continuation = entry.Group!.First },
            "incomplete" => CompactMembers([headers[0]], false),
            "unordered" => page with { Headers = headers.Reverse().ToArray() },
            "owner" => page with { Headers = [headers[0] with { Items = [headers[0].Items[0] with { TurnId = Guid.NewGuid() }] }, headers[1]] },
            "skipped-first" => CompactMembers([headers[1]], false),
            "past-end" => page with { HasMore = true },
            _ => throw new InvalidOperationException(),
        };
        var factory = new TestRowFactory();
        using var timeline = CreateTimeline(45, 30, 60, factory);
        timeline.ConfigureCompact((_, _) => throw new InvalidOperationException(), (_, _) => Task.FromResult(page));
        timeline.SetCompactMode(true);
        timeline.TryCompleteCompactInitialLoad(timeline.BeginInitialLoad(session), new([entry], false, false));
        var state = timeline.Projector.Rows.Single().GroupState!;
        state.IsExpanded = true;
        await timeline.PendingCompactOperations.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(state.IsLoading);
        Assert.NotEmpty(state.FailureText);
        Assert.Single(timeline.Projector.Rows);
        Assert.Equal(0, factory.CreatedTools);
    }

    [Fact]
    public async Task Compact_LateHeaderRefreshCannotOverwriteNewerLiveResultAndKeepsItsHistoryIdentity()
    {
        var session = Guid.NewGuid();
        var (entry, headers) = CompactGroup(session, 1);
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshHeaders = new TaskCompletionSource<AgentTranscriptActivityMembersPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        using var timeline = CreateTimeline(45, 30, 60);
        var summary = entry.Group! with { Revision = 2 };
        timeline.ConfigureCompact((_, _) => Task.FromResult(new AgentCompactTranscriptPage([entry with { Group = summary }], false, false)), (_, _) =>
        {
            if (Interlocked.Increment(ref reads) == 1) return Task.FromResult(CompactMembers(headers, false));
            refreshStarted.TrySetResult();
            return refreshHeaders.Task;
        });
        timeline.SetCompactMode(true);
        timeline.TryCompleteCompactInitialLoad(timeline.BeginInitialLoad(session), new([entry], false, false));
        timeline.Projector.Rows.Single().GroupState!.IsExpanded = true;
        await timeline.PendingCompactOperations.WaitAsync(TimeSpan.FromSeconds(5));
        var result = CreateToolResultTurn(session, Guid.NewGuid(), headers[0].Items[0].CallId!) with
        {
            Items = [headers[0].Items[0] with { TurnId = Guid.NewGuid(), ItemId = Guid.NewGuid(), Kind = AgentTurnItemKind.ToolResult,
                ToolDetailRevision = 3, ToolExecutionStatus = AgentToolExecutionStatus.Failed }],
        };
        result = result with { Items = [result.Items[0] with { TurnId = result.TurnId }] };
        timeline.ApplyLiveTurn(result);
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        refreshHeaders.SetResult(CompactMembers([headers[0] with { Items = [headers[0].Items[0] with { ToolDetailRevision = 2 }] }], false));
        await timeline.PendingCompactOperations.WaitAsync(TimeSpan.FromSeconds(5));
        var member = timeline.Projector.Rows.Single(row => row.GroupState is null);
        Assert.Equal("Failed", member.Status);
        Assert.Equal(result.TurnId, member.ResultTurnId);
        Assert.Contains(timeline.Projector.TurnWindow.OrderedTurns(), turn => turn.TurnId == result.TurnId);
    }

    [Fact]
    public async Task Compact_TargetedResultDoesNotRereadMembersButAuthoritativeSnapshotRevalidatesThem()
    {
        var session = Guid.NewGuid();
        var (entry, headers) = CompactGroup(session, 2);
        var reads = 0;
        var outerReads = 0;
        var group = entry.Group!;
        var refreshed = headers.Select((header, index) => header with
        {
            Items = [header.Items[0] with { ToolDetailRevision = 3,
                ToolExecutionStatus = index == 0 ? AgentToolExecutionStatus.Failed : AgentToolExecutionStatus.Completed }],
        }).ToArray();
        using var timeline = CreateTimeline(45, 30, 60);
        timeline.ConfigureCompact((_, _) =>
        {
            outerReads++;
            return Task.FromResult(new AgentCompactTranscriptPage([entry with { Group = group }], false, false));
        }, (_, _) => Task.FromResult(CompactMembers(++reads == 1 ? headers : refreshed, false)));
        timeline.SetCompactMode(true);
        timeline.TryCompleteCompactInitialLoad(timeline.BeginInitialLoad(session), new([entry], false, false));
        timeline.Projector.Rows.Single().GroupState!.IsExpanded = true;
        await timeline.PendingCompactOperations.WaitAsync(TimeSpan.FromSeconds(5));
        var retained = timeline.Projector.Rows[1];
        var result = CreateToolResultTurn(session, Guid.NewGuid(), headers[0].Items[0].CallId!) with
        {
            Items = [headers[0].Items[0] with { Kind = AgentTurnItemKind.ToolResult, ItemId = Guid.NewGuid(),
                ToolDetailRevision = 2, ToolExecutionStatus = AgentToolExecutionStatus.Failed }],
        };
        result = result with { Items = [result.Items[0] with { TurnId = result.TurnId }] };
        group = group with { Revision = 2, RunningCount = 1, FailedCount = 1 };
        timeline.ApplyLiveTurn(result, group);
        await timeline.PendingCompactOperations.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, reads);
        Assert.Equal(0, outerReads);
        Assert.Equal("Failed", retained.Status);
        group = group with { Revision = 3, RunningCount = 0, CompletedCount = 1 };
        timeline.ReconcileCompactSnapshot(new([entry with { Group = group }], false, false));
        await timeline.PendingCompactOperations.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, reads);
        Assert.Equal(1, outerReads);
        Assert.Same(retained, timeline.Projector.Rows[1]);
        Assert.Equal("Completed", timeline.Projector.Rows[2].Status);
    }

    [Fact]
    public async Task Compact_RefreshRacingWithPageCannotReplaceTheNewlyLoadedReaderWindow()
    {
        var session = Guid.NewGuid();
        var turns = CreateTurns(session, 0, 90);
        var staleRefresh = new TaskCompletionSource<AgentCompactTranscriptPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshRequests = new List<AgentCompactTranscriptRequest>();
        using var timeline = CreateTimeline(45, 30, 60);
        timeline.ConfigureCompact((request, _) =>
        {
            if (request.Direction == AgentCompactTranscriptDirection.Before)
                return Task.FromResult(new AgentCompactTranscriptPage(turns[15..45].Select(CompactMessage).ToArray(), true, true));
            refreshRequests.Add(request);
            if (refreshRequests.Count == 1) { refreshStarted.TrySetResult(); return staleRefresh.Task; }
            return Task.FromResult(new AgentCompactTranscriptPage(turns[15..75].Select(CompactMessage).ToArray(), true, true));
        }, (_, _) => throw new InvalidOperationException());
        timeline.SetCompactMode(true);
        timeline.TryCompleteCompactInitialLoad(timeline.BeginInitialLoad(session), new(turns[45..].Select(CompactMessage).ToArray(), true, false));
        timeline.ReconcileCompactSnapshot(new(turns[46..].Select(CompactMessage).ToArray(), true, false));
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await timeline.LoadOlderAsync((_, _, _, _, _) => throw new InvalidOperationException(), timeline.Projector.Rows[0].AnchorKey));
        staleRefresh.SetResult(new(turns[45..].Select(CompactMessage).ToArray(), true, false));
        await timeline.PendingCompactOperations.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(turns[15..75].Select(turn => turn.TurnId), timeline.Projector.Rows.Select(row => row.RowId));
        Assert.Equal(2, refreshRequests.Count);
        Assert.Equal(AgentCompactTranscriptDirection.Window, refreshRequests[1].Direction);
        Assert.True(timeline.HasOlderRows);
        Assert.True(timeline.HasNewerRows);
        Assert.False(timeline.IsFollowingLatest);
    }

    [Fact]
    public async Task Compact_ModeAnchorMapsAVisibleInvocationToItsSummaryWithoutLoadingMembers()
    {
        var session = Guid.NewGuid();
        var (entry, headers) = CompactGroup(session, 3);
        using var timeline = CreateTimeline(45, 30, 60);
        var memberReads = 0;
        timeline.ConfigureCompact((_, _) => Task.FromResult(new AgentCompactTranscriptPage([entry], true, true, entry.EntryId)), (_, _) =>
        {
            memberReads++;
            return Task.FromResult(CompactMembers(headers, false));
        });
        var detailed = headers[0] with { Items = headers.SelectMany(header => header.Items).ToArray() };
        timeline.TryCompleteInitialLoad(timeline.BeginInitialLoad(session), [detailed]);
        timeline.DetachFromLatest();
        var sourceAnchor = timeline.Projector.Rows[1].AnchorKey;
        timeline.SetViewportAnchor(new(sourceAnchor, 17, 500, 28));
        var before = timeline.CaptureModeAnchor()!;
        timeline.SetCompactMode(true);
        Assert.True(await timeline.LoadCompactAroundAsync(session, before.TurnId, before.ItemId, default,
            revealMembers: false, previousModeAnchor: before));
        var summary = Assert.Single(timeline.Projector.Rows);
        Assert.Equal(summary.AnchorKey, timeline.ResolveCompactVisibleAnchor(sourceAnchor));
        Assert.Equal(summary.AnchorKey, timeline.ViewportAnchor?.AnchorKey);
        Assert.Equal(17, timeline.ViewportAnchor?.OffsetY);
        Assert.Equal(0, memberReads);
        Assert.False(summary.GroupState!.IsExpanded);
        var collapsed = timeline.CaptureModeAnchor()!;
        Assert.Equal(entry.Group!.First.ItemId, collapsed.ItemId);
        timeline.SetCompactMode(false);
        var ticket = timeline.BeginAnchoredInitialLoad(session);
        var detailedAnchor = CorePresentation.TranscriptRowAnchorKey.Tool(headers[0], headers[0].Items[0]);
        timeline.RowsChanged += () => Assert.Equal(17, timeline.ViewportAnchor?.OffsetY);
        timeline.RestoreModeAnchor(collapsed, detailedAnchor, notify: false);
        timeline.TryCompleteAnchoredInitialLoad(ticket, [detailed], true, true, detailedAnchor,
            collapsed.Viewport!.Value with { AnchorKey = detailedAnchor });
        Assert.Equal(detailedAnchor, timeline.ResolveCompactVisibleAnchor(summary.AnchorKey));
        Assert.Equal(17, timeline.ViewportAnchor?.OffsetY);
        Assert.True(summary.Disposed);
    }

    private static AgentCompactTranscriptEntry CompactMessage(AgentTurnRecord turn)
        => new(turn.TurnId, turn.CreatedAtUtc, turn.TurnId, turn, null);

    [Fact]
    public async Task Compact_NewEpochKeepsVisibleMembersUntilRevalidationAndRemovesOnlyDeletedInvocations()
    {
        var session = Guid.NewGuid();
        var (entry, headers) = CompactGroup(session, 2);
        var release = new TaskCompletionSource<AgentTranscriptActivityMembersPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var replacement = entry with { Group = entry.Group! with { TranscriptEpoch = 2, Last = entry.Group.First, Tools = [new("read_file", 1)], RunningCount = 1 } };
        using var timeline = CreateTimeline(45, 30, 60);
        timeline.ConfigureCompact((_, _) => Task.FromResult(new AgentCompactTranscriptPage([replacement], false, false)), (_, _) =>
        {
            if (Interlocked.Increment(ref reads) == 1) return Task.FromResult(CompactMembers(headers, false));
            started.TrySetResult();
            return release.Task;
        });
        timeline.SetCompactMode(true);
        timeline.TryCompleteCompactInitialLoad(timeline.BeginInitialLoad(session), new([entry], false, false));
        timeline.Projector.Rows.Single().GroupState!.IsExpanded = true;
        await timeline.PendingCompactOperations.WaitAsync(TimeSpan.FromSeconds(5));
        var retained = timeline.Projector.Rows[1];
        var removed = timeline.Projector.Rows[2];
        timeline.ReconcileCompactSnapshot(new([replacement], false, false));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(retained, timeline.Projector.Rows);
        Assert.Contains(removed, timeline.Projector.Rows);
        Assert.False(retained.Disposed);
        release.SetResult(CompactMembers([headers[0]], false));
        await timeline.PendingCompactOperations.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(retained, timeline.Projector.Rows[1]);
        Assert.False(retained.Disposed);
        Assert.True(removed.Disposed);
        Assert.DoesNotContain(removed, timeline.Projector.Rows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compact_LateOuterPageCannotPublishAfterSessionOrModeReplacement(bool changeMode)
    {
        var session = Guid.NewGuid();
        var turns = CreateTurns(session, 0, 5);
        var release = new TaskCompletionSource<AgentCompactTranscriptPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeline = CreateTimeline(3, 2, 4);
        timeline.ConfigureCompact((_, _) => release.Task, (_, _) => throw new InvalidOperationException());
        timeline.SetCompactMode(true);
        timeline.TryCompleteCompactInitialLoad(timeline.BeginInitialLoad(session), new(turns[2..].Select(CompactMessage).ToArray(), true, false));
        var pending = timeline.LoadOlderAsync((_, _, _, _, _) => throw new InvalidOperationException());
        Assert.True(timeline.IsLoadingOlder);
        var replacement = CreateTurns(changeMode ? session : Guid.NewGuid(), 20, 1)[0];
        if (changeMode)
        {
            timeline.SetCompactMode(false);
            timeline.TryCompleteInitialLoad(timeline.BeginInitialLoad(session, forceReplacement: true), [replacement]);
        }
        else timeline.TryCompleteCompactInitialLoad(timeline.BeginInitialLoad(replacement.SessionId), new([CompactMessage(replacement)], false, false));
        release.SetResult(new(turns[..2].Select(CompactMessage).ToArray(), false, true));
        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(replacement.TurnId, Assert.Single(timeline.Projector.Rows).RowId);
        Assert.False(timeline.IsLoadingOlder);
    }

    [Fact]
    public async Task Compact_MetadataOnlySnapshotRetainsTheLargerWindowWithoutPublishingUnchangedLayout()
    {
        var session = Guid.NewGuid();
        var entries = CreateTurns(session, 0, 60).Select(CompactMessage).ToArray();
        using var timeline = CreateTimeline(45, 30, 60);
        timeline.ConfigureCompact((request, _) =>
        {
            Assert.Equal(60, request.Limit);
            return Task.FromResult(new AgentCompactTranscriptPage(entries, true, false));
        }, (_, _) => throw new InvalidOperationException());
        timeline.SetCompactMode(true);
        timeline.TryCompleteCompactInitialLoad(timeline.BeginInitialLoad(session), new(entries, true, false));
        var changed = 0;
        timeline.RowsChanged += () => changed++;
        timeline.RowsChanging += _ => changed++;
        timeline.ReconcileCompactSnapshot(new(entries[^45..], true, false));
        await timeline.PendingCompactOperations.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(60, timeline.Projector.Rows.Count);
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task Compact_AuthoritativeMessagePublishesImmediatelyWhileSummaryRefreshIsPending()
    {
        var session = Guid.NewGuid();
        var messages = CreateTurns(session, 0, 61);
        var release = new TaskCompletionSource<AgentCompactTranscriptPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeline = CreateTimeline(45, 30, 60);
        timeline.ConfigureCompact((_, _) => release.Task, (_, _) => throw new InvalidOperationException());
        timeline.SetCompactMode(true);
        timeline.TryCompleteCompactInitialLoad(timeline.BeginInitialLoad(session), new(messages[..60].Select(CompactMessage).ToArray(), false, false));
        Assert.Equal(CorePresentation.TranscriptLiveTurnResult.Applied, timeline.ApplyAuthoritativeTurn(messages[^1]));
        Assert.Equal(60, timeline.Projector.Rows.Count);
        Assert.Equal(messages[^1].TurnId, timeline.Projector.Rows[^1].RowId);
        Assert.True(timeline.HasOlderRows);
        release.SetResult(new(messages[1..].Select(CompactMessage).ToArray(), true, false));
        await timeline.PendingCompactOperations.WaitAsync(TimeSpan.FromSeconds(5));
        timeline.DetachFromLatest();
        var next = CreateTurns(session, 61, 1)[0];
        timeline.ApplyLiveTurn(next);
        Assert.True(timeline.HasNewerRows);
        Assert.DoesNotContain(timeline.Projector.Rows, row => row.RowId == next.TurnId);
    }

    private static (AgentCompactTranscriptEntry Entry, AgentTurnRecord[] Headers) CompactGroup(Guid session, int count, int second = 0)
    {
        var turn = CreateToolTurn(session, Guid.NewGuid(), count) with { CreatedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(second) };
        var headers = turn.Items.Select(item => turn with { Items = [item with
        {
            ArgumentsJson = null, IsToolHeaderProjection = true, ToolHasDetails = true,
            ToolDetailRevision = 1, ToolExecutionId = Guid.NewGuid(), ToolExecutionStatus = AgentToolExecutionStatus.Started,
        }] }).ToArray();
        var first = CorePresentation.TranscriptCompactPayload.Cursor(headers[0], headers[0].Items[0]);
        var last = CorePresentation.TranscriptCompactPayload.Cursor(headers[^1], headers[^1].Items[0]);
        return (new(first.ItemId, first.CreatedAtUtc, first.TurnId, null,
            new(session, first, last, session, 1, 1, 1, [new("read_file", count)], count, 0, 0)), headers);
    }

    private static AgentTranscriptActivityMembersPage CompactMembers(IReadOnlyList<AgentTurnRecord> headers, bool more)
        => new(headers, more, CorePresentation.TranscriptCompactPayload.Cursor(headers[^1], headers[^1].Items[0]));
}
