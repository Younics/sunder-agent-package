using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentCompactTranscriptTests
{
    [Theory]
    [InlineData(1003)]
    [InlineData(10_000)]
    public void LargeGroupOccupiesOneSlotAndMembersLoadAsFiniteHeaderOnlyChunks(int count)
    {
        using var fixture = new Fixture();
        var leading = fixture.Message("Before tools");
        var calls = Enumerable.Range(0, count).Select(index => fixture.Call(index % 2 == 0 ? "read" : "shell")).ToArray();
        foreach (var call in calls) fixture.Result(call, failed: call == calls[17]);
        var trailing = fixture.Message("After tools");
        fixture.Commit();

        var page = fixture.Store.ReadCompactTranscript(new(fixture.Session, AgentCompactTranscriptDirection.Recent, 3));
        Assert.Equal(3, page.Entries.Count);
        Assert.Equal(leading, page.Entries[0].TurnId);
        Assert.Equal(trailing, page.Entries[2].TurnId);
        Assert.False(page.HasOlder);
        Assert.False(page.HasNewer);
        var group = Assert.IsType<AgentTranscriptActivityGroup>(page.Entries[1].Group);
        Assert.Equal(count, group.Tools.Sum(tool => tool.Count));
        Assert.Equal(1, group.FailedCount);
        Assert.Equal(0, group.RunningCount);
        Assert.DoesNotContain("argument-canary", JsonSerializer.Serialize(page));
        Assert.DoesNotContain("output-canary", JsonSerializer.Serialize(page));

        var ids = new List<Guid>();
        var readTimer = Stopwatch.StartNew();
        AgentTranscriptInvocationCursor? after = null;
        do
        {
            var members = fixture.Store.ReadActivityMembers(new(fixture.Session, group.First.ItemId, group.TranscriptEpoch, group.Last, after, 113));
            Assert.InRange(members.Headers.Count, 1, 113);
            foreach (var header in members.Headers)
            {
                var item = Assert.Single(header.Items);
                ids.Add(item.ItemId);
                Assert.True(item.IsToolHeaderProjection);
                Assert.Null(item.ArgumentsJson);
                Assert.Null(item.TextContent);
                Assert.Null(item.StructuredPayloadJson);
                Assert.Equal(AgentTurnKind.ToolCall, header.Kind);
            }
            after = members.HasMore ? members.Continuation : null;
        } while (after is not null);
        Console.WriteLine($"compact-storage members={count} member-read-ms={readTimer.ElapsedMilliseconds}");
        Assert.Equal(calls.Select(call => call.Item), ids);
        var detailed = fixture.Store.ListRecentTranscriptHeaders(fixture.Session, 10);
        Assert.Equal(calls.TakeLast(9).Select(call => call.Turn).Append(trailing), detailed.Select(turn => turn.TurnId));
        Assert.All(detailed.Take(9), turn => Assert.Equal(AgentToolExecutionStatus.Completed, Assert.Single(turn.Items).ToolExecutionStatus));
        var detail = fixture.Store.GetTranscriptToolDetail(new(fixture.Session, calls[17].Execution, ItemId: calls[17].Item));
        Assert.NotNull(detail);
        Assert.Equal("output-canary", detail.OutputText);
    }

    [Fact]
    public void BidirectionalPagesAndAroundResultUseCompleteCanonicalGroups()
    {
        using var fixture = new Fixture();
        var firstMessage = fixture.Message("first");
        var first = fixture.Call("read");
        fixture.Message("between calls and delayed result");
        var second = fixture.Call("shell");
        var result = fixture.Result(first);
        fixture.Result(second);
        var finalMessage = fixture.Message("last");
        fixture.Commit();

        var recent = fixture.Store.ReadCompactTranscript(new(fixture.Session, AgentCompactTranscriptDirection.Recent, 2));
        Assert.Equal(2, recent.Entries.Count);
        Assert.True(recent.HasOlder);
        var group = Assert.IsType<AgentTranscriptActivityGroup>(recent.Entries[0].Group);
        Assert.Equal(second.Item, group.First.ItemId);
        Assert.Single(group.Tools);
        Assert.Equal(1, group.Tools[0].Count);
        Assert.Equal(finalMessage, recent.Entries[1].TurnId);
        var edge = recent.Entries[0];
        var older = fixture.Store.ReadCompactTranscript(new(fixture.Session, AgentCompactTranscriptDirection.Before, 2, edge.CreatedAtUtc, edge.TurnId));
        Assert.Equal(first.Item, older.Entries[0].EntryId);
        Assert.Equal(1, older.Entries[0].Group!.Tools[0].Count);
        Assert.True(older.HasOlder);
        Assert.True(older.HasNewer);
        var newer = fixture.Store.ReadCompactTranscript(new(fixture.Session, AgentCompactTranscriptDirection.After, 2,
            older.Entries[^1].CreatedAtUtc, older.Entries[^1].TurnId));
        Assert.Equal(recent.Entries.Select(entry => entry.EntryId), newer.Entries.Select(entry => entry.EntryId));
        var around = fixture.Store.ReadCompactTranscript(new(fixture.Session, AgentCompactTranscriptDirection.Around, 3,
            AnchorTurnId: result.Turn, AnchorItemId: result.Item));
        Assert.Equal(first.Item, around.AnchorEntryId);
        Assert.Equal(firstMessage, around.Entries[0].TurnId);
        Assert.Equal(first.Item, around.Entries[1].EntryId);
        var exact = fixture.Store.LoadTranscriptAroundTurn(fixture.Session, result.Turn, 0, 0);
        var anchored = Assert.Single(exact.Turns);
        Assert.Equal(result.Turn, exact.AnchorTurnId);
        Assert.Equal(result.Item, Assert.Single(anchored.Items).ItemId);
        Assert.Equal(fixture.Store.GetTurn(first.Turn)!.TurnSequence, anchored.TurnSequence);
    }

    [Fact]
    public void EmptyMessagesDoNotSplitGroupsAndRunRevisionsDo()
    {
        using var fixture = new Fixture();
        fixture.Call("read");
        fixture.Message(" \t\n\u00a0");
        fixture.Call("read");
        fixture.RunRevision++;
        fixture.Call("read");
        fixture.Commit();
        var page = fixture.Store.ReadCompactTranscript(new(fixture.Session, AgentCompactTranscriptDirection.Recent));
        Assert.Equal(2, page.Entries.Count);
        Assert.Equal(2, page.Entries[0].Group!.Tools[0].Count);
        Assert.Equal(1, page.Entries[1].Group!.Tools[0].Count);
    }

    [Fact]
    public void OpeningSnapshotDoesNotChaseNewCallsAndRollbackEpochRejectsOldMemberRequests()
    {
        using var fixture = new Fixture();
        var first = fixture.Call("read");
        fixture.Commit();
        var group = Assert.Single(fixture.Store.ReadCompactTranscript(new(fixture.Session, AgentCompactTranscriptDirection.Recent)).Entries).Group!;
        var request = new AgentTranscriptActivityMembersRequest(fixture.Session, group.First.ItemId, group.TranscriptEpoch, group.Last);
        var sessions = new AgentSessionService(fixture.Store);
        sessions.AppendToolCallTurn(fixture.Session, AgentMessageRole.Assistant, "new-live-call", "shell", "{}");
        var members = fixture.Store.ReadActivityMembers(request);
        Assert.Equal(first.Item, Assert.Single(Assert.Single(members.Headers).Items).ItemId);
        Assert.False(members.HasMore);
        using var connection = new SqliteConnection($"Data Source={fixture.Store.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE AgentSessions SET PresentationEpoch = PresentationEpoch + 1 WHERE SessionId = $session;";
        command.Parameters.AddWithValue("$session", fixture.Session.ToString());
        command.ExecuteNonQuery();
        Assert.Throws<InvalidOperationException>(() => fixture.Store.ReadActivityMembers(request));
    }

    [Fact]
    public void CollapsedReadDoesNotAllocateLargeToolPayloadsAndSummaryUsesExecutionOutcomes()
    {
        using var fixture = new Fixture();
        var original = fixture.Call("maker_apply");
        fixture.Result(original, failed: true);
        fixture.Message("boundary");
        var check = fixture.Call("read");
        fixture.Result(check, failed: true);
        var huge = fixture.Call("read", new string('x', 4 * 1024 * 1024));
        fixture.Result(huge);
        fixture.Commit();
        var request = new AgentCompactTranscriptRequest(fixture.Session, AgentCompactTranscriptDirection.Recent);
        _ = fixture.Store.ReadCompactTranscript(request);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        var page = fixture.Store.ReadCompactTranscript(request);
        var allocation = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocation, 0, 1024 * 1024);
        Assert.Equal(1, page.Entries[0].Group!.FailedCount);
        Assert.Equal(0, page.Entries[0].Group!.CompletedCount);
        Assert.Equal(1, page.Entries[2].Group!.FailedCount);
        Assert.InRange(timer.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void CurrentToolReceiptsRequireTheirDurableExecutionIdentity()
    {
        using var fixture = new Fixture();
        var first = fixture.Call("read");
        fixture.Result(first);
        fixture.RunRevision++;
        var second = fixture.Call("read");
        fixture.Result(second);
        fixture.RunRevision++;
        var unidentified = fixture.Call("read");
        fixture.Result(unidentified);
        fixture.Commit();
        using (var connection = new SqliteConnection($"Data Source={fixture.Store.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE AgentTurnItems SET ToolExecutionId = NULL WHERE Kind IN ('ToolCall', 'ToolResult');";
            Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        }
        var page = fixture.Store.ReadCompactTranscript(new(fixture.Session, AgentCompactTranscriptDirection.Recent));
        Assert.Equal([1L, 1L, 1L], page.Entries.Select(entry => entry.Group!.Tools.Sum(tool => tool.Count)));
        foreach (var entry in page.Entries)
        {
            var group = entry.Group!;
            var headers = fixture.Store.ReadActivityMembers(new(fixture.Session, group.First.ItemId, group.TranscriptEpoch, group.Last));
            Assert.Equal(group.Tools.Sum(tool => tool.Count), headers.Headers.Count);
        }
    }

    [Fact]
    public void DurableSequenceOrdersBothModesAcrossBackwardClockChangesAndStructuralEditsFenceMembers()
    {
        using var fixture = new Fixture { BackwardClock = true };
        var leading = fixture.Message("leading");
        var first = fixture.Call("read");
        var separator = fixture.Message("");
        var second = fixture.Call("shell");
        fixture.Result(second);
        var result = fixture.Result(first);
        var trailing = fixture.Message("trailing");
        fixture.Commit();

        var compact = fixture.Store.ReadCompactTranscript(new(fixture.Session, AgentCompactTranscriptDirection.Recent));
        Assert.Equal([leading, first.Turn, trailing], compact.Entries.Select(entry => entry.TurnId));
        var detailed = fixture.Store.ListRecentTranscriptHeaders(fixture.Session, 20);
        Assert.Equal([leading, first.Turn, second.Turn, trailing], detailed.Select(turn => turn.TurnId));
        Assert.Equal(result.Turn, fixture.Store.LoadTranscriptAroundTurn(fixture.Session, result.Turn, 0, 0).AnchorTurnId);
        var original = compact.Entries[1].Group!;
        var members = new AgentTranscriptActivityMembersRequest(fixture.Session, original.First.ItemId, original.TranscriptEpoch, original.Last);

        fixture.Store.UpdateTextTurn(separator, "explanation");
        var split = fixture.Store.ReadCompactTranscript(new(fixture.Session, AgentCompactTranscriptDirection.Recent));
        Assert.Equal([leading, first.Turn, separator, second.Turn, trailing], split.Entries.Select(entry => entry.TurnId));
        Assert.True(split.TranscriptEpoch > compact.TranscriptEpoch);
        Assert.Throws<InvalidOperationException>(() => fixture.Store.ReadActivityMembers(members));
        fixture.Store.UpdateTextTurn(separator, "");
        var joined = fixture.Store.ReadCompactTranscript(new(fixture.Session, AgentCompactTranscriptDirection.Recent));
        Assert.Equal([leading, first.Turn, trailing], joined.Entries.Select(entry => entry.TurnId));
        Assert.Equal(2, joined.Entries[1].Group!.CompletedCount);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly RegressionTestPackageScope _scope = RegressionTestPackageScope.Create();
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;
        private int _sequence;
        private bool _committed;
        internal AgentLocalStore Store { get; }
        internal Guid Session { get; }
        internal long RunRevision = 1;
        internal bool BackwardClock;
        private readonly Guid _run = Guid.NewGuid();

        internal Fixture()
        {
            Store = new(_scope.Context);
            var workspace = new AgentWorkspaceService(Store).CreateWorkspace("Compact history");
            Session = Store.CreateSession("Compact history", workspaceId: workspace.WorkspaceId).SessionId;
            _connection = new($"Data Source={Store.DatabasePath}");
            _connection.Open();
            _transaction = _connection.BeginTransaction();
        }

        internal Guid Message(string text) => Insert("Message", "Text", null, null, text, null, false, null).Turn;
        internal Tool Call(string tool, string arguments = "{\"argument-canary\":true}")
        {
            var execution = Guid.NewGuid();
            return Insert("ToolCall", "ToolCall", tool, arguments, null, execution, false, null);
        }
        internal Tool Result(Tool call, bool failed = false, string? payload = null)
            => Insert("ToolResult", "ToolResult", call.ToolId, null, "output-canary", call.Execution, failed, payload);

        private Tool Insert(string kind, string itemKind, string? tool, string? arguments, string? text, Guid? execution, bool failed, string? payload)
        {
            var turn = Guid.NewGuid();
            var item = Guid.NewGuid();
            var timestamp = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero).AddTicks(++_sequence * (BackwardClock ? -1 : 1));
            var record = new AgentTurnRecord(turn, Session, AgentMessageRole.Assistant, Enum.Parse<AgentTurnKind>(kind),
                [new AgentTurnItemRecord(item, turn, 0, Enum.Parse<AgentTurnItemKind>(itemKind), text, execution?.ToString(), tool,
                    arguments, null, null, null, false, failed, null, null, payload) { ToolExecutionId = execution }], timestamp, timestamp)
                { RunId = _run, RunRevision = RunRevision };
            AgentLocalStore.InsertTurn(_connection, _transaction, record);
            return new(turn, item, execution ?? Guid.Empty, tool ?? "");
        }

        internal void Commit() { _transaction.Commit(); _committed = true; }
        public void Dispose() { if (!_committed) _transaction.Rollback(); _transaction.Dispose(); _connection.Dispose(); _scope.Dispose(); }
    }

    private sealed record Tool(Guid Turn, Guid Item, Guid Execution, string ToolId);
}
