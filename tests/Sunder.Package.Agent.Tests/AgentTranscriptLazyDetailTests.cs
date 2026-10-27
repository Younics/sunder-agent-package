extern alias AgentCore;

using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Rpc;
using Xunit;
using CorePresentation = AgentCore::Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.Tests;

extern alias SubagentsApp;
extern alias SubagentsWorker;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Runtime;
using IRuntimeCatalogService = SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Runtime_Catalog.IRuntimeCatalogService;
using SubagentModels = SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models;

public sealed class AgentTranscriptLazyDetailTests
{
    [Fact]
    public void HistoricalWriteHeaderKeepsBoundedPathWhileArgumentsAndPersistedDiffRemainLazy()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessions = new AgentSessionService(store);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Write history");
        var session = sessions.CreateSession("Write history", workspaceId: workspace.WorkspaceId);
        var args = System.Text.Json.JsonSerializer.Serialize(new { path = "Services/WeatherService.cs", content = new string('x', 64 * 1024) });
        var turn = sessions.AppendToolResultTurn(session.SessionId, "write-history", "write", args, "Wrote 65536 characters",
            "Write completed", null, null, false, false, null, null,
            "{\"schema\":\"younics.ai.agent.tools.files.diff.v1\",\"files\":[]}");
        var header = Assert.Single(Assert.Single(store.LoadTranscriptAroundTurn(session.SessionId, turn.TurnId, 0, 0).Turns).Items);
        Assert.Equal("Services/WeatherService.cs", header.ToolHeaderHint);
        Assert.Null(header.ArgumentsJson);
        Assert.Null(header.TextContent);
        Assert.Null(header.PresentationPayloadJson);
        Assert.Equal(args, Assert.Single(store.GetTurn(turn.TurnId)!.Items).ArgumentsJson);
    }

    [Fact]
    public void PresentationReferencesDoNotMergeIndependentExecutionReceipts()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessions = new AgentSessionService(store);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Operation receipts");
        var session = sessions.CreateSession("Operation receipts", workspaceId: workspace.WorkspaceId);
        var original = sessions.AppendToolResultTurn(session.SessionId, "apply", "maker_apply", "{}", "original-receipt", "Package operation applied", null, null,
            false, false, null, null, "{\"operationStatus\":\"running\",\"operationRef\":\"o-original\"}");
        var originalHeader = Assert.Single(Assert.Single(store.LoadTranscriptAroundTurn(session.SessionId, original.TurnId, 0, 0).Turns).Items);
        var originalGroup = Assert.Single(store.ReadCompactTranscript(new(session.SessionId, AgentCompactTranscriptDirection.Recent)).Entries).Group!;
        var failed = sessions.AppendToolResultTurn(session.SessionId, "other", "maker_apply", "{}", "other-receipt", "Other operation failed", null, null,
            false, true, "maker.failed", null, "{\"operationStatus\":\"failed\",\"operationRef\":\"o-original\"}");
        var foreign = sessions.CreateSession("Other session", workspaceId: workspace.WorkspaceId);
        sessions.AppendToolResultTurn(foreign.SessionId, "foreign", "maker_apply", "{}", "foreign-failure", "Foreign failure", null, null,
            false, true, null, null, "{\"operationStatus\":\"failed\",\"operationRef\":\"o-original\"}");
        var page = store.LoadTranscriptAroundTurn(session.SessionId, original.TurnId, 0, 0);
        var header = Assert.Single(Assert.Single(page.Turns).Items);
        Assert.Null(header.PresentationPayloadJson);
        Assert.Equal(AgentToolExecutionStatus.Completed, header.ToolExecutionStatus);
        Assert.Equal("Package operation applied", header.ToolHeaderHint);
        Assert.Equal(originalHeader.ToolDetailRevision, header.ToolDetailRevision);
        var detail = store.GetTranscriptToolDetail(new(session.SessionId, null, "apply", header.ItemId));
        Assert.NotNull(detail);
        Assert.Equal(header.ToolDetailRevision, detail.Revision);
        Assert.Equal("original-receipt", detail.OutputText);
        var refreshedGroup = Assert.Single(store.ReadCompactTranscript(new(session.SessionId, AgentCompactTranscriptDirection.Recent)).Entries).Group!;
        Assert.True(refreshedGroup.Revision > originalGroup.Revision);
        var raw = Assert.Single(store.GetTurn(original.TurnId)!.Items);
        Assert.Equal("original-receipt", raw.TextContent);
        Assert.Contains("running", raw.PresentationPayloadJson);
        var other = Assert.Single(Assert.Single(store.LoadTranscriptAroundTurn(session.SessionId, failed.TurnId, 0, 0).Turns).Items);
        Assert.Equal(AgentToolExecutionStatus.Failed, other.ToolExecutionStatus);
        Assert.Equal("Other operation failed", other.ToolErrorSummary);
        Assert.NotEqual(header.ToolExecutionId, other.ToolExecutionId);
    }
    [Fact]
    public async Task RuntimeCatalogPagingPreservesPayloadsWhileSubsessionsReadHeaders()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var extensions = new RegressionTestExtensionCatalog();
        var store = new AgentLocalStore(scope.Context);
        var sessions = new AgentSessionService(store, extensions.SessionCleaners);
        var workspaces = new AgentWorkspaceService(store, extensions.SessionCleaners, sessions);
        var toolService = new AgentToolService(
            sessions,
            workspaces,
            new AgentExecutionTargetService(extensions.ExecutionTargets),
            extensions.ToolSources,
            extensions.ObservedToolExecutions);
        using var profiles = extensions.CreateProfileService(store, toolService);
        var runtimeCatalog = new AgentRuntimeCatalog(sessions, profiles, workspaces);
        extensions.AddRuntimeCatalog(runtimeCatalog);
        var workspace = workspaces.CreateWorkspace("Catalog payloads");
        var session = sessions.CreateSession("Catalog payloads", workspaceId: workspace.WorkspaceId);
        var first = sessions.AppendTextTurn(session.SessionId, AgentMessageRole.User, "before");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = runtimeCatalog.WatchAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await events.MoveNextAsync());
        Assert.IsType<AgentRuntimeCatalogResetEvent>(events.Current);
        var payload = sessions.AppendToolResultTurn(
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
        var last = sessions.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, "after");

        while (await events.MoveNextAsync() && events.Current is not AgentTurnCatalogEvent) { }
        var catalogEvent = Assert.IsType<AgentTurnCatalogEvent>(events.Current);
        Assert.Equal(session.SessionId, catalogEvent.SessionId);
        AssertHydratedToolPayload(catalogEvent.Turn);
        AssertHydratedToolPayload((await runtimeCatalog.ListRecentTurnsAsync(session.SessionId, 2))[0]);
        AssertHydratedToolPayload(Assert.Single(await runtimeCatalog.ListTurnsBeforeAsync(
            session.SessionId,
            last.CreatedAtUtc,
            last.TurnId,
            1)));
        AssertHydratedToolPayload(Assert.Single(await runtimeCatalog.ListTurnsAfterAsync(
            session.SessionId,
            first.CreatedAtUtc,
            first.TurnId,
            1)));

        var subsessions = Assert.Single((await extensions.Subagents()
            .Providers<IRuntimeCatalogService>()
            .ListAsync()).Providers).Service;
        var recent = await SubsessionTranscriptReader.RecentAsync(
            subsessions, session.SessionId, 2, CancellationToken.None);
        var before = await SubsessionTranscriptReader.BeforeAsync(
            subsessions,
            session.SessionId,
            new SunderRpcOffsetDateTime(last.CreatedAtUtc.ToString("O")),
            last.TurnId,
            1,
            CancellationToken.None);
        var after = await SubsessionTranscriptReader.AfterAsync(
            subsessions,
            session.SessionId,
            new SunderRpcOffsetDateTime(first.CreatedAtUtc.ToString("O")),
            first.TurnId,
            1,
            CancellationToken.None);

        AssertHeaderOnly(Assert.Single(Assert.Single(recent.Turns, turn => turn.TurnId == payload.TurnId).Items));
        AssertHeaderOnly(Assert.Single(Assert.Single(before.Turns).Items));
        AssertHeaderOnly(Assert.Single(Assert.Single(after.Turns).Items));

        var child = sessions.CreateSession("child receipt", parentSessionId: session.SessionId,
            rootSessionId: session.SessionId, workspaceId: workspace.WorkspaceId);
        var childCall = sessions.AppendToolCallTurn(child.SessionId, AgentMessageRole.Assistant, "child-call", "read", "{}");
        var childResult = sessions.AppendToolResultTurn(child.SessionId, "child-call", "read", "{}", "child-output-canary",
            "finished", null, null, false, false, null, null);
        var exact = await SubsessionTranscriptReader.AroundAsync(subsessions, child.SessionId, childResult.TurnId,
            childResult.CreatedAtUtc, childResult.Items[0].ItemId, 0, 0, CancellationToken.None);
        var exactTurn = Assert.Single(exact.Turns);
        Assert.Equal(childResult.TurnId, exact.AnchorTurnId);
        Assert.Equal(childResult.Items[0].ItemId, Assert.Single(exactTurn.Items).ItemId);
        Assert.Equal(childCall.TurnSequence, exactTurn.TurnSequence);
        AssertHeaderOnly(exactTurn.Items[0]);
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
        {
            for (var index = 0; index < count; index++)
            {
                SeedTool(connection, transaction, session.SessionId, callTurnIds[index], callItemIds[index], executionIds[index],
                    start.AddTicks(index + 1), AgentTurnItemKind.ToolCall, $"parallel-{index}", "parallel_tool",
                    $"{{\"index\":{index}}}", runId: runId);
            }
            for (var index = 0; index < count; index++)
                SeedTool(connection, transaction, session.SessionId, resultTurnIds[index], resultItemIds[index], executionIds[index],
                    start.AddMinutes(1).AddTicks(index + 1), AgentTurnItemKind.ToolResult, $"parallel-{index}", "parallel_tool",
                    output: $"output-{index}", summary: $"summary-{index}", runId: runId);
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
        const long expectedRevision = 2;

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
        var first = sessions.AppendTextTurn(session.SessionId, AgentMessageRole.User, "before");
        var turnId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        string? huge = new('x', 4 * 1024 * 1024);
        var timestamp = new DateTimeOffset(2026, 7, 28, 13, 0, 0, TimeSpan.Zero);
        using (var connection = Open(store.DatabasePath))
        using (var transaction = connection.BeginTransaction())
        {
            SeedTool(connection, transaction, session.SessionId, turnId, itemId, executionId, timestamp,
                AgentTurnItemKind.ToolResult, "huge-call", "huge_tool", huge, huge, huge, huge, huge, huge);
            transaction.Commit();
        }
        huge = null;
        var last = sessions.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, "after");

        _ = store.ListTranscriptHeadersBefore(session.SessionId, last.CreatedAtUtc, last.TurnId, 1);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var beforeHeaderAllocation = GC.GetAllocatedBytesForCurrentThread();
        var recent = store.ListTranscriptHeadersBefore(session.SessionId, last.CreatedAtUtc, last.TurnId, 1);
        var headerAllocation = GC.GetAllocatedBytesForCurrentThread() - beforeHeaderAllocation;
        var header = Assert.Single(Assert.Single(recent).Items);

        Assert.InRange(headerAllocation, 0, 2 * 1024 * 1024);
        AssertHeaderOnly(header);
        Assert.True(header.ToolHasDetails);
        Assert.Single(store.ListTranscriptHeadersBefore(
            session.SessionId,
            last.CreatedAtUtc,
            last.TurnId,
            1));
        Assert.Single(store.ListTranscriptHeadersAfter(
            session.SessionId,
            first.CreatedAtUtc,
            first.TurnId,
            1));
        Assert.NotNull(store.GetTranscriptHeader(turnId));
        Assert.Single(store.LoadTranscriptAroundTurn(session.SessionId, turnId, 0, 0).Turns);

        var snapshot = await store.ReadChatSnapshotAsync(
            1,
            new AgentChatSnapshotRequest(
                InitialTranscriptLimit: 2,
                PreferredWorkspaceId: workspace.WorkspaceId,
                PreferredSessionId: session.SessionId));
        var startupHeader = Assert.Single(Assert.Single(snapshot.InitialTranscript.Turns, turn => turn.TurnId == turnId).Items);
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
        await using var extensions = new RegressionTestExtensionCatalog();
        var store = new AgentLocalStore(scope.Context);
        var sessions = new AgentSessionService(store, extensions.SessionCleaners);
        var workspaces = new AgentWorkspaceService(store, extensions.SessionCleaners, sessions);
        var toolService = new AgentToolService(
            sessions,
            workspaces,
            new AgentExecutionTargetService(extensions.ExecutionTargets),
            extensions.ToolSources,
            extensions.ObservedToolExecutions);
        using var profiles = extensions.CreateProfileService(store, toolService);
        using var changes = new AgentRuntimeChangeHub(profiles, workspaces, sessions);
        var workspace = workspaces.CreateWorkspace("Live header reads");
        var session = sessions.CreateSession("Live header reads", workspaceId: workspace.WorkspaceId);
        var turnId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var timestamp = new DateTimeOffset(2026, 7, 28, 14, 0, 0, TimeSpan.Zero);
        string? huge = new('x', 4 * 1024 * 1024);
        using (var connection = Open(store.DatabasePath))
        using (var transaction = connection.BeginTransaction())
        {
            SeedTool(connection, transaction, session.SessionId, turnId, itemId, executionId, timestamp,
                AgentTurnItemKind.ToolResult, "live-heavy-call", "live-heavy-tool", huge, huge, huge, huge, huge, huge);
            transaction.Commit();
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
        var executionId = Guid.NewGuid();
        AgentTurnRecord call, result;
        using (var connection = Open(store.DatabasePath))
        using (var transaction = connection.BeginTransaction())
        {
            call = SeedTool(connection, transaction, session.SessionId, Guid.NewGuid(), Guid.NewGuid(), executionId,
                DateTimeOffset.UtcNow, AgentTurnItemKind.ToolCall, "empty-call", "empty-tool", "{}");
            result = SeedTool(connection, transaction, session.SessionId, Guid.NewGuid(), Guid.NewGuid(), executionId,
                DateTimeOffset.UtcNow, AgentTurnItemKind.ToolResult, "empty-call", "empty-tool");
            transaction.Commit();
        }

        var fullTurns = store.ListTurns(session.SessionId);
        var liveHeaders = fullTurns
            .Select(turn => CorePresentation.TranscriptTurnTransportProjection.ProjectToolHeaders(
                AgentRoleBoundary.As<AgentCore::Sunder.Package.Agent.Contracts.Models.AgentTurnRecord>(turn)))
            .ToDictionary(turn => turn.TurnId);
        var reloadedHeaders = store.ListRecentTranscriptHeaders(session.SessionId, 10)
            .ToDictionary(turn => turn.TurnId);

        Assert.False(Assert.Single(liveHeaders[call.TurnId].Items).ToolHasDetails);
        Assert.False(Assert.Single(liveHeaders[result.TurnId].Items).ToolHasDetails);
        Assert.False(Assert.Single(reloadedHeaders[call.TurnId].Items).ToolHasDetails);
        Assert.Single(reloadedHeaders);
        Assert.False(Assert.Single(store.GetTranscriptHeader(result.TurnId)!.Items).ToolHasDetails);
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

    private static AgentTurnRecord SeedTool(SqliteConnection connection, SqliteTransaction transaction, Guid session,
        Guid turn, Guid item, Guid execution, DateTimeOffset timestamp, AgentTurnItemKind kind, string call, string tool,
        string? arguments = null, string? output = null, string? summary = null, string? structured = null,
        string? sources = null, string? presentation = null, Guid? runId = null)
        => AgentLocalStore.InsertTurn(connection, transaction, new AgentTurnRecord(turn, session,
            kind == AgentTurnItemKind.ToolCall ? AgentMessageRole.Assistant : AgentMessageRole.Tool,
            kind == AgentTurnItemKind.ToolCall ? AgentTurnKind.ToolCall : AgentTurnKind.ToolResult,
            [new(item, turn, 0, kind, output, call, tool, arguments, summary, structured, sources, false, false,
                null, null, presentation) { ToolExecutionId = execution }], timestamp, timestamp)
            { RunId = runId, RunRevision = runId is null ? null : 1 });

    private static void AssertHeaderOnly(AgentTurnItemRecord item)
        => AssertHeaderOnly(item.IsToolHeaderProjection, item.TextContent, item.ArgumentsJson, item.ResultSummary,
            item.StructuredPayloadJson, item.SourcesJson, item.ErrorCode, item.BackendId, item.PresentationPayloadJson,
            item.ToolOwnerPackageId, item.ToolSchemaId, item.ToolSchemaVersion);

    private static void AssertHeaderOnly(SubagentModels.AgentTurnItemRecord item)
        => AssertHeaderOnly(item.IsToolHeaderProjection, item.TextContent, item.ArgumentsJson, item.ResultSummary,
            item.StructuredPayloadJson, item.SourcesJson, item.ErrorCode, item.BackendId, item.PresentationPayloadJson,
            item.ToolOwnerPackageId, item.ToolSchemaId, item.ToolSchemaVersion);

    private static void AssertHeaderOnly(bool isHeader, params string?[] payloads)
    {
        Assert.True(isHeader);
        Assert.All(payloads, Assert.Null);
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
