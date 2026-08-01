extern alias AgentCore;

using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Package.Agent.Subagents.Runtime;
using Xunit;
using CorePresentation = AgentCore::Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentTranscriptLazyDetailTests
{
    [Fact]
    public async Task RuntimeCatalogPagingPreservesPayloadsWhileSubsessionsReadHeaders()
    {
        using var scope = RegressionTestPackageScope.Create();
        var extensions = new RegressionTestExtensionCatalog();
        var store = new AgentLocalStore(scope.Context);
        var sessions = new AgentSessionService(store, extensions);
        var workspaces = new AgentWorkspaceService(store, extensions, sessions);
        var toolService = new AgentToolService(
            sessions,
            workspaces,
            new AgentExecutionTargetService(extensions),
            extensions);
        using var profiles = new AgentProfileService(store, toolService, extensions, extensions.BehaviorLoops);
        var runtimeCatalog = new AgentRuntimeCatalog(sessions, profiles, workspaces);
        extensions.AddProvider(AgentRpcServices.RuntimeCatalogs, runtimeCatalog);
        IAgentRuntimeCatalog runtime = runtimeCatalog;
        var workspace = workspaces.CreateWorkspace("Catalog payloads");
        var session = sessions.CreateSession("Catalog payloads", workspaceId: workspace.WorkspaceId);
        AgentTurnRecord? catalogEventTurn = null;
        runtime.TurnChanged += (_, turn) => catalogEventTurn = turn;
        sessions.AppendToolResultTurn(
            session.SessionId,
            "payload-call",
            "payload-tool",
            "{\"privateArgument\":\"argument-canary\"}",
            "private-output-canary",
            "private-summary-canary",
            "{\"privateStructured\":true}",
            "[{\"privateSource\":true}]",
            wasTruncated: true,
            isError: true,
            errorCode: "private-error-canary",
            backendId: "private-backend-canary",
            presentationPayloadJson: "{\"privatePresentation\":true}");

        AssertHydratedToolPayload(Assert.IsType<AgentTurnRecord>(catalogEventTurn));
        AssertHydratedToolPayload(Assert.Single(runtime.ListRecentTurns(session.SessionId, 1)));
        AssertHydratedToolPayload(Assert.Single(runtime.ListTurnsBefore(
            session.SessionId,
            DateTimeOffset.MaxValue,
            Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
            1)));
        AssertHydratedToolPayload(Assert.Single(runtime.ListTurnsAfter(
            session.SessionId,
            DateTimeOffset.MinValue,
            Guid.Empty,
            1)));

        using var subsessions = new SubsessionLocalRuntimeAdapter(
            extensions.GetRequiredReference(AgentRpcServices.RuntimeCatalogs));
        var recent = await subsessions.ListRecentTurnsAsync(session.SessionId, 1);
        var before = await subsessions.ListTurnsBeforeAsync(
            session.SessionId,
            DateTimeOffset.MaxValue,
            Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
            1);
        var after = await subsessions.ListTurnsAfterAsync(
            session.SessionId,
            DateTimeOffset.MinValue,
            Guid.Empty,
            1);

        AssertHeaderOnly(Assert.Single(Assert.Single(recent.Turns).Items));
        AssertHeaderOnly(Assert.Single(Assert.Single(before.Turns).Items));
        AssertHeaderOnly(Assert.Single(Assert.Single(after.Turns).Items));
    }

    [Fact]
    public void AroundPage_ThousandParallelCallsUseResultRevisionOutsideWindow()
    {
        const int count = 1000;
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Parallel tools");
        var session = store.CreateSession("Parallel tools", workspaceId: workspace.WorkspaceId);
        var runId = Guid.NewGuid();
        var executionIds = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        var callTurnIds = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        var callItemIds = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        var resultTurnIds = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        var resultItemIds = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        var start = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

        using (var connection = Open(store.DatabasePath))
        using (var transaction = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO AgentTurns (
                    TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc,
                    ContentRevision, IsStreaming, RunId, RunRevision)
                VALUES
                    ($callTurnId, $sessionId, 'Assistant', 'ToolCall', $callCreated, $callUpdated,
                     1, 0, $runId, 1),
                    ($resultTurnId, $sessionId, 'Tool', 'ToolResult', $resultCreated, $resultUpdated,
                     1, 0, $runId, 1);

                INSERT INTO AgentTurnItems (
                    ItemId, TurnId, SequenceNumber, Kind, TextContent, CallId, ToolId,
                    ArgumentsJson, ResultSummary, WasTruncated, IsError, ToolExecutionId)
                VALUES
                    ($callItemId, $callTurnId, 0, 'ToolCall', NULL, $callId, 'parallel_tool',
                     $arguments, NULL, 0, 0, $executionId),
                    ($resultItemId, $resultTurnId, 0, 'ToolResult', $output, $callId, 'parallel_tool',
                     NULL, $summary, 0, 0, $executionId);
                """;
            command.Parameters.Add("$callTurnId", SqliteType.Text);
            command.Parameters.AddWithValue("$sessionId", session.SessionId.ToString());
            command.Parameters.Add("$callCreated", SqliteType.Text);
            command.Parameters.Add("$callUpdated", SqliteType.Text);
            command.Parameters.AddWithValue("$runId", runId.ToString());
            command.Parameters.Add("$resultTurnId", SqliteType.Text);
            command.Parameters.Add("$resultCreated", SqliteType.Text);
            command.Parameters.Add("$resultUpdated", SqliteType.Text);
            command.Parameters.Add("$callItemId", SqliteType.Text);
            command.Parameters.Add("$callId", SqliteType.Text);
            command.Parameters.Add("$arguments", SqliteType.Text);
            command.Parameters.Add("$executionId", SqliteType.Text);
            command.Parameters.Add("$resultItemId", SqliteType.Text);
            command.Parameters.Add("$output", SqliteType.Text);
            command.Parameters.Add("$summary", SqliteType.Text);

            for (var index = 0; index < count; index++)
            {
                var callTimestamp = start.AddTicks(index + 1);
                var resultTimestamp = start.AddMinutes(1).AddTicks(index + 1);
                command.Parameters["$callTurnId"].Value = callTurnIds[index].ToString();
                command.Parameters["$callCreated"].Value = callTimestamp.ToString("O");
                command.Parameters["$callUpdated"].Value = callTimestamp.ToString("O");
                command.Parameters["$resultTurnId"].Value = resultTurnIds[index].ToString();
                command.Parameters["$resultCreated"].Value = resultTimestamp.ToString("O");
                command.Parameters["$resultUpdated"].Value = resultTimestamp.ToString("O");
                command.Parameters["$callItemId"].Value = callItemIds[index].ToString();
                command.Parameters["$callId"].Value = $"parallel-{index}";
                command.Parameters["$arguments"].Value = $"{{\"index\":{index}}}";
                command.Parameters["$executionId"].Value = executionIds[index].ToString();
                command.Parameters["$resultItemId"].Value = resultItemIds[index].ToString();
                command.Parameters["$output"].Value = $"output-{index}";
                command.Parameters["$summary"].Value = $"summary-{index}";
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        const int anchorIndex = 500;
        var page = store.LoadTranscriptAroundTurn(
            session.SessionId,
            callTurnIds[anchorIndex],
            beforeLimit: 30,
            afterLimit: 30);
        var anchor = Assert.Single(page.Turns, turn => turn.TurnId == callTurnIds[anchorIndex]);
        var header = Assert.Single(anchor.Items);
        var expectedTimestamp = start.AddMinutes(1).AddTicks(anchorIndex + 1);
        var expectedRevision = (expectedTimestamp.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks) / 10;

        Assert.Equal(61, page.Turns.Count);
        Assert.DoesNotContain(page.Turns, turn => turn.TurnId == resultTurnIds[anchorIndex]);
        Assert.Equal(executionIds[anchorIndex], header.ToolExecutionId);
        Assert.Equal(AgentToolExecutionStatus.Completed, header.ToolExecutionStatus);
        Assert.Equal(expectedRevision, header.ToolDetailRevision);
        Assert.True(header.ToolHasDetails);
        Assert.All(page.Turns.SelectMany(turn => turn.Items), AssertHeaderOnly);

        var detail = store.GetTranscriptToolDetail(new AgentTranscriptToolDetailRequest(
            session.SessionId,
            header.ToolExecutionId,
            header.CallId,
            header.ItemId,
            anchor.RunId,
            anchor.RunRevision));

        Assert.NotNull(detail);
        Assert.Equal(expectedRevision, detail.Revision);
        Assert.Equal($"output-{anchorIndex}", detail.OutputText);
        Assert.Equal($"{{\"index\":{anchorIndex}}}", detail.ArgumentsJson);
        Assert.Equal(resultItemIds[anchorIndex], detail.ResultItemId);
    }

    [Fact]
    public async Task HeaderReadsAndStartup_DoNotAllocateHugeToolFields_DetailReadIsBounded()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessions = new AgentSessionService(store);
        var workspaces = new AgentWorkspaceService(store, sessionService: sessions);
        var workspace = workspaces.CreateWorkspace("Huge fields");
        var session = sessions.CreateSession("Huge fields", workspaceId: workspace.WorkspaceId);
        var turnId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        string? huge = new('x', 4 * 1024 * 1024);
        var timestamp = new DateTimeOffset(2026, 7, 28, 13, 0, 0, TimeSpan.Zero);
        using (var connection = Open(store.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO AgentTurns (
                    TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc,
                    ContentRevision, IsStreaming, RunId, RunRevision)
                VALUES ($turnId, $sessionId, 'Tool', 'ToolResult', $created, $updated, 1, 0, NULL, NULL);
                INSERT INTO AgentTurnItems (
                    ItemId, TurnId, SequenceNumber, Kind, TextContent, CallId, ToolId,
                    ArgumentsJson, ResultSummary, StructuredPayloadJson, SourcesJson,
                    WasTruncated, IsError, ErrorCode, BackendId, PresentationPayloadJson,
                    ToolExecutionId)
                VALUES ($itemId, $turnId, 0, 'ToolResult', $huge, 'huge-call', 'huge_tool',
                    $huge, $huge, $huge, $huge, 0, 0, NULL, 'huge-backend', $huge,
                    $executionId);
                """;
            command.Parameters.AddWithValue("$turnId", turnId.ToString());
            command.Parameters.AddWithValue("$sessionId", session.SessionId.ToString());
            command.Parameters.AddWithValue("$created", timestamp.ToString("O"));
            command.Parameters.AddWithValue("$updated", timestamp.ToString("O"));
            command.Parameters.AddWithValue("$itemId", itemId.ToString());
            command.Parameters.AddWithValue("$huge", huge);
            command.Parameters.AddWithValue("$executionId", executionId.ToString());
            command.ExecuteNonQuery();
        }
        huge = null;

        _ = store.ListRecentTranscriptHeaders(session.SessionId, 1);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var beforeHeaderAllocation = GC.GetAllocatedBytesForCurrentThread();
        var recent = store.ListRecentTranscriptHeaders(session.SessionId, 1);
        var headerAllocation = GC.GetAllocatedBytesForCurrentThread() - beforeHeaderAllocation;
        var header = Assert.Single(Assert.Single(recent).Items);

        Assert.InRange(headerAllocation, 0, 2 * 1024 * 1024);
        AssertHeaderOnly(header);
        Assert.True(header.ToolHasDetails);
        Assert.Single(store.ListTranscriptHeadersBefore(
            session.SessionId,
            DateTimeOffset.MaxValue,
            Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
            1));
        Assert.Single(store.ListTranscriptHeadersAfter(
            session.SessionId,
            DateTimeOffset.MinValue,
            Guid.Empty,
            1));
        Assert.NotNull(store.GetTranscriptHeader(turnId));
        Assert.Single(store.LoadTranscriptAroundTurn(session.SessionId, turnId, 0, 0).Turns);

        var snapshot = await store.ReadChatSnapshotAsync(
            1,
            new AgentChatSnapshotRequest(
                InitialTranscriptLimit: 1,
                PreferredWorkspaceId: workspace.WorkspaceId,
                PreferredSessionId: session.SessionId),
            storedProfileId: null,
            storedWorkspaceId: workspace.WorkspaceId,
            (_, _) => Task.FromResult<Guid?>(session.SessionId));
        var startupHeader = Assert.Single(Assert.Single(snapshot.InitialTranscript.Turns).Items);
        AssertHeaderOnly(startupHeader);
        Assert.True(startupHeader.ToolHasDetails);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var beforeDetailAllocation = GC.GetAllocatedBytesForCurrentThread();
        var detail = store.GetTranscriptToolDetail(new(
            session.SessionId,
            ToolExecutionId: executionId,
            ItemId: itemId));
        var detailAllocation = GC.GetAllocatedBytesForCurrentThread() - beforeDetailAllocation;

        Assert.NotNull(detail);
        Assert.True(detail.WasTransportTruncated);
        Assert.InRange(detailAllocation, 0, 4 * 1024 * 1024);
        Assert.Null(detail.ArgumentsJson);
        Assert.Null(detail.StructuredPayloadJson);
        Assert.Null(detail.SourcesJson);
        Assert.Null(detail.PresentationPayloadJson);
        Assert.Equal(64 * 1024, detail.OutputText?.Length);
    }

    [Fact]
    public async Task ChangeHub_TurnEventsUseBoundedSingleHeaderReadsWithoutHydratingToolPayloads()
    {
        using var scope = RegressionTestPackageScope.Create();
        var extensions = new RegressionTestExtensionCatalog();
        var store = new AgentLocalStore(scope.Context);
        var sessions = new AgentSessionService(store, extensions);
        var workspaces = new AgentWorkspaceService(store, extensions, sessions);
        var toolService = new AgentToolService(
            sessions,
            workspaces,
            new AgentExecutionTargetService(extensions),
            extensions);
        using var profiles = new AgentProfileService(store, toolService, extensions, extensions.BehaviorLoops);
        using var changes = new AgentRuntimeChangeHub(profiles, workspaces, sessions);
        var workspace = workspaces.CreateWorkspace("Live header reads");
        var session = sessions.CreateSession("Live header reads", workspaceId: workspace.WorkspaceId);
        var turnId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var timestamp = new DateTimeOffset(2026, 7, 28, 14, 0, 0, TimeSpan.Zero);
        string? huge = new('x', 4 * 1024 * 1024);
        using (var connection = Open(store.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO AgentTurns (
                    TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc,
                    ContentRevision, IsStreaming, RunId, RunRevision)
                VALUES ($turnId, $sessionId, 'Tool', 'ToolResult', $timestamp, $timestamp,
                        1, 0, NULL, NULL);
                INSERT INTO AgentTurnItems (
                    ItemId, TurnId, SequenceNumber, Kind, TextContent, CallId, ToolId,
                    ArgumentsJson, ResultSummary, StructuredPayloadJson, SourcesJson,
                    WasTruncated, IsError, ErrorCode, BackendId, PresentationPayloadJson,
                    ToolExecutionId)
                VALUES ($itemId, $turnId, 0, 'ToolResult', $huge, 'live-heavy-call',
                        'live-heavy-tool', $huge, $huge, $huge, $huge, 0, 0,
                        'live-heavy-error', 'live-heavy-backend', $huge, $executionId);
                """;
            command.Parameters.AddWithValue("$turnId", turnId.ToString());
            command.Parameters.AddWithValue("$sessionId", session.SessionId.ToString());
            command.Parameters.AddWithValue("$timestamp", timestamp.ToString("O"));
            command.Parameters.AddWithValue("$itemId", itemId.ToString());
            command.Parameters.AddWithValue("$huge", huge);
            command.Parameters.AddWithValue("$executionId", executionId.ToString());
            command.ExecuteNonQuery();
        }

        // Warm the notification and serialization paths before measuring the exact heavy turn lookup.
        sessions.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, "warmup");
        var afterRevision = changes.Revision;
        var publishMutation = typeof(AgentRuntimeChangeHub).GetMethod(
            "OnTurnMutated",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var mutation = new AgentTurnMutation(
            session.SessionId,
            turnId,
            1,
            AgentTurnMutationKind.Add,
            BaseContentLength: 0,
            Text: null,
            timestamp);
        huge = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var beforeAllocation = GC.GetAllocatedBytesForCurrentThread();

        publishMutation.Invoke(changes, [mutation]);

        var allocation = GC.GetAllocatedBytesForCurrentThread() - beforeAllocation;
        Assert.InRange(allocation, 0, 2 * 1024 * 1024);
        await using var subscription = changes.SubscribeAsync(
            new AgentChangeSubscription(afterRevision)).GetAsyncEnumerator();
        Assert.True(await subscription.MoveNextAsync());
        var change = subscription.Current;
        Assert.Equal(AgentRuntimeChangeKind.Turn, change.Kind);
        var header = Assert.Single(Assert.IsType<AgentTurnRecord>(change.Turn).Items);
        AssertHeaderOnly(header);
        Assert.True(header.ToolHasDetails);
    }

    [Fact]
    public void EmptyToolInvocation_HasDetailsParityBetweenLiveAndReloadedHeaders()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessions = new AgentSessionService(store);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Empty tool detail");
        var session = sessions.CreateSession(
            "Empty tool detail",
            workspaceId: workspace.WorkspaceId);
        var call = sessions.AppendToolCallTurn(
            session.SessionId,
            AgentMessageRole.Assistant,
            "empty-call",
            "empty-tool",
            "{}");
        var result = sessions.AppendToolResultTurn(
            session.SessionId,
            "empty-call",
            "empty-tool",
            argumentsJson: null,
            content: null,
            resultSummary: null,
            structuredPayloadJson: null,
            sourcesJson: null,
            wasTruncated: false,
            isError: false,
            errorCode: null,
            backendId: null);
        var executionId = Guid.NewGuid();
        using (var connection = Open(store.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE AgentTurnItems
                SET ToolExecutionId = $executionId
                WHERE ItemId IN ($callItemId, $resultItemId);
                """;
            command.Parameters.AddWithValue("$executionId", executionId.ToString());
            command.Parameters.AddWithValue("$callItemId", call.Items[0].ItemId.ToString());
            command.Parameters.AddWithValue("$resultItemId", result.Items[0].ItemId.ToString());
            command.ExecuteNonQuery();
        }

        var fullTurns = store.ListTurns(session.SessionId);
        var liveHeaders = fullTurns
            .Select(CorePresentation.TranscriptTurnTransportProjection.ProjectToolHeaders)
            .ToDictionary(turn => turn.TurnId);
        var reloadedHeaders = store.ListRecentTranscriptHeaders(session.SessionId, 10)
            .ToDictionary(turn => turn.TurnId);

        Assert.False(Assert.Single(liveHeaders[call.TurnId].Items).ToolHasDetails);
        Assert.False(Assert.Single(liveHeaders[result.TurnId].Items).ToolHasDetails);
        Assert.False(Assert.Single(reloadedHeaders[call.TurnId].Items).ToolHasDetails);
        Assert.False(Assert.Single(reloadedHeaders[result.TurnId].Items).ToolHasDetails);
        var detail = Assert.IsType<AgentTranscriptToolDetailRecord>(store.GetTranscriptToolDetail(new(
            session.SessionId,
            executionId,
            ItemId: call.Items[0].ItemId)));
        Assert.Equal("{}", detail.ArgumentsJson);
        Assert.Null(detail.OutputText);
    }

    private static void AssertHydratedToolPayload(AgentTurnRecord turn)
    {
        var item = Assert.Single(turn.Items);
        Assert.False(item.IsToolHeaderProjection);
        Assert.Equal("private-output-canary", item.TextContent);
        Assert.Equal("{\"privateArgument\":\"argument-canary\"}", item.ArgumentsJson);
        Assert.Equal("private-summary-canary", item.ResultSummary);
        Assert.Equal("{\"privateStructured\":true}", item.StructuredPayloadJson);
        Assert.Equal("[{\"privateSource\":true}]", item.SourcesJson);
        Assert.Equal("private-error-canary", item.ErrorCode);
        Assert.Equal("private-backend-canary", item.BackendId);
        Assert.Equal("{\"privatePresentation\":true}", item.PresentationPayloadJson);
    }

    private static void AssertHeaderOnly(AgentTurnItemRecord item)
    {
        Assert.True(item.IsToolHeaderProjection);
        Assert.Null(item.TextContent);
        Assert.Null(item.ArgumentsJson);
        Assert.Null(item.ResultSummary);
        Assert.Null(item.StructuredPayloadJson);
        Assert.Null(item.SourcesJson);
        Assert.Null(item.ErrorCode);
        Assert.Null(item.BackendId);
        Assert.Null(item.PresentationPayloadJson);
        Assert.Null(item.ToolOwnerPackageId);
        Assert.Null(item.ToolSchemaId);
        Assert.Null(item.ToolSchemaVersion);
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}
