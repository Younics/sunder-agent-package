extern alias AgentCore;

using System.Collections.Specialized;
using AgentCore::Sunder.Package.Agent.Contracts.Models;
using CorePresentation = AgentCore::Sunder.Package.Agent.Shared.PackageViews;
using Dispatch = AgentCore::Sunder.Package.Agent.Shared.Presentation;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class TranscriptPresentationStateTests
{
    [Theory]
    [InlineData("collapse", 1)]
    [InlineData("collapse", 2)]
    [InlineData("session", 1)]
    [InlineData("session", 2)]
    [InlineData("mode", 1)]
    [InlineData("mode", 2)]
    [InlineData("dispose", 1)]
    [InlineData("dispose", 2)]
    public async Task Compact_CancellationBetweenDispatchEnqueueAndExecutionIsNotACallbackFault(string stop, int publication)
    {
        var session = Guid.NewGuid();
        var (entry, headers) = CompactGroup(session, 2);
        var dispatcher = new PausedTranscriptDispatcher(publication);
        using var timeline = CreateTimeline(45, 30, 60, dispatcher: dispatcher);
        timeline.ConfigureCompact((_, _) => throw new InvalidOperationException(),
            (_, _) => Task.FromResult(CompactMembers(headers, false)));
        timeline.SetCompactMode(true);
        timeline.TryCompleteCompactInitialLoad(timeline.BeginInitialLoad(session), new([entry], false, false));
        var state = timeline.Projector.Rows.Single().GroupState!;
        state.IsExpanded = true;
        var token = await dispatcher.Queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = timeline.PendingCompactOperations;
        Assert.True(token.CanBeCanceled);
        switch (stop)
        {
            case "collapse": state.IsExpanded = false; break;
            case "session": timeline.BeginInitialLoad(Guid.NewGuid()); break;
            case "mode": timeline.SetCompactMode(false); break;
            case "dispose": timeline.Dispose(); break;
        }
        Assert.True(token.IsCancellationRequested);
        dispatcher.Release.TrySetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(state.IsLoading);
        Assert.Equal(publication - 1, dispatcher.MemberCallbacks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compact_TenThousandMembersPublishInRangesAndReleaseAllBackingOnCollapse(bool separateTurns)
    {
        var session = Guid.NewGuid();
        var (entry, headers) = CompactGroup(session, 10_000);
        if (separateTurns)
        {
            headers = headers.Select((header, index) =>
            {
                var id = Guid.NewGuid();
                return header with { TurnId = id, CreatedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(index),
                    Items = [header.Items[0] with { TurnId = id, SequenceNumber = 0 }] };
            }).ToArray();
            var first = CorePresentation.TranscriptCompactPayload.Cursor(headers[0], headers[0].Items[0]);
            var last = CorePresentation.TranscriptCompactPayload.Cursor(headers[^1], headers[^1].Items[0]);
            entry = entry with { EntryId = first.ItemId, TurnId = first.TurnId, CreatedAtUtc = first.CreatedAtUtc,
                Group = entry.Group! with { First = first, Last = last } };
        }
        var indices = headers.Select((header, index) => (header.Items[0].ItemId, index)).ToDictionary(item => item.ItemId, item => item.index);
        var rows = new CorePresentation.TranscriptObservableCollection<TestRow>();
        var factory = new TestRowFactory();
        using var timeline = CreateTimeline(45, 30, 60, factory, rows: rows);
        var reads = 0;
        timeline.ConfigureCompact((_, _) => throw new InvalidOperationException(), (request, _) =>
        {
            reads++;
            var start = request.After is { } after ? indices[after.ItemId] + 1 : 0;
            var chunk = headers.Skip(start).Take(request.Limit).ToArray();
            return Task.FromResult(CompactMembers(chunk, start + chunk.Length < headers.Length));
        });
        timeline.SetCompactMode(true);
        timeline.TryCompleteCompactInitialLoad(timeline.BeginInitialLoad(session), new([entry], false, false));
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        rows.CollectionChanged += (_, change) => notifications.Add(change);
        var group = rows.Single();
        group.GroupState!.IsExpanded = true;
        await timeline.PendingCompactOperations.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(100, reads);
        Assert.Equal(100, notifications.Count);
        Assert.All(notifications, change =>
        {
            Assert.Equal(NotifyCollectionChangedAction.Add, change.Action);
            Assert.Equal(100, change.NewItems!.Count);
        });
        Assert.Equal(10_001, rows.Count);
        Assert.Equal(separateTurns ? 10_000 : 1, timeline.Projector.TurnWindow.Count);
        Assert.Equal(separateTurns ? 10_000 : 100, timeline.Projector.CompactBackingTurnsRebuilt);
        var members = rows.Skip(1).ToArray();
        group.GroupState.IsExpanded = false;
        Assert.Same(group, Assert.Single(rows));
        Assert.Equal(0, timeline.Projector.TurnWindow.Count);
        Assert.Empty(timeline.Projector.ToolRows);
        Assert.All(members, member => Assert.True(member.Disposed));
        group.GroupState.IsExpanded = true;
        await timeline.PendingCompactOperations.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(10_001, rows.Count);
        Assert.All(rows.Skip(1), member => Assert.False(member.Disposed));
    }

    [Fact]
    public async Task Compact_EpochInsertionKeepsCanonicalOrderAndRetainsExistingInvocationRows()
    {
        var session = Guid.NewGuid();
        var (entry, allHeaders) = CompactGroup(session, 3);
        entry = entry with { Group = entry.Group! with { Tools = [new("read_file", 2)], RunningCount = 2 } };
        var replacement = entry with { Group = entry.Group! with { TranscriptEpoch = 2, Revision = 2,
            Tools = [new("read_file", 3)], RunningCount = 3 } };
        var reads = 0;
        using var dispatcher = new SerializedTranscriptDispatcher();
        using var timeline = CreateTimeline(45, 30, 60, dispatcher: dispatcher,
            rows: new CorePresentation.TranscriptObservableCollection<TestRow>());
        timeline.ConfigureCompact((_, _) => Task.FromResult(new AgentCompactTranscriptPage([replacement], false, false)),
            (_, _) => Task.FromResult(CompactMembers(++reads == 1 ? [allHeaders[0], allHeaders[2]] : allHeaders, false)));
        timeline.SetCompactMode(true);
        timeline.TryCompleteCompactInitialLoad(timeline.BeginInitialLoad(session), new([entry], false, false));
        timeline.Projector.Rows[0].GroupState!.IsExpanded = true;
        await timeline.PendingCompactOperations.WaitAsync(TimeSpan.FromSeconds(5));
        var first = timeline.Projector.Rows[1];
        var last = timeline.Projector.Rows[2];
        timeline.ReconcileCompactSnapshot(new([replacement], false, false));
        await timeline.PendingCompactOperations.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(allHeaders.Select(header => (object)CorePresentation.TranscriptRowAnchorKey.Tool(header, header.Items[0])),
            timeline.Projector.Rows.Skip(1).Select(row => row.AnchorKey));
        Assert.Same(first, timeline.Projector.Rows[1]);
        Assert.Same(last, timeline.Projector.Rows[3]);
        Assert.False(first.Disposed);
        Assert.False(last.Disposed);
        Assert.Equal(3, timeline.Projector.TurnWindow.OrderedTurns().Single().Items.Count);
    }

    [Fact]
    public async Task Compact_ModeReplacementDropsCompactBackingAndReentryStartsWithoutDetailedResidue()
    {
        var session = Guid.NewGuid();
        var (entry, headers) = CompactGroup(session, 3);
        using var timeline = CreateTimeline(45, 30, 60);
        timeline.ConfigureCompact((_, _) => throw new InvalidOperationException(),
            (_, _) => Task.FromResult(CompactMembers(headers, false)));
        timeline.SetCompactMode(true);
        timeline.TryCompleteCompactInitialLoad(timeline.BeginInitialLoad(session), new([entry], false, false));
        timeline.Projector.Rows[0].GroupState!.IsExpanded = true;
        await timeline.PendingCompactOperations.WaitAsync(TimeSpan.FromSeconds(5));
        var retired = CaptureTranscriptRowReferences(timeline.Projector.Rows.Skip(1));
        timeline.SetCompactMode(false);
        var message = CreateTurns(session, 10, 1).Single();
        timeline.TryCompleteInitialLoad(timeline.BeginInitialLoad(session, forceReplacement: true), [message]);
        Assert.Empty(timeline.Projector.ToolRows);
        Assert.Equal(message.TurnId, timeline.Projector.TurnWindow.OrderedTurns().Single().TurnId);
        for (var attempt = 0; attempt < 5 && retired.Any(reference => reference.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.All(retired, reference => Assert.False(reference.IsAlive));
        timeline.SetCompactMode(true);
        timeline.TryCompleteCompactInitialLoad(timeline.BeginInitialLoad(session, forceReplacement: true), new([entry], false, false));
        Assert.Single(timeline.Projector.Rows);
        Assert.Equal(0, timeline.Projector.TurnWindow.Count);
        Assert.Empty(timeline.Projector.ToolRows);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference[] CaptureTranscriptRowReferences(IEnumerable<TestRow> rows)
        => rows.Select(row => new WeakReference(row)).ToArray();

    private sealed class PausedTranscriptDispatcher(int publication) : Dispatch.IPresentationDispatcher
    {
        private int _calls;
        public TaskCompletionSource<CancellationToken> Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MemberCallbacks { get; private set; }
        public bool CheckAccess() => false;
        public Task InvokeAsync(Action action) => InvokeAsync(action, default);
        public async Task InvokeAsync(Action action, CancellationToken token)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == publication)
            {
                Queued.TrySetResult(token);
                await Release.Task.ConfigureAwait(false);
            }
            Dispatch.PresentationCancellation.ThrowIfStopped(token, default);
            Dispatch.PresentationDispatcher.InvokeCallback(action);
            if (token.CanBeCanceled) MemberCallbacks++;
        }
    }

    // Like the native UI dispatcher, a refresh callback and a member-publication
    // callback cannot run concurrently. Pure-state tests have no Avalonia loop.
    private sealed class SerializedTranscriptDispatcher : Dispatch.IPresentationDispatcher, IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        public bool CheckAccess() => false;
        public Task InvokeAsync(Action action) => InvokeAsync(action, default);
        public async Task InvokeAsync(Action action, CancellationToken token)
        {
            await _gate.WaitAsync(token);
            try
            {
                Dispatch.PresentationCancellation.ThrowIfStopped(token, default);
                Dispatch.PresentationDispatcher.InvokeCallback(action);
            }
            finally { _gate.Release(); }
        }
        public void Dispose() => _gate.Dispose();
    }
}
