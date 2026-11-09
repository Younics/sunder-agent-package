using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Xunit;
using static Sunder.Package.Agent.Tests.WorkspaceServiceFixture;
using static Sunder.Package.Agent.Tests.WorkspaceStorageFixture;

namespace Sunder.Package.Agent.Tests;

[Collection("Workspace component behavior")]
public sealed class WorkspacePersistenceTests
{
    [Fact]
    public void AgentLocalStore_DoesNotSeedWorkspaceProfileOrSession()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);

        Assert.Empty(store.ListWorkspaces());
        Assert.Empty(store.ListProfiles());
        Assert.Empty(store.ListSessions());
    }

    [Fact]
    public async Task AgentLocalStore_AsyncWorkspaceRead_DoesNotBlockCallerAndObservesCancellation()
    {
        using var scope = TestScope.Create();
        using var store = new AgentLocalStore(scope.Context);
        var callerThread = Environment.CurrentManagedThreadId;
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.WorkspaceReadStarting = cancellationToken =>
        {
            started.TrySetResult(Environment.CurrentManagedThreadId);
            cancellationToken.WaitHandle.WaitOne();
            cancellationToken.ThrowIfCancellationRequested();
        };
        using var cancellation = new CancellationTokenSource();

        var read = store.ListWorkspacesAsync(cancellation.Token);
        var workerThread = await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotEqual(callerThread, workerThread);
        Assert.False(read.IsCompleted);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read)
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AgentLocalStore_AsyncWorkspaceReads_UseIndependentConcurrentOperations()
    {
        using var scope = TestScope.Create();
        using var store = new AgentLocalStore(scope.Context);
        var service = new AgentWorkspaceService(store);
        var workspace = service.CreateWorkspace("Concurrent");
        using var release = new ManualResetEventSlim();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        store.WorkspaceReadStarting = cancellationToken =>
        {
            var current = Interlocked.Increment(ref active);
            var observed = Volatile.Read(ref maximumActive);
            while (current > observed)
            {
                var prior = Interlocked.CompareExchange(ref maximumActive, current, observed);
                if (prior == observed)
                {
                    break;
                }
                observed = prior;
            }
            if (current == 2)
            {
                bothStarted.TrySetResult();
            }
            release.Wait(cancellationToken);
            Interlocked.Decrement(ref active);
        };

        var list = service.ListWorkspacesAsync();
        var get = service.GetWorkspaceAsync(workspace.WorkspaceId);
        try
        {
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, maximumActive);
        }
        finally
        {
            release.Set();
        }

        Assert.Contains(workspace.WorkspaceId, (await list).Select(static item => item.WorkspaceId));
        Assert.Equal(workspace.WorkspaceId, (await get)?.WorkspaceId);
    }

    [Fact]
    public void AgentWorkspaceService_SaveWorkspacePathsAndDocuments_PersistsHydratedWorkspace()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var service = new AgentWorkspaceService(store);
        var root = Path.Combine(scope.RootPath, "workspace");
        var documentPath = Path.Combine(scope.RootPath, "README.md");
        Directory.CreateDirectory(root);
        File.WriteAllText(documentPath, "Workspace docs.");
        var workspace = service.CreateWorkspace("Docs Workspace");

        service.SaveWorkspacePaths(workspace.WorkspaceId,
        [
            new AgentWorkspacePathRecord(string.Empty, workspace.WorkspaceId, root, IsDefault: false, 10, default, default),
        ]);
        service.SaveWorkspaceDocuments(workspace.WorkspaceId,
        [
            new AgentWorkspaceDocumentRecord(string.Empty, workspace.WorkspaceId, documentPath, 10, default, default),
        ]);

        var hydrated = service.GetWorkspace(workspace.WorkspaceId);

        Assert.NotNull(hydrated);
        var path = Assert.Single(hydrated!.Paths);
        Assert.Equal(Path.GetFullPath(root), path.HostPath);
        Assert.True(path.IsDefault);
        Assert.Equal(0, path.SortOrder);
        var document = Assert.Single(hydrated.Documents);
        Assert.Equal(Path.GetFullPath(documentPath), document.FilePath);
        Assert.Equal(0, document.SortOrder);
    }

    [Fact]
    public void AgentWorkspaceService_AggregateStageFailureRollsBackEveryStageAndNotification()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var service = new AgentWorkspaceService(store);
        var workspace = service.CreateWorkspace("before");
        var notifications = 0;
        service.WorkspacesChanged += () => notifications++;
        store.WorkspaceAggregateStageCompleted = stage =>
        {
            if (stage == "documents") throw new InvalidOperationException("injected workspace stage failure");
        };

        Assert.Throws<InvalidOperationException>(() => service.SaveWorkspaceAggregate(
            workspace.WorkspaceId,
            "after",
            "changed",
            [new AgentWorkspacePathRecord("path", workspace.WorkspaceId, scope.RootPath, true, 0, default, default)],
            [new AgentWorkspaceDocumentRecord("doc", workspace.WorkspaceId, Path.Combine(scope.RootPath, "README.md"), 0, default, default)],
            "local"));

        var persisted = service.GetWorkspace(workspace.WorkspaceId);
        Assert.Equal("before", persisted?.DisplayName);
        Assert.Empty(persisted?.Paths ?? []);
        Assert.Empty(persisted?.Documents ?? []);
        Assert.Empty(service.ListBindings(workspace.WorkspaceId));
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task AgentWorkspaceService_ConcurrentAggregateSavesNeverPersistMixedStages()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var service = new AgentWorkspaceService(store);
        var workspace = service.CreateWorkspace("initial");

        Task SaveAsync(string suffix) => Task.Run(() => service.SaveWorkspaceAggregate(
            workspace.WorkspaceId,
            $"workspace-{suffix}",
            suffix,
            [new AgentWorkspacePathRecord($"path-{suffix}", workspace.WorkspaceId, Path.Combine(scope.RootPath, suffix), true, 0, default, default)],
            [new AgentWorkspaceDocumentRecord($"doc-{suffix}", workspace.WorkspaceId, Path.Combine(scope.RootPath, $"{suffix}.md"), 0, default, default)],
            $"target-{suffix}"));

        await Task.WhenAll(SaveAsync("a"), SaveAsync("b"));

        var persisted = service.GetWorkspace(workspace.WorkspaceId)!;
        var suffix = persisted.DisplayName[^1].ToString();
        Assert.Equal(suffix, persisted.Description);
        Assert.EndsWith(suffix, Path.GetFileName(Assert.Single(persisted.Paths).HostPath));
        Assert.Equal($"{suffix}.md", Path.GetFileName(Assert.Single(persisted.Documents).FilePath));
        Assert.Equal($"target-{suffix}", Assert.Single(service.ListBindings(workspace.WorkspaceId)).ContributionId);
    }

    [Fact]
    public void AgentLocalStore_PreservesSessions_WhenStoreReopens()
    {
        using var scope = TestScope.Create();
        var firstStore = new AgentLocalStore(scope.Context);
        var workspace = CreateWorkspace();
        firstStore.SaveWorkspace(workspace);
        var session = firstStore.CreateSession("Persistent Session", workspaceId: workspace.WorkspaceId);

        var reopenedStore = new AgentLocalStore(scope.Context);

        var persistedSession = reopenedStore.GetSession(session.SessionId);
        Assert.NotNull(persistedSession);
        Assert.Equal(session.SessionId, persistedSession!.SessionId);
        Assert.Contains(reopenedStore.ListSessions(), item => item.SessionId == session.SessionId);
    }

    [Fact]
    public void AgentLocalStore_CreateSessionWithoutWorkspace_Throws()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);

        var exception = Assert.Throws<InvalidOperationException>(() => store.CreateSession("Unassigned Session"));

        Assert.Equal("Sessions must be created with an assigned workspace.", exception.Message);
        Assert.Null(store.GetWorkspace(AgentWorkspaceService.UnassignedSessionsWorkspaceId));
    }

    [Fact]
    public void AgentLocalStore_SaveCheckpoint_MarksFailedSessionFailed()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = CreateWorkspace();
        store.SaveWorkspace(workspace);
        var session = store.CreateSession("Failed Session", workspaceId: workspace.WorkspaceId);

        store.SaveCheckpoint(session.SessionId, 1, AgentRunStatus.Failed, "Provider stream failed.");

        Assert.Equal(AgentSessionState.Failed, store.GetSession(session.SessionId)?.State);
    }

    [Fact]
    public void AgentLocalStore_ListTurnsAfter_ReturnsForwardPage()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = CreateWorkspace();
        store.SaveWorkspace(workspace);
        var session = store.CreateSession("Forward Paging Session", workspaceId: workspace.WorkspaceId);
        for (var index = 0; index < 6; index++)
        {
            store.AppendTextTurn(session.SessionId, AgentMessageRole.User, $"turn-{index}");
        }

        var allTurns = store.ListTurns(session.SessionId)
            .OrderBy(turn => turn.CreatedAtUtc)
            .ThenBy(turn => turn.TurnId)
            .ToArray();
        var boundary = allTurns[2];

        var afterTurns = store.ListTurnsAfter(session.SessionId, boundary.CreatedAtUtc, boundary.TurnId, 2);

        Assert.Equal(
            allTurns.Skip(3).Take(2).Select(turn => turn.TurnId),
            afterTurns.Select(turn => turn.TurnId));
    }

    [Fact]
    public void AgentLocalStore_ProfileDeletion_IsIndependentFromWorkspaces()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var profile = CreateProfile("profile");
        store.SaveProfile(profile);
        store.SaveWorkspace(CreateWorkspace());

        store.DeleteProfile(profile.ProfileId);

        Assert.Null(store.GetProfile(profile.ProfileId));
        Assert.Single(store.ListWorkspaces());
    }

    [Fact]
    public void AgentLocalStore_DeleteWorkspace_RemovesSessions_WhenSessionsExist()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = CreateWorkspace();
        store.SaveWorkspace(workspace);
        var session = store.CreateSession("Workspace Session", workspaceId: workspace.WorkspaceId);

        store.DeleteWorkspace(workspace.WorkspaceId);

        Assert.Null(store.GetWorkspace(workspace.WorkspaceId));
        Assert.Null(store.GetSession(session.SessionId));
    }

    [Fact]
    public void AgentLocalStore_DeleteWorkspace_RemovesBindings_WhenNoSessionsExist()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);
        store.SaveWorkspace(workspace);
        store.SaveWorkspaceBinding(binding);

        store.DeleteWorkspace(workspace.WorkspaceId);

        Assert.Null(store.GetWorkspace(workspace.WorkspaceId));
        Assert.Empty(store.ListWorkspaceBindings(workspace.WorkspaceId));
    }

    [Fact]
    public void AgentLocalStore_GetTranscriptToolDetailUsesPersistedExecutionLinksWithoutHydratingPages()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store);
        var workspace = CreateWorkspace();
        var argumentsJson = JsonSerializer.Serialize(new { path = "src/App.cs" });
        var presentationJson = JsonSerializer.Serialize(new { schema = "younics.ai.agent.tools.files.diff.v1" });
        store.SaveWorkspace(workspace);
        var session = store.CreateSession("Tool detail", workspaceId: workspace.WorkspaceId);
        var callTurn = sessionService.AppendToolCallTurn(
            session.SessionId,
            AgentMessageRole.Assistant,
            "detail-call",
            "edit",
            argumentsJson);
        var resultTurn = sessionService.AppendToolResultTurn(
            session.SessionId,
            "detail-call",
            "edit",
            argumentsJson: null,
            content: "updated",
            resultSummary: "Updated src/App.cs.",
            structuredPayloadJson: "{\"changed\":true}",
            sourcesJson: "[]",
            wasTruncated: false,
            isError: false,
            errorCode: null,
            backendId: "local",
            presentationPayloadJson: presentationJson);
        var callItem = Assert.Single(callTurn.Items);
        var resultItem = Assert.Single(resultTurn.Items);
        var fromCallItem = store.GetTranscriptToolDetail(new AgentTranscriptToolDetailRequest(
            session.SessionId,
            ItemId: callItem.ItemId));
        var fromResultItem = store.GetTranscriptToolDetail(new AgentTranscriptToolDetailRequest(
            session.SessionId,
            ItemId: resultItem.ItemId));
        var fromExecutionId = store.GetTranscriptToolDetail(new AgentTranscriptToolDetailRequest(
            session.SessionId,
            ToolExecutionId: callItem.ToolExecutionId,
            CallId: "detail-call"));

        Assert.NotNull(fromCallItem);
        Assert.Equal(fromCallItem, fromResultItem);
        Assert.Equal(fromCallItem, fromExecutionId);
        Assert.Equal(callItem.ItemId, fromCallItem!.CallItemId);
        Assert.Equal(resultItem.ItemId, fromCallItem.ResultItemId);
        Assert.Equal(argumentsJson, fromCallItem.ArgumentsJson);
        Assert.Equal("updated", fromCallItem.OutputText);
        Assert.Equal("Updated src/App.cs.", fromCallItem.ResultSummary);
        Assert.Equal("{\"changed\":true}", fromCallItem.StructuredPayloadJson);
        Assert.Equal(presentationJson, fromCallItem.PresentationPayloadJson);
        Assert.Equal("local", fromCallItem.BackendId);
        Assert.Null(fromCallItem.RunId);
        Assert.Null(fromCallItem.RunRevision);
        Assert.Equal(2, fromCallItem.Revision);
        Assert.Null(store.GetTranscriptToolDetail(new AgentTranscriptToolDetailRequest(
            session.SessionId, CallId: "detail-call"))); // Provider text alone is not an invocation identity.
        Assert.Null(store.GetTranscriptToolDetail(new AgentTranscriptToolDetailRequest(
            Guid.NewGuid(),
            CallId: "detail-call")));
    }
}
