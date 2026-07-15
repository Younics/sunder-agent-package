using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Execution.Docker;
using Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Package.Agent.Storage;
using Sunder.Package.Agent.Tools.Files;
using Sunder.Package.Agent.Tools.Shell;
using Sunder.Package.Agent.Tools.Web;
using Sunder.Package.Agent.Tools.Web.Backends;
using Sunder.Package.Agent.Tools.Web.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class WorkspaceTests
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
    public async Task AgentWorkspaceStackContributor_ExportAsync_IncludesSelectedLocalPaths()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var service = new AgentWorkspaceService(store);
        var workspaceRoot = Path.Combine(scope.RootPath, "repo");
        var documentPath = Path.Combine(scope.RootPath, "project-guide.md");
        service.ImportWorkspace(
            new AgentWorkspaceRecord("workspace.stack", "Stack Workspace", "Private workspace notes.", default, default),
            [new AgentWorkspacePathRecord("root", "workspace.stack", workspaceRoot, IsDefault: true, 0, default, default)],
            [new AgentWorkspaceDocumentRecord("doc", "workspace.stack", documentPath, 0, default, default)]);
        service.SavePrimaryExecutionBinding("workspace.stack", "local");
        var contributor = new AgentWorkspaceStackContributor(service, scope.Context, new TestExtensionCatalog());

        var contribution = await contributor.ExportAsync(new StackExportRequest(["workspace.stack"]));

        var fragment = Assert.Single(contribution.Fragments);
        Assert.Equal("sunder.package.agent", Assert.Single(contribution.PackageRequirements).PackageId);
        Assert.Equal("sunder.package.agent.workspaces", contributor.ContributorId);
        Assert.Empty(fragment.RequiredInputs ?? []);
        Assert.Contains(workspaceRoot, fragment.JsonPayload, StringComparison.Ordinal);
        Assert.Contains(documentPath, fragment.JsonPayload, StringComparison.Ordinal);
        Assert.Contains("Stack Workspace", fragment.JsonPayload, StringComparison.Ordinal);
        Assert.Contains("local", fragment.JsonPayload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentWorkspaceStackContributor_ImportAsync_CreatesWorkspaceFromPublicPaths()
    {
        using var scope = TestScope.Create();
        var sourceContext = new TestPackageContext(Path.Combine(scope.RootPath, "source"));
        var sourceService = new AgentWorkspaceService(new AgentLocalStore(sourceContext));
        sourceService.ImportWorkspace(
            new AgentWorkspaceRecord("workspace.stack", "Stack Workspace", "Workspace docs.", default, default),
            [new AgentWorkspacePathRecord("root", "workspace.stack", Path.Combine(scope.RootPath, "source-repo"), IsDefault: true, 0, default, default)],
            [new AgentWorkspaceDocumentRecord("doc", "workspace.stack", Path.Combine(scope.RootPath, "source-guide.md"), 0, default, default)]);
        sourceService.SavePrimaryExecutionBinding("workspace.stack", "local");
        var sourceContributor = new AgentWorkspaceStackContributor(sourceService, sourceContext, new TestExtensionCatalog());
        var sourceRoot = Path.Combine(scope.RootPath, "source-repo");
        var sourceDocumentPath = Path.Combine(scope.RootPath, "source-guide.md");
        var fragment = Assert.Single((await sourceContributor.ExportAsync(new StackExportRequest(["workspace.stack"]))).Fragments);

        var targetContext = new TestPackageContext(Path.Combine(scope.RootPath, "target"));
        var targetService = new AgentWorkspaceService(new AgentLocalStore(targetContext));
        var targetContributor = new AgentWorkspaceStackContributor(targetService, targetContext, new TestExtensionCatalog());
        var importFragment = ToImportFragment(fragment, sourceContributor.ContributorId);
        var preview = await targetContributor.PreviewImportAsync(new StackImportPreviewRequest(
            [importFragment],
            new Dictionary<string, string>(),
            new Dictionary<string, string>()));
        var action = Assert.Single(preview.Actions);
        Assert.Empty(preview.RequiredInputs);

        var result = await targetContributor.ImportAsync(new StackImportRequest(
            [importFragment],
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            [action.ActionId]));

        Assert.Equal(StackImportOutcome.Completed, result.Outcome);
        var imported = targetService.GetWorkspace("workspace.stack");
        Assert.NotNull(imported);
        Assert.Equal("Stack Workspace", imported!.DisplayName);
        Assert.Equal("Workspace docs.", imported.Description);
        Assert.Equal(Path.GetFullPath(sourceRoot), Assert.Single(imported.Paths).HostPath);
        Assert.Equal(Path.GetFullPath(sourceDocumentPath), Assert.Single(imported.Documents).FilePath);
        Assert.Equal("local", Assert.Single(targetService.ListBindings("workspace.stack")).ContributionId);
    }

    [Fact]
    public async Task WorkspaceDocumentationContextService_ContributeAsync_LoadsExplicitAndAutoDocs()
    {
        using var scope = TestScope.Create();
        var workspaceRoot = Path.Combine(scope.RootPath, "repo");
        var docsRoot = Path.Combine(workspaceRoot, ".sunder", "docs");
        var explicitDocumentPath = Path.Combine(scope.RootPath, "project-guide.md");
        var autoDocumentPath = Path.Combine(docsRoot, "guide.md");
        Directory.CreateDirectory(docsRoot);
        File.WriteAllText(explicitDocumentPath, "Explicit project guidance.");
        File.WriteAllText(autoDocumentPath, "Auto workspace guidance.");
        var now = DateTimeOffset.UtcNow;
        var workspace = new AgentWorkspaceRecord(
            "workspace.docs",
            "Docs Workspace",
            null,
            now,
            now,
            [new AgentWorkspacePathRecord("path", "workspace.docs", workspaceRoot, true, 0, now, now)],
            [new AgentWorkspaceDocumentRecord("doc", "workspace.docs", explicitDocumentPath, 0, now, now)]);
        var service = new WorkspaceDocumentationContextService();

        var blocks = await service.ContributeAsync(CreateSystemPromptRequest(workspace));

        var block = Assert.Single(blocks);
        Assert.Equal("workspace-documentation", block.BlockId);
        Assert.Contains("project-guide.md", block.Content, StringComparison.Ordinal);
        Assert.Contains("Explicit project guidance.", block.Content, StringComparison.Ordinal);
        Assert.Contains("guide.md", block.Content, StringComparison.Ordinal);
        Assert.Contains("Auto workspace guidance.", block.Content, StringComparison.Ordinal);
        Assert.Contains($"Workspace path scope: {Path.GetFullPath(workspaceRoot).Replace(Path.DirectorySeparatorChar, '/')}", block.Content, StringComparison.Ordinal);
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
    public void AgentSessionService_CreateRootSessionWithoutWorkspace_Throws()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store);

        var exception = Assert.Throws<InvalidOperationException>(() => sessionService.CreateSession("Root Session"));

        Assert.Equal("Root sessions must be created with an explicit workspace id.", exception.Message);
    }

    [Fact]
    public void AgentSessionService_CreateRootSessionInUnassignedWorkspace_Throws()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store);

        var exception = Assert.Throws<InvalidOperationException>(() => sessionService.CreateSession(
            "Root Session",
            workspaceId: AgentWorkspaceService.UnassignedSessionsWorkspaceId));

        Assert.Equal("Root sessions cannot be created in Unassigned Sessions.", exception.Message);
    }

    [Fact]
    public void AgentSessionService_CreateChildSessionWithoutWorkspace_InheritsParentWorkspace()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store);
        var workspace = CreateWorkspace();
        store.SaveWorkspace(workspace);
        var parent = sessionService.CreateSession("Parent Session", workspaceId: workspace.WorkspaceId);

        var child = sessionService.CreateSession("Child Session", parentSessionId: parent.SessionId);

        Assert.Equal(workspace.WorkspaceId, child.WorkspaceId);
        Assert.Equal(workspace.WorkspaceId, store.GetSession(child.SessionId)?.WorkspaceId);
    }

    [Fact]
    public void AgentSessionService_CreateChildSessionWithDifferentWorkspace_Throws()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store);
        var now = DateTimeOffset.UtcNow;
        var parentWorkspace = new AgentWorkspaceRecord(Guid.NewGuid().ToString("N"), "Parent Workspace", null, now, now);
        var otherWorkspace = new AgentWorkspaceRecord(Guid.NewGuid().ToString("N"), "Other Workspace", null, now, now);
        store.SaveWorkspace(parentWorkspace);
        store.SaveWorkspace(otherWorkspace);
        var parent = sessionService.CreateSession("Parent Session", workspaceId: parentWorkspace.WorkspaceId);

        var exception = Assert.Throws<InvalidOperationException>(() => sessionService.CreateSession(
            "Child Session",
            parentSessionId: parent.SessionId,
            workspaceId: otherWorkspace.WorkspaceId));

        Assert.Equal("Child sessions must use their parent session workspace.", exception.Message);
    }

    [Fact]
    public void AgentSessionService_UpdateSessionRejectsBlankWorkspace()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store);
        var workspace = CreateWorkspace();
        store.SaveWorkspace(workspace);
        var session = sessionService.CreateSession("Workspace Session", workspaceId: workspace.WorkspaceId);

        var exception = Assert.Throws<InvalidOperationException>(() => sessionService.UpdateSession(session with
        {
            WorkspaceId = " ",
        }));

        Assert.Equal("Sessions must have an assigned workspace.", exception.Message);
        Assert.Equal(workspace.WorkspaceId, store.GetSession(session.SessionId)?.WorkspaceId);
    }

    [Fact]
    public void AgentSessionService_UpdateSessionRejectsMoveToUnassignedWorkspace()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store);
        var workspace = CreateWorkspace();
        store.SaveWorkspace(workspace);
        var session = sessionService.CreateSession("Workspace Session", workspaceId: workspace.WorkspaceId);

        var exception = Assert.Throws<InvalidOperationException>(() => sessionService.UpdateSession(session with
        {
            WorkspaceId = AgentWorkspaceService.UnassignedSessionsWorkspaceId,
        }));

        Assert.Equal("Sessions cannot be moved to Unassigned Sessions.", exception.Message);
        Assert.Equal(workspace.WorkspaceId, store.GetSession(session.SessionId)?.WorkspaceId);
    }

    [Fact]
    public void AgentSessionService_UpdateSessionRejectsMoveBetweenWorkspaces()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store);
        var now = DateTimeOffset.UtcNow;
        var firstWorkspace = new AgentWorkspaceRecord(Guid.NewGuid().ToString("N"), "First Workspace", null, now, now);
        var secondWorkspace = new AgentWorkspaceRecord(Guid.NewGuid().ToString("N"), "Second Workspace", null, now, now);
        store.SaveWorkspace(firstWorkspace);
        store.SaveWorkspace(secondWorkspace);
        var session = sessionService.CreateSession("Workspace Session", workspaceId: firstWorkspace.WorkspaceId);

        var exception = Assert.Throws<InvalidOperationException>(() => sessionService.UpdateSession(session with
        {
            WorkspaceId = secondWorkspace.WorkspaceId,
        }));

        Assert.Equal("Sessions cannot be moved between workspaces.", exception.Message);
        Assert.Equal(firstWorkspace.WorkspaceId, store.GetSession(session.SessionId)?.WorkspaceId);
    }

    [Fact]
    public void AgentLocalStore_PreservesLegacySessionWorkspaceColumn()
    {
        using var scope = TestScope.Create();
        var databasePath = scope.Context.Storage.RoleLocalWorkspace.GetLocalPath("agent/agent.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE AgentSessions (
                    SessionId TEXT PRIMARY KEY,
                    WorkspaceId TEXT NOT NULL,
                    Title TEXT NOT NULL,
                    State TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );

                INSERT INTO AgentSessions (SessionId, WorkspaceId, Title, State, CreatedAtUtc, UpdatedAtUtc)
                VALUES ($sessionId, 'legacy-workspace', 'Legacy Session', 'Active', $created, $updated);
                """;
            command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            command.Parameters.AddWithValue("$created", now);
            command.Parameters.AddWithValue("$updated", now);
            command.ExecuteNonQuery();
        }

        var store = new AgentLocalStore(scope.Context);

        var session = store.GetSession(sessionId);
        Assert.NotNull(session);
        Assert.Equal("Legacy Session", session!.Title);
        Assert.Equal("legacy-workspace", session.WorkspaceId);
        Assert.Contains(GetTableColumns(databasePath, "AgentSessions"), column => string.Equals(column, "WorkspaceId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AgentLocalStore_AddsMissingSessionWorkspaceColumn()
    {
        using var scope = TestScope.Create();
        var databasePath = scope.Context.Storage.RoleLocalWorkspace.GetLocalPath("agent/agent.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE AgentSessions (
                    SessionId TEXT PRIMARY KEY,
                    Title TEXT NOT NULL,
                    State TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );

                INSERT INTO AgentSessions (SessionId, Title, State, CreatedAtUtc, UpdatedAtUtc)
                VALUES ($sessionId, 'Legacy Session', 'Active', $created, $updated);
                """;
            command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            command.Parameters.AddWithValue("$created", now);
            command.Parameters.AddWithValue("$updated", now);
            command.ExecuteNonQuery();
        }

        var store = new AgentLocalStore(scope.Context);

        var session = store.GetSession(sessionId);
        Assert.NotNull(session);
        Assert.Equal("Legacy Session", session!.Title);
        Assert.Equal(AgentWorkspaceService.UnassignedSessionsWorkspaceId, session.WorkspaceId);
        Assert.Contains(GetTableColumns(databasePath, "AgentSessions"), column => string.Equals(column, "WorkspaceId", StringComparison.OrdinalIgnoreCase));

        var workspace = store.GetWorkspace(AgentWorkspaceService.UnassignedSessionsWorkspaceId);
        Assert.NotNull(workspace);
        Assert.Equal(AgentWorkspaceService.UnassignedSessionsWorkspaceDisplayName, workspace!.DisplayName);
    }

    [Fact]
    public void AgentLocalStore_MigratesActiveSessionWithFailedLatestCheckpoint()
    {
        using var scope = TestScope.Create();
        var databasePath = scope.Context.Storage.RoleLocalWorkspace.GetLocalPath("agent/agent.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE AgentSessions (
                    SessionId TEXT PRIMARY KEY,
                    Title TEXT NOT NULL,
                    State TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                CREATE TABLE AgentRunCheckpoints (
                    CheckpointId TEXT PRIMARY KEY,
                    SessionId TEXT NOT NULL,
                    RunRevision INTEGER NOT NULL,
                    Status TEXT NOT NULL,
                    Summary TEXT NULL,
                    CreatedAtUtc TEXT NOT NULL
                );
                INSERT INTO AgentSessions (SessionId, Title, State, CreatedAtUtc, UpdatedAtUtc)
                VALUES ($sessionId, 'Failed Legacy Session', 'Active', $createdAtUtc, $createdAtUtc);
                INSERT INTO AgentRunCheckpoints (CheckpointId, SessionId, RunRevision, Status, Summary, CreatedAtUtc)
                VALUES ($checkpointId, $sessionId, 1, 'Failed', 'Provider stream failed.', $createdAtUtc);
                """;
            command.Parameters.AddWithValue("$checkpointId", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            command.Parameters.AddWithValue("$createdAtUtc", now);
            command.ExecuteNonQuery();
        }

        var reopenedStore = new AgentLocalStore(scope.Context);

        Assert.Equal(AgentSessionState.Failed, reopenedStore.GetSession(sessionId)?.State);
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
    public void AgentSessionService_SaveCheckpoint_IgnoresThrowingSessionChangedSubscriber()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store);
        var workspace = CreateWorkspace();
        store.SaveWorkspace(workspace);
        var session = sessionService.CreateSession("Event Isolation Session", workspaceId: workspace.WorkspaceId);
        var observed = false;
        sessionService.SessionChanged += _ => throw new InvalidOperationException("Subscriber failed.");
        sessionService.SessionChanged += _ => observed = true;

        var checkpoint = sessionService.SaveCheckpoint(session.SessionId, 1, AgentRunStatus.Running, "Still running.");

        Assert.Equal(AgentRunStatus.Running, checkpoint.Status);
        Assert.True(observed);
    }

    [Fact]
    public void AgentSessionService_AppendTextTurn_IgnoresThrowingTurnChangedSubscriber()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store);
        var workspace = CreateWorkspace();
        store.SaveWorkspace(workspace);
        var session = sessionService.CreateSession("Turn Event Isolation Session", workspaceId: workspace.WorkspaceId);
        var observed = false;
        sessionService.TurnChanged += (_, _) => throw new InvalidOperationException("Subscriber failed.");
        sessionService.TurnChanged += (_, _) => observed = true;

        var turn = sessionService.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, "hello");

        Assert.Equal(AgentMessageRole.Assistant, turn.Role);
        Assert.True(observed);
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
    public void AgentSessionService_AppendToolResultTurn_PersistsArgumentsJson()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store);
        var workspace = CreateWorkspace();
        var argumentsJson = JsonSerializer.Serialize(new { pattern = "*.html" });
        store.SaveWorkspace(workspace);
        var session = store.CreateSession("Tool Args", workspaceId: workspace.WorkspaceId);

        sessionService.AppendToolResultTurn(
            session.SessionId,
            "call-1",
            "glob",
            argumentsJson,
            "index.html",
            "Found 1 match",
            structuredPayloadJson: null,
            sourcesJson: null,
            wasTruncated: false,
            isError: false,
            errorCode: null,
            backendId: null);

        var resultItem = Assert.Single(store.ListTurns(session.SessionId).SelectMany(turn => turn.Items), item => item.Kind == AgentTurnItemKind.ToolResult);
        Assert.Equal(argumentsJson, resultItem.ArgumentsJson);
    }

    [Fact]
    public void AgentSessionService_AppendToolResultTurn_PersistsPresentationPayloadJson()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store);
        var workspace = CreateWorkspace();
        var payloadJson = JsonSerializer.Serialize(new { schema = "sunder.file-diff.v1" });
        store.SaveWorkspace(workspace);
        var session = store.CreateSession("Tool Payload", workspaceId: workspace.WorkspaceId);

        sessionService.AppendToolResultTurn(
            session.SessionId,
            "call-1",
            "edit",
            argumentsJson: null,
            content: "Wrote 42 character(s).",
            resultSummary: "Wrote 42 character(s).",
            structuredPayloadJson: null,
            sourcesJson: null,
            wasTruncated: false,
            isError: false,
            errorCode: null,
            backendId: null,
            presentationPayloadJson: payloadJson);

        var reopenedStore = new AgentLocalStore(scope.Context);
        var resultItem = Assert.Single(reopenedStore.ListTurns(session.SessionId).SelectMany(turn => turn.Items), item => item.Kind == AgentTurnItemKind.ToolResult);
        Assert.Equal(payloadJson, resultItem.PresentationPayloadJson);
    }

    [Fact]
    public async Task AgentToolService_AllowsMcpToolGroupAssignmentBySourceKind()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var catalog = new TestExtensionCatalog();
        var sessionService = new AgentSessionService(store);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog);
        var toolDescriptor = new AgentToolDescriptor(
            "stitch_create_project",
            "Stitch: Create Project",
            "Create a Stitch project.",
            SourceKind: "mcp",
            SourceId: "stitch-server-id",
            SourceDisplayName: "stitch",
            SelectionScope: AgentToolSelectionScope.Group,
            SelectionGroupId: "stitch-server-id",
            SelectionGroupDisplayName: "stitch");
        catalog.AddExtension(PackageExtensionPoints.ToolSources, new StaticToolSource("mcp", "mcp", "Model Context Protocol", [toolDescriptor]));
        var toolService = new AgentToolService(new InstalledPackageToolSource(catalog), sessionService, workspaceService, executionTargetService, catalog);
        var profile = CreateProfile("profile") with
        {
            SelectableCapabilityAssignments =
            [
                new AgentProfileSelectableCapabilityAssignmentRecord(
                    AgentProfileSelectableCapabilityKinds.ToolGroup,
                    "stitch-server-id",
                    "mcp")
            ]
        };
        var workspace = workspaceService.CreateWorkspace("MCP Workspace");
        var session = sessionService.CreateSession("MCP Session", workspaceId: workspace.WorkspaceId);

        var tools = await toolService.ListReadyRuntimeToolsAsync(profile, session.SessionId, workspace);

        Assert.Contains(tools, tool => string.Equals(tool.Descriptor.ToolId, "stitch_create_project", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalExecutionTarget_RejectsPathEscapes()
    {
        using var scope = TestScope.Create();
        var root = Path.Combine(scope.RootPath, "workspace");
        Directory.CreateDirectory(root);
        var configService = new LocalExecutionWorkspaceConfigService(scope.Context);
        var shellCatalogService = new LocalShellCatalogService(scope.Context);
        var target = new LocalExecutionTarget(scope.Context, configService, shellCatalogService);
        var (workspace, binding) = await CreateLocalWorkspaceAsync(root, configService);

        var result = await target.ReadFileAsync(
            new AgentExecutionTargetContext(null, null, workspace, binding),
            new AgentFileReadRequest(Path.Combine(scope.RootPath, "outside.txt")));

        Assert.True(result.IsError);
        Assert.Equal(AgentFileReadErrorCodes.OutsideConfiguredScope, result.ErrorCode);
        Assert.Empty(result.Content);
    }

    [Fact]
    public async Task LocalExecutionTarget_RejectsBinaryReads()
    {
        using var scope = TestScope.Create();
        var root = Path.Combine(scope.RootPath, "workspace");
        Directory.CreateDirectory(root);
        var binaryPath = Path.Combine(root, "binary.bin");
        await File.WriteAllBytesAsync(binaryPath, [1, 2, 0, 4]);
        var configService = new LocalExecutionWorkspaceConfigService(scope.Context);
        var shellCatalogService = new LocalShellCatalogService(scope.Context);
        var target = new LocalExecutionTarget(scope.Context, configService, shellCatalogService);
        var (workspace, binding) = await CreateLocalWorkspaceAsync(root, configService);

        var result = await target.ReadFileAsync(
            new AgentExecutionTargetContext(null, null, workspace, binding),
            new AgentFileReadRequest(binaryPath));

        Assert.True(result.IsError);
        Assert.Equal(AgentFileReadErrorCodes.BinaryFile, result.ErrorCode);
        Assert.Empty(result.Content);
    }

    [Fact]
    public void LocalProcessEnvironment_BuildEffectivePathEntries_AddsMacOsFallbacksAfterInheritedPath()
    {
        var entries = LocalProcessEnvironment.BuildEffectivePathEntries(
            ["/custom/bin"],
            "/usr/bin:/bin",
            "/Users/fredy",
            isWindows: false,
            isMacOS: true).ToArray();

        Assert.Equal("/custom/bin", entries[0]);
        Assert.True(Array.IndexOf(entries, "/opt/homebrew/bin") > Array.IndexOf(entries, "/bin"));
        Assert.Contains("/Users/fredy/.dotnet", entries);
        Assert.Contains("/Users/fredy/.dotnet/tools", entries);
        Assert.Contains("/usr/local/share/dotnet", entries);
        Assert.Contains("/opt/homebrew/bin", entries);

        var resolution = ExecutableResolver.Resolve(
            "dotnet",
            entries,
            path => string.Equals(path, "/opt/homebrew/bin/dotnet", StringComparison.Ordinal),
            isWindows: false);

        Assert.True(resolution.WasResolved);
        Assert.Equal("/opt/homebrew/bin/dotnet", resolution.FileName);
        Assert.Contains("/opt/homebrew/bin/dotnet", resolution.CheckedLocations);
    }

    [Fact]
    public async Task LocalExecutionTarget_ExecuteProcessAsync_ReportsMissingExecutableInsteadOfWorkingDirectory()
    {
        using var scope = TestScope.Create();
        var root = Path.Combine(scope.RootPath, "workspace");
        Directory.CreateDirectory(root);
        var configService = new LocalExecutionWorkspaceConfigService(scope.Context);
        var shellCatalogService = new LocalShellCatalogService(scope.Context);
        var target = new LocalExecutionTarget(scope.Context, configService, shellCatalogService);
        var (workspace, binding) = await CreateLocalWorkspaceAsync(root, configService);
        const string missingExecutable = "sunder-missing-executable-for-test";

        var result = await target.ExecuteProcessAsync(
            new AgentExecutionTargetContext(null, null, workspace, binding),
            new AgentProcessCommandRequest(missingExecutable, []));

        Assert.Equal(127, result.ExitCode);
        Assert.Contains($"Executable '{missingExecutable}' was not found", result.Output);
        Assert.Contains("Checked PATH locations", result.Output);
        Assert.DoesNotContain("Working directory does not exist", result.Output);
    }

    [Fact]
    public async Task DockerExecutionWorkspaceConfigService_GetConfig_UsesAgentZeroDefaults()
    {
        using var scope = TestScope.Create();
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);

        var config = await configService.GetConfigAsync("workspace:primary-execution-target");

        Assert.Equal("agent0ai/agent-zero:latest", config.ImageReference);
        Assert.Equal(DockerExecutionWorkspaceConfigService.BuildContainerName("workspace:primary-execution-target"), config.ContainerName);
        Assert.Equal("/bin/sh", config.ShellPath);
        Assert.Empty(config.PathEntries ?? []);
    }

    [Fact]
    public async Task DockerExecutionWorkspaceConfigService_GetConfig_UsesBindingScopedDefaultContainerNames()
    {
        using var scope = TestScope.Create();
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);

        var first = await configService.GetConfigAsync("workspace-one:primary-execution-target");
        var second = await configService.GetConfigAsync("workspace-two:primary-execution-target");

        Assert.StartsWith("sunder-agent-", first.ContainerName);
        Assert.StartsWith("sunder-agent-", second.ContainerName);
        Assert.NotEqual(first.ContainerName, second.ContainerName);
    }

    [Fact]
    public async Task DockerImageCatalogService_SeedsDefaultOnlyUntilUserDeletesIt()
    {
        using var scope = TestScope.Create();
        var imageCatalog = new DockerImageCatalogService(scope.Context);

        var images = await imageCatalog.ListImagesAsync();
        Assert.Single(images);
        Assert.Equal("agent0ai/agent-zero:latest", images[0].ImageReference);

        await imageCatalog.DeleteImageAsync("agent0ai/agent-zero:latest");

        Assert.Empty(await imageCatalog.ListImagesAsync());
        Assert.Empty(await new DockerImageCatalogService(scope.Context).ListImagesAsync());
        Assert.Null((await new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog).GetConfigAsync("workspace:primary-execution-target")).ImageReference);
    }

    [Fact]
    public async Task DockerImageStackContributor_ExportAsync_ExportsSelectedImageReferencesOnly()
    {
        using var scope = TestScope.Create();
        var imageCatalog = new DockerImageCatalogService(scope.Context);
        await imageCatalog.SaveImagesAsync(
        [
            new DockerImageDefinition("custom:latest", DockerImageStatus.Ready, DateTimeOffset.UtcNow, "ready"),
            new DockerImageDefinition("other:latest", DockerImageStatus.NotPulled, null, null),
        ]);
        var contributor = new DockerImageStackContributor(imageCatalog, scope.Context);

        var contribution = await contributor.ExportAsync(new StackExportRequest(
            ["docker-images"],
            [new StackExportItemSelection("docker-images",
            [
                new StackExportDetailSelection("custom:latest"),
                new StackExportDetailSelection("other:latest", IsSelected: false),
            ])]));

        var fragment = Assert.Single(contribution.Fragments);
        Assert.Equal("docker-images", fragment.FragmentId);
        Assert.Contains("custom:latest", fragment.JsonPayload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("other:latest", fragment.JsonPayload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ready", fragment.JsonPayload, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(scope.Context.PackageId, Assert.Single(contribution.PackageRequirements).PackageId);
    }

    [Fact]
    public async Task DockerImageStackContributor_ImportAsync_ConfiguresReferencesWithoutMarkingReady()
    {
        using var sourceScope = TestScope.Create();
        var sourceCatalog = new DockerImageCatalogService(sourceScope.Context);
        await sourceCatalog.SaveImagesAsync([new DockerImageDefinition("custom:latest", DockerImageStatus.Ready, DateTimeOffset.UtcNow, "ready")]);
        var sourceContributor = new DockerImageStackContributor(sourceCatalog, sourceScope.Context);
        var fragment = Assert.Single((await sourceContributor.ExportAsync(new StackExportRequest(["docker-images"]))).Fragments);

        using var targetScope = TestScope.Create();
        var targetCatalog = new DockerImageCatalogService(targetScope.Context);
        await targetCatalog.DeleteImageAsync(DockerExecutionWorkspaceConfigService.DefaultImageReference);
        var targetContributor = new DockerImageStackContributor(targetCatalog, targetScope.Context);
        var importFragment = ToImportFragment(fragment, sourceContributor.ContributorId);
        var preview = await targetContributor.PreviewImportAsync(new StackImportPreviewRequest(
            [importFragment],
            new Dictionary<string, string>(),
            new Dictionary<string, string>()));
        var action = Assert.Single(preview.Actions);

        var result = await targetContributor.ImportAsync(new StackImportRequest(
            [importFragment],
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            [action.ActionId]));

        Assert.Equal(StackImportOutcome.Completed, result.Outcome);
        var image = Assert.Single(await targetCatalog.ListImagesAsync());
        Assert.Equal("custom:latest", image.ImageReference);
        Assert.Equal(DockerImageStatus.NotPulled, image.Status);
    }

    [Fact]
    public async Task DockerImageCatalogService_PullImage_ReportsProgressAndMarksReady()
    {
        using var scope = TestScope.Create();
        var calls = new List<IReadOnlyList<string>>();
        var progressLines = new List<string>();
        var runner = new FakeDockerCliRunner(scope.Context, (args, _, _, _, progress) =>
        {
            calls.Add(args.ToArray());
            if (args.Count >= 2 && string.Equals(args[0], "pull", StringComparison.Ordinal))
            {
                progress?.Report("pulling layer");
            }

            return Task.FromResult(new DockerCliRunResult(0, "ok", TimedOut: false, WasTruncated: false));
        });
        var imageCatalog = new DockerImageCatalogService(scope.Context, runner);

        var result = await imageCatalog.PullImageAsync("custom:latest", new DelegateProgress(progressLines.Add));

        Assert.True(result.Success, result.Message);
        Assert.Equal(DockerImageStatus.Ready, (await imageCatalog.ListImagesAsync()).Single(image => image.ImageReference == "custom:latest").Status);
        Assert.Contains(progressLines, line => line.Contains("pulling layer", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(calls, args => args.SequenceEqual(["pull", "custom:latest"]));
        Assert.Contains(calls, args => args.SequenceEqual(["image", "inspect", "custom:latest"]));
    }

    [Fact]
    public async Task DockerExecutionTarget_WriteFileAsync_StreamsContentThroughStdin()
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var dockerCalls = new List<(IReadOnlyList<string> Args, string? StandardInput)>();
        var runner = new FakeDockerCliRunner(scope.Context, (args, _, _, standardInput, _) =>
        {
            dockerCalls.Add((args.ToArray(), standardInput));
            var exitCode = args.Count > 0 && string.Equals(args[0], "inspect", StringComparison.Ordinal) ? 1 : 0;
            var output = args.Contains(DockerFileOperationScript.Content, StringComparer.Ordinal)
                ? $"{DockerFileOperationScript.Protocol}|ok|file-written\n"
                : string.Empty;
            return Task.FromResult(new DockerCliRunResult(exitCode, output, TimedOut: false, WasTruncated: false));
        });
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var target = new DockerExecutionTarget(scope.Context, configService, lifecycle, dockerCliRunner: runner);
        var (workspace, _) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(
            binding.BindingId,
            new DockerExecutionWorkspaceConfig("test-image:latest", "sunder-agent-test", "/bin/sh"));
        var content = string.Join("\n", Enumerable.Range(0, 5000).Select(index => $"line-{index}"));
        var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));

        var result = await target.WriteFileAsync(
            new AgentExecutionTargetContext(null, null, workspace, binding),
            new AgentFileWriteRequest("app/page.tsx", content));

        Assert.False(result.IsError, result.Summary);
        var execCall = dockerCalls.Last(call => call.Args.Count > 0 && string.Equals(call.Args[0], "exec", StringComparison.Ordinal));
        Assert.Equal("-i", execCall.Args[1]);
        Assert.DoesNotContain("-w", execCall.Args);
        var commandLine = string.Join(" ", execCall.Args);
        Assert.DoesNotContain(content, commandLine, StringComparison.Ordinal);
        Assert.DoesNotContain(base64, commandLine, StringComparison.Ordinal);
        Assert.Equal(content, execCall.StandardInput);
    }

    [Fact]
    public async Task DockerExecutionTarget_ReadFileAsync_ReportsMissingFileLikeLocal()
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var runner = new FakeDockerCliRunner(scope.Context, (args, _, _, _, _) =>
        {
            if (args.Count > 0 && string.Equals(args[0], "inspect", StringComparison.Ordinal))
            {
                return Task.FromResult(new DockerCliRunResult(1, string.Empty, TimedOut: false, WasTruncated: false));
            }

            if (args.Count > 0 && string.Equals(args[0], "exec", StringComparison.Ordinal))
            {
                return Task.FromResult(new DockerCliRunResult(74, $"{DockerFileOperationScript.Protocol}|error|{AgentFileReadErrorCodes.FileNotFound}\n", TimedOut: false, WasTruncated: false));
            }

            return Task.FromResult(new DockerCliRunResult(0, string.Empty, TimedOut: false, WasTruncated: false));
        });
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var target = new DockerExecutionTarget(scope.Context, configService, lifecycle, dockerCliRunner: runner);
        var (workspace, _) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(binding.BindingId, new DockerExecutionWorkspaceConfig("test-image:latest", "sunder-agent-test", "/bin/sh"));
        var containerRoot = (await target.GetExecutionScopeAsync(new AgentExecutionTargetContext(null, null, workspace, binding))).DefaultWorkingDirectory!;

        var result = await target.ReadFileAsync(
            new AgentExecutionTargetContext(null, null, workspace, binding),
            new AgentFileReadRequest("missing.txt"));

        Assert.False(result.IsDirectory);
        Assert.Equal($"{containerRoot}/missing.txt", result.Path);
        Assert.True(result.IsError);
        Assert.Equal(AgentFileReadErrorCodes.FileNotFound, result.ErrorCode);
        Assert.Equal($"File not found: {containerRoot}/missing.txt", result.ErrorMessage);
        Assert.Empty(result.Content);
    }

    [Fact]
    public async Task DockerExecutionTarget_ReadFileAsync_RejectsBinaryFilesLikeLocal()
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var runner = new FakeDockerCliRunner(scope.Context, (args, _, _, _, _) =>
        {
            if (args.Count > 0 && string.Equals(args[0], "inspect", StringComparison.Ordinal))
            {
                return Task.FromResult(new DockerCliRunResult(1, string.Empty, TimedOut: false, WasTruncated: false));
            }

            if (args.Count > 0 && string.Equals(args[0], "exec", StringComparison.Ordinal))
            {
                return Task.FromResult(new DockerCliRunResult(75, $"{DockerFileOperationScript.Protocol}|error|{AgentFileReadErrorCodes.BinaryFile}\n", TimedOut: false, WasTruncated: false));
            }

            return Task.FromResult(new DockerCliRunResult(0, string.Empty, TimedOut: false, WasTruncated: false));
        });
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var target = new DockerExecutionTarget(scope.Context, configService, lifecycle, dockerCliRunner: runner);
        var (workspace, _) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(binding.BindingId, new DockerExecutionWorkspaceConfig("test-image:latest", "sunder-agent-test", "/bin/sh"));

        var result = await target.ReadFileAsync(
            new AgentExecutionTargetContext(null, null, workspace, binding),
            new AgentFileReadRequest("image.png"));

        Assert.True(result.IsError);
        Assert.Equal(AgentFileReadErrorCodes.BinaryFile, result.ErrorCode);
        Assert.Empty(result.Content);
    }

    [Fact]
    public async Task DockerExecutionTarget_DeleteFileAsync_ReportsMissingPathLikeLocal()
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var runner = new FakeDockerCliRunner(scope.Context, (args, _, _, _, _) =>
        {
            if (args.Count > 0 && string.Equals(args[0], "inspect", StringComparison.Ordinal))
            {
                return Task.FromResult(new DockerCliRunResult(1, string.Empty, TimedOut: false, WasTruncated: false));
            }

            if (args.Count > 0 && string.Equals(args[0], "exec", StringComparison.Ordinal))
            {
                return Task.FromResult(new DockerCliRunResult(74, $"{DockerFileOperationScript.Protocol}|error|path-not-found\n", TimedOut: false, WasTruncated: false));
            }

            return Task.FromResult(new DockerCliRunResult(0, string.Empty, TimedOut: false, WasTruncated: false));
        });
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var target = new DockerExecutionTarget(scope.Context, configService, lifecycle, dockerCliRunner: runner);
        var (workspace, _) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(binding.BindingId, new DockerExecutionWorkspaceConfig("test-image:latest", "sunder-agent-test", "/bin/sh"));
        var containerRoot = (await target.GetExecutionScopeAsync(new AgentExecutionTargetContext(null, null, workspace, binding))).DefaultWorkingDirectory!;

        var result = await target.DeleteFileAsync(
            new AgentExecutionTargetContext(null, null, workspace, binding),
            new AgentFileDeleteRequest("missing.txt"));

        Assert.True(result.IsError);
        Assert.Equal("path-not-found", result.ErrorCode);
        Assert.Equal("Path does not exist.", result.Summary);
        Assert.Equal($"{containerRoot}/missing.txt", result.Path);
    }

    [Fact]
    public async Task DockerCli_CreateStartInfo_OnlySetsInputEncodingWhenInputIsRedirected()
    {
        using var scope = TestScope.Create();
        ConfigureFakeDockerCli(scope);

        var runStartInfo = await DockerCli.CreateStartInfoAsync(scope.Context, ["run", "image"], redirectStandardInput: false);
        var writeStartInfo = await DockerCli.CreateStartInfoAsync(scope.Context, ["exec", "-i", "container"], redirectStandardInput: true);

        Assert.False(runStartInfo.RedirectStandardInput);
        Assert.Null(runStartInfo.StandardInputEncoding);
        Assert.True(writeStartInfo.RedirectStandardInput);
        Assert.NotNull(writeStartInfo.StandardInputEncoding);
    }

    [Fact]
    public void DockerCli_ResolveExecutable_FindsFallback_WhenInheritedPathOmitsDocker()
    {
        var fallbackPath = "/opt/homebrew/bin/docker";
        var pathValue = string.Join(Path.PathSeparator, "/usr/bin", "/bin");

        var resolution = DockerCli.ResolveExecutable(
            configuredPath: null,
            environmentPath: null,
            pathValue,
            [fallbackPath],
            path => string.Equals(path, fallbackPath, StringComparison.Ordinal),
            isWindows: false);

        Assert.Equal(fallbackPath, resolution.ExecutablePath);
    }

    [Fact]
    public void DockerCli_ResolveExecutable_UsesConfiguredPathBeforePath()
    {
        const string configuredPath = "/custom/docker";
        const string pathDocker = "/usr/local/bin/docker";
        var pathValue = "/usr/local/bin";

        var resolution = DockerCli.ResolveExecutable(
            configuredPath,
            environmentPath: null,
            pathValue,
            fallbackPaths: [],
            path => path is configuredPath or pathDocker,
            isWindows: false);

        Assert.Equal(configuredPath, resolution.ExecutablePath);
    }

    [Fact]
    public async Task DockerCli_CreateStartInfo_UsesConfiguredPathAndAugmentsPath()
    {
        using var scope = TestScope.Create();
        var dockerPath = ConfigureFakeDockerCli(scope);

        var startInfo = await DockerCli.CreateStartInfoAsync(scope.Context, ["pull", "custom:latest"], redirectStandardInput: false);

        Assert.Equal(dockerPath, startInfo.FileName);
        Assert.False(startInfo.RedirectStandardInput);
        Assert.Contains("pull", startInfo.ArgumentList);
        var startInfoPath = startInfo.Environment["PATH"];
        Assert.NotNull(startInfoPath);
        Assert.Contains(Path.GetDirectoryName(dockerPath)!, startInfoPath.Split(Path.PathSeparator));
    }

    [Fact]
    public void DockerExecutionConfiguration_Schema_ExposesDockerSettings()
    {
        var fields = DockerExecutionConfiguration.Schema.Sections
            .SelectMany(section => section.Fields)
            .ToDictionary(field => field.Key, StringComparer.OrdinalIgnoreCase);

        Assert.Equal("300", fields["docker.timeoutSeconds.default"].DefaultValue);
        Assert.Equal("300", fields["docker.timeoutSeconds.default"].Placeholder);
        Assert.Equal("Docker CLI path", fields[DockerCli.ExecutablePathConfigurationKey].Label);
        Assert.Equal("Auto-detect", fields[DockerCli.ExecutablePathConfigurationKey].Placeholder);
    }

    [Fact]
    public async Task DockerExecutionWorkspaceConfigService_BuildRuntimeConfig_MountsWorkspacePathsAndShellPath()
    {
        using var scope = TestScope.Create();
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var (workspace, hostRoot) = CreateDockerWorkspace(scope, "test");
        var bindingId = "workspace:primary-execution-target";

        await configService.SaveConfigAsync(
            bindingId,
            new DockerExecutionWorkspaceConfig("test-image:latest", null, "/bin/bash"));

        var config = await configService.GetConfigAsync(bindingId);
        var runtimeConfig = configService.BuildRuntimeConfig(bindingId, workspace, config);

        Assert.Equal("/bin/bash", config.ShellPath);
        Assert.NotNull(config.ContainerName);
        Assert.DoesNotContain(':', config.ContainerName!);
        var mount = Assert.Single(configService.ResolveMounts(runtimeConfig));
        Assert.Equal(Path.GetFullPath(hostRoot), mount.HostPath);
        Assert.StartsWith("/workspace/test-", mount.ContainerPath, StringComparison.Ordinal);
        Assert.Equal(mount.ContainerPath, runtimeConfig.DefaultWorkingDirectory);
    }

    [Fact]
    public async Task DockerExecutionWorkspaceConfigService_BuildRuntimeConfig_UsesWorkspacePathHostRoot()
    {
        using var scope = TestScope.Create();
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var hostRoot = Path.Combine(scope.RootPath, "custom-docker-root");
        Directory.CreateDirectory(hostRoot);
        var workspace = CreateWorkspace(hostRoot);
        var bindingId = "workspace:primary-execution-target";

        await configService.SaveConfigAsync(
            bindingId,
            new DockerExecutionWorkspaceConfig("test-image:latest", null, "/bin/sh"));

        var config = await configService.GetConfigAsync(bindingId);
        var runtimeConfig = configService.BuildRuntimeConfig(bindingId, workspace, config);
        var mount = Assert.Single(configService.ResolveMounts(runtimeConfig));

        Assert.Equal(Path.GetFullPath(hostRoot), mount.HostPath);
        Assert.StartsWith("/workspace/custom-docker-root-", mount.ContainerPath, StringComparison.Ordinal);
    }

    [Fact]
    public void DockerExecutionWorkspaceConfigService_BuildRuntimeConfig_RejectsWorkspacePathWithComma()
    {
        using var scope = TestScope.Create();
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var hostRoot = Path.Combine(scope.RootPath, "docker,workspace");
        var workspace = CreateWorkspace(hostRoot);
        var bindingId = "workspace:primary-execution-target";
        var config = new DockerExecutionWorkspaceConfig("test-image:latest", null, "/bin/sh");

        var exception = Assert.Throws<InvalidOperationException>(() => configService.BuildRuntimeConfig(bindingId, workspace, config));

        Assert.Contains("cannot contain commas", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DockerExecutionWorkspaceConfigService_BuildRuntimeConfig_UsesStableAliasesForNestedWorkspacePaths()
    {
        using var scope = TestScope.Create();
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var outerRoot = Path.Combine(scope.RootPath, "workspace");
        var innerRoot = Path.Combine(outerRoot, "test");
        Directory.CreateDirectory(innerRoot);
        var workspace = CreateWorkspace(outerRoot, innerRoot);
        var bindingId = "workspace:primary-execution-target";

        var runtimeConfig = configService.BuildRuntimeConfig(
            bindingId,
            workspace,
            new DockerExecutionWorkspaceConfig("test-image:latest", null, "/bin/sh"));

        Assert.Equal(2, runtimeConfig.Mounts.Count);
        Assert.Equal(runtimeConfig.Mounts.Select(mount => mount.ContainerPath).Distinct(StringComparer.Ordinal).Count(), runtimeConfig.Mounts.Count);
        Assert.All(runtimeConfig.Mounts, mount => Assert.StartsWith("/workspace/", mount.ContainerPath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DockerExecutionWorkspaceEditorContributor_UsesOnlyReadyImagesAsSelectOptions()
    {
        using var scope = TestScope.Create();
        var runner = new FakeDockerCliRunner(scope.Context, (args, _, _, _, _) =>
        {
            var image = args.Count >= 3 ? args[2] : string.Empty;
            var exitCode = string.Equals(image, "custom:latest", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            return Task.FromResult(new DockerCliRunResult(exitCode, string.Empty, TimedOut: false, WasTruncated: false));
        });
        var imageCatalog = new DockerImageCatalogService(scope.Context, runner);
        await imageCatalog.AddImageAsync("custom:latest");
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        var contributor = new DockerExecutionWorkspaceEditorContributor(configService, imageCatalog);
        var workspace = CreateWorkspace();
        var bindingId = AgentWorkspaceService.BuildPrimaryBindingId(workspace.WorkspaceId);
        await configService.SaveConfigAsync(
            bindingId,
            new DockerExecutionWorkspaceConfig("custom:latest", null, "/bin/sh"));

        var sections = await contributor.GetSectionsAsync(new AgentWorkspaceEditorContext(workspace, "docker", bindingId));

        var imageField = sections.Single().Fields.Single(field => field.FieldId == "image");
        Assert.Equal(AgentEditorFieldKind.Select, imageField.Kind);
        Assert.Equal("custom:latest", imageField.Value);
        var option = Assert.Single(imageField.Options!);
        Assert.Equal("custom:latest", option.Value);
        Assert.Equal("custom:latest", option.Label);
        Assert.Null(option.Description);
        Assert.Contains(imageField.Actions!, action => action.Kind == AgentEditorActionKind.RefreshField
                                                     && action.ActionId == "refresh-docker-images");
        Assert.DoesNotContain(imageField.Actions!, action => action.Kind == AgentEditorActionKind.OpenPackageSettings);
    }

    [Fact]
    public async Task DockerExecutionWorkspaceEditorContributor_AddsSettingsActionWhenNoReadyImagesExist()
    {
        using var scope = TestScope.Create();
        var runner = new FakeDockerCliRunner(scope.Context, (_, _, _, _, _) =>
            Task.FromResult(new DockerCliRunResult(1, "No such image", TimedOut: false, WasTruncated: false)));
        var imageCatalog = new DockerImageCatalogService(scope.Context, runner);
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        var contributor = new DockerExecutionWorkspaceEditorContributor(configService, imageCatalog);
        var workspace = CreateWorkspace();

        var sections = await contributor.GetSectionsAsync(new AgentWorkspaceEditorContext(workspace, "docker", AgentWorkspaceService.BuildPrimaryBindingId(workspace.WorkspaceId)));

        var imageField = sections.Single().Fields.Single(field => field.FieldId == "image");
        Assert.Empty(imageField.Options!);
        Assert.Contains("Pull at least one", imageField.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(imageField.Actions!, action => action.Kind == AgentEditorActionKind.OpenPackageSettings
                                                     && action.PackageId == "sunder.package.agent.execution.docker"
                                                     && action.Label == "Open Settings");
        Assert.Contains(imageField.Actions!, action => action.Kind == AgentEditorActionKind.RefreshField
                                                     && action.ActionId == "refresh-docker-images");
    }

    [Fact]
    public async Task DockerExecutionWorkspaceEditorContributor_SaveRejectsUnconfiguredImage()
    {
        using var scope = TestScope.Create();
        var imageCatalog = new DockerImageCatalogService(scope.Context);
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        var contributor = new DockerExecutionWorkspaceEditorContributor(configService, imageCatalog);
        var workspace = CreateWorkspace();

        var result = await contributor.SaveSectionAsync(
            new AgentWorkspaceEditorContext(workspace, "docker", AgentWorkspaceService.BuildPrimaryBindingId(workspace.WorkspaceId)),
            new AgentEditorSaveRequest(
                "docker-execution-settings",
                new Dictionary<string, AgentEditorFieldValue>(StringComparer.OrdinalIgnoreCase)
                {
                    ["image"] = new("missing:latest"),
                    ["shell-path"] = new("/bin/sh"),
                }));

        Assert.False(result.Success);
        Assert.Contains("not configured", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DockerExecutionWorkspaceEditorContributor_SaveRejectsConfiguredUnreadyImage()
    {
        using var scope = TestScope.Create();
        var runner = new FakeDockerCliRunner(scope.Context, (_, _, _, _, _) =>
            Task.FromResult(new DockerCliRunResult(1, "No such image", TimedOut: false, WasTruncated: false)));
        var imageCatalog = new DockerImageCatalogService(scope.Context, runner);
        await imageCatalog.AddImageAsync("custom:latest");
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        var contributor = new DockerExecutionWorkspaceEditorContributor(configService, imageCatalog);
        var workspace = CreateWorkspace();

        var result = await contributor.SaveSectionAsync(
            new AgentWorkspaceEditorContext(workspace, "docker", AgentWorkspaceService.BuildPrimaryBindingId(workspace.WorkspaceId)),
            new AgentEditorSaveRequest(
                "docker-execution-settings",
                new Dictionary<string, AgentEditorFieldValue>(StringComparer.OrdinalIgnoreCase)
                {
                    ["image"] = new("custom:latest"),
                    ["shell-path"] = new("/bin/sh"),
                }));

        Assert.False(result.Success);
        Assert.Contains("not ready", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Pull it", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DockerExecutionTarget_GetReadinessAsync_StopsBeforeContainerWhenImageIsNotReady()
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var targetCallCount = 0;
        var imageRunner = new FakeDockerCliRunner(scope.Context, (args, _, _, _, _) =>
        {
            Assert.Equal(["image", "inspect", "custom:latest"], args);
            return Task.FromResult(new DockerCliRunResult(1, "No such image", TimedOut: false, WasTruncated: false));
        });
        var targetRunner = new FakeDockerCliRunner(scope.Context, (_, _, _, _, _) =>
        {
            targetCallCount++;
            return Task.FromResult(new DockerCliRunResult(0, string.Empty, TimedOut: false, WasTruncated: false));
        });
        var imageCatalog = new DockerImageCatalogService(scope.Context, imageRunner);
        await imageCatalog.AddImageAsync("custom:latest");
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        var target = new DockerExecutionTarget(scope.Context, configService, lifecycle, imageCatalog, targetRunner);
        var (workspace, _) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(binding.BindingId, new DockerExecutionWorkspaceConfig("custom:latest", "sunder-agent-test", "/bin/sh"));

        var readiness = await target.GetReadinessAsync(new AgentExecutionTargetContext(null, null, workspace, binding));

        Assert.Equal(AgentExecutionTargetReadinessStatus.Failed, readiness.Status);
        Assert.Contains("Pull it", readiness.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, targetCallCount);
    }

    [Fact]
    public async Task DockerExecutionTarget_GetReadinessAsync_ReportsDockerRunOutput()
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var runArgs = Array.Empty<string>();
        var imageRunner = new FakeDockerCliRunner(scope.Context, (_, _, _, _, _) =>
            Task.FromResult(new DockerCliRunResult(0, string.Empty, TimedOut: false, WasTruncated: false)));
        var targetRunner = new FakeDockerCliRunner(scope.Context, (args, _, _, _, _) =>
        {
            if (args.Count > 0 && string.Equals(args[0], "inspect", StringComparison.Ordinal))
            {
                return Task.FromResult(new DockerCliRunResult(1, string.Empty, TimedOut: false, WasTruncated: false));
            }

            if (args.Count > 0 && string.Equals(args[0], "run", StringComparison.Ordinal))
            {
                runArgs = args.ToArray();
                return Task.FromResult(new DockerCliRunResult(1, "No such image: custom:latest", TimedOut: false, WasTruncated: false));
            }

            return Task.FromResult(new DockerCliRunResult(0, string.Empty, TimedOut: false, WasTruncated: false));
        });
        var imageCatalog = new DockerImageCatalogService(scope.Context, imageRunner);
        await imageCatalog.AddImageAsync("custom:latest");
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        var target = new DockerExecutionTarget(scope.Context, configService, lifecycle, imageCatalog, targetRunner);
        var (workspace, _) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(binding.BindingId, new DockerExecutionWorkspaceConfig("custom:latest", "sunder-agent-test", "/bin/sh"));

        var readiness = await target.GetReadinessAsync(new AgentExecutionTargetContext(null, null, workspace, binding));

        Assert.Equal(AgentExecutionTargetReadinessStatus.Failed, readiness.Status);
        Assert.Contains("failed to start from image 'custom:latest'", readiness.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No such image", readiness.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--pull", runArgs);
        Assert.Contains("never", runArgs);
    }

    [Fact]
    public async Task DockerContainerLifecycleService_StopsContainerAfterIdleTimeout()
    {
        using var lifecycle = new DockerContainerLifecycleService(TimeSpan.FromMilliseconds(25));
        var stopCount = 0;

        using (await lifecycle.AcquireAsync(
                   "container-key",
                   _ => Task.FromResult("container-name"),
                   (_, _) =>
                   {
                       Interlocked.Increment(ref stopCount);
                       return Task.CompletedTask;
                   }))
        {
            await Task.Delay(75);
            Assert.Equal(0, Volatile.Read(ref stopCount));
        }

        await WaitUntilAsync(() => Volatile.Read(ref stopCount) == 1);
    }

    [Fact]
    public async Task DockerContainerLifecycleService_Dispose_DoesNotStopContainerSynchronously()
    {
        var lifecycle = new DockerContainerLifecycleService(TimeSpan.FromMinutes(1));
        var stopCount = 0;

        using (await lifecycle.AcquireAsync(
                   "container-key",
                   _ => Task.FromResult("container-name"),
                   (_, _) =>
                   {
                       Interlocked.Increment(ref stopCount);
                       return Task.CompletedTask;
                   }))
        {
        }

        lifecycle.Dispose();

        Assert.Equal(0, Volatile.Read(ref stopCount));
    }

    [Fact]
    public async Task DockerContainerLifecycleService_DisposeAsync_StopsKnownContainer()
    {
        var lifecycle = new DockerContainerLifecycleService(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1));
        var stopCount = 0;

        using (await lifecycle.AcquireAsync(
                   "container-key",
                   _ => Task.FromResult("container-name"),
                   (_, _) =>
                   {
                       Interlocked.Increment(ref stopCount);
                       return Task.CompletedTask;
                   }))
        {
        }

        await lifecycle.DisposeAsync();

        Assert.Equal(1, Volatile.Read(ref stopCount));
    }

    [Fact]
    public async Task AgentExecutionTargetWarmupService_CallsPrimaryExecutionTargetReadiness()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var catalog = new TestExtensionCatalog();
        var target = new CountingExecutionTarget("test-target");
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, target);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog);
        var warmupService = new AgentExecutionTargetWarmupService(workspaceService, executionTargetService);
        var workspace = workspaceService.CreateWorkspace("Warmup Workspace");
        workspaceService.SavePrimaryExecutionBinding(workspace.WorkspaceId, target.Descriptor.TargetId);

        var result = await warmupService.WarmWorkspaceAsync(workspace);

        Assert.Equal(AgentExecutionTargetWarmupStatus.Ready, result.Status);
        Assert.Equal(1, target.ReadinessCallCount);
        Assert.Null(target.LastContext?.ProfileId);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_SaveWorkspace_WarmsSelectedExecutionTarget()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var catalog = new TestExtensionCatalog();
        var target = new CountingExecutionTarget("test-target");
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, target);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog);
        var warmupService = new AgentExecutionTargetWarmupService(workspaceService, executionTargetService);
        using var viewModel = new AgentWorkspacesViewModel(workspaceService, executionTargetService, catalog, warmupService);
        await viewModel.InitializeAsync();

        viewModel.CreateWorkspaceCommand.Execute(null);
        viewModel.SelectedExecutionTarget = viewModel.ExecutionTargets.Single(targetOption => string.Equals(targetOption.TargetId, target.Descriptor.TargetId, StringComparison.OrdinalIgnoreCase));
        await viewModel.SaveWorkspaceCommand.ExecuteAsync(null);

        Assert.Equal(1, target.ReadinessCallCount);
        Assert.Contains("Execution target is ready", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_RefreshesExecutionTargets_WhenCatalogChanges()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var catalog = new TestExtensionCatalog();
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog);
        using var viewModel = new AgentWorkspacesViewModel(workspaceService, executionTargetService, catalog);
        await viewModel.InitializeAsync();

        viewModel.CreateWorkspaceCommand.Execute(null);
        Assert.False(viewModel.HasExecutionTargetChoices);
        Assert.True(viewModel.HasNoExecutionTargetChoices);

        var target = new CountingExecutionTarget("docker");
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, target);

        await WaitUntilAsync(() => viewModel.ExecutionTargets.Any(targetOption => string.Equals(targetOption.TargetId, "docker", StringComparison.OrdinalIgnoreCase)));
        Assert.True(viewModel.HasExecutionTargetChoices);
        Assert.False(viewModel.HasNoExecutionTargetChoices);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_RefreshesEditorSections_WhenCatalogChanges()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var catalog = new TestExtensionCatalog();
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog);
        using var viewModel = new AgentWorkspacesViewModel(workspaceService, executionTargetService, catalog);
        await viewModel.InitializeAsync();

        viewModel.CreateWorkspaceCommand.Execute(null);
        var target = new CountingExecutionTarget("docker");
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, target);
        await WaitUntilAsync(() => viewModel.ExecutionTargets.Any(targetOption => string.Equals(targetOption.TargetId, "docker", StringComparison.OrdinalIgnoreCase)));
        viewModel.SelectedExecutionTarget = viewModel.ExecutionTargets.Single(targetOption => string.Equals(targetOption.TargetId, "docker", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(viewModel.EditorSections);

        catalog.AddExtension(PackageExtensionPoints.WorkspaceEditorContributors, new TestWorkspaceEditorContributor("docker"));

        await WaitUntilAsync(() => viewModel.EditorSections.Any(section => string.Equals(section.SectionId, "test-editor", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_LoadWorkspace_RefreshesEditorSectionsOnce()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var catalog = new TestExtensionCatalog();
        var target = new CountingExecutionTarget("docker");
        var contributor = new CountingWorkspaceEditorContributor("docker");
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, target);
        catalog.AddExtension(PackageExtensionPoints.WorkspaceEditorContributors, contributor);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog);
        var workspace = workspaceService.CreateWorkspace("Docker Workspace");
        workspaceService.SavePrimaryExecutionBinding(workspace.WorkspaceId, target.Descriptor.TargetId);

        using var viewModel = new AgentWorkspacesViewModel(workspaceService, executionTargetService, catalog);
        await viewModel.InitializeAsync();

        await WaitUntilAsync(() => viewModel.EditorSections.Count == 1);
        Assert.Equal(1, contributor.GetSectionsCallCount);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_SaveWorkspace_DoesNotRefreshEditorSectionsAndReturnsToList()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var catalog = new TestExtensionCatalog();
        var target = new CountingExecutionTarget("docker");
        var contributor = new CountingWorkspaceEditorContributor("docker");
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, target);
        catalog.AddExtension(PackageExtensionPoints.WorkspaceEditorContributors, contributor);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog);
        using var viewModel = new AgentWorkspacesViewModel(workspaceService, executionTargetService, catalog)
        {
            IsCompactLayout = true,
        };
        await viewModel.InitializeAsync();

        viewModel.CreateWorkspaceCommand.Execute(null);
        viewModel.SelectedExecutionTarget = viewModel.ExecutionTargets.Single(targetOption => string.Equals(targetOption.TargetId, "docker", StringComparison.OrdinalIgnoreCase));
        await WaitUntilAsync(() => viewModel.EditorSections.Count == 1);
        var getSectionsCallCount = contributor.GetSectionsCallCount;
        viewModel.DisplayName = "Saved Docker Workspace";
        var savedWorkspaceId = viewModel.SelectedWorkspace!.WorkspaceId;

        await viewModel.SaveWorkspaceCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsEditorActive);
        Assert.Empty(viewModel.EditorSections);
        Assert.Equal(getSectionsCallCount, contributor.GetSectionsCallCount);
        Assert.Equal(1, contributor.SaveSectionCallCount);
        Assert.Null(viewModel.SelectedWorkspace);
        Assert.False(viewModel.HasSelectedWorkspace);
        Assert.False(viewModel.SaveWorkspaceCommand.CanExecute(null));
        Assert.Contains(viewModel.Workspaces, workspace => workspace.WorkspaceId == savedWorkspaceId
                                                        && workspace.DisplayName == "Saved Docker Workspace");
        Assert.Empty(viewModel.StatusText);
        Assert.False(viewModel.HasStatusText);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_SaveWorkspace_WideLayout_KeepsEditorLoadedAndAutoClearsSuccess()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var catalog = new TestExtensionCatalog();
        var target = new CountingExecutionTarget("docker");
        var contributor = new CountingWorkspaceEditorContributor("docker");
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, target);
        catalog.AddExtension(PackageExtensionPoints.WorkspaceEditorContributors, contributor);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog);
        using var viewModel = new AgentWorkspacesViewModel(workspaceService, executionTargetService, catalog);
        await viewModel.InitializeAsync();

        viewModel.CreateWorkspaceCommand.Execute(null);
        viewModel.SelectedExecutionTarget = viewModel.ExecutionTargets.Single(targetOption => string.Equals(targetOption.TargetId, "docker", StringComparison.OrdinalIgnoreCase));
        await WaitUntilAsync(() => viewModel.EditorSections.Count == 1);
        var getSectionsCallCount = contributor.GetSectionsCallCount;
        var savedWorkspaceId = viewModel.SelectedWorkspace!.WorkspaceId;
        viewModel.DisplayName = "Saved Docker Workspace";

        await viewModel.SaveWorkspaceCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsEditorActive);
        Assert.Equal(savedWorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
        Assert.Equal("Saved Docker Workspace", viewModel.DisplayName);
        Assert.Single(viewModel.EditorSections);
        Assert.Equal(getSectionsCallCount, contributor.GetSectionsCallCount);
        Assert.Equal(1, contributor.SaveSectionCallCount);
        Assert.Equal("docker", viewModel.SelectedExecutionTarget?.TargetId);
        Assert.Equal("Workspace saved.", viewModel.StatusText);
        Assert.True(viewModel.HasStatusText);
        Assert.True(viewModel.IsStatusSuccess);
        Assert.False(viewModel.IsStatusWarning);
        Assert.False(viewModel.IsStatusError);

        await WaitUntilAsync(() => !viewModel.HasStatusText, TimeSpan.FromSeconds(4));

        Assert.Empty(viewModel.StatusText);
        Assert.Equal(AgentWorkspaceStatusKind.None, viewModel.StatusKind);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_SaveWorkspace_FailedEditorSaveKeepsEditorOpen()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var catalog = new TestExtensionCatalog();
        var target = new CountingExecutionTarget("docker");
        var contributor = new CountingWorkspaceEditorContributor("docker", AgentEditorSaveResult.Failed("Editor save failed."));
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, target);
        catalog.AddExtension(PackageExtensionPoints.WorkspaceEditorContributors, contributor);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog);
        using var viewModel = new AgentWorkspacesViewModel(workspaceService, executionTargetService, catalog)
        {
            IsCompactLayout = true,
        };
        await viewModel.InitializeAsync();

        viewModel.CreateWorkspaceCommand.Execute(null);
        viewModel.SelectedExecutionTarget = viewModel.ExecutionTargets.Single(targetOption => string.Equals(targetOption.TargetId, "docker", StringComparison.OrdinalIgnoreCase));
        await WaitUntilAsync(() => viewModel.EditorSections.Count == 1);

        await viewModel.SaveWorkspaceCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsEditorActive);
        Assert.Equal("Editor save failed.", viewModel.StatusText);
        Assert.Single(viewModel.EditorSections);
        Assert.Equal(1, contributor.SaveSectionCallCount);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_SaveWorkspace_SelectionChangesDuringEditorSavePersistOriginalSnapshot()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var catalog = new TestExtensionCatalog();
        var target = new CountingExecutionTarget("docker");
        var contributor = new BlockingWorkspaceEditorContributor("docker");
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, target);
        catalog.AddExtension(PackageExtensionPoints.WorkspaceEditorContributors, contributor);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog);
        var originalWorkspace = workspaceService.CreateWorkspace("Original Workspace");
        var otherWorkspace = workspaceService.CreateWorkspace("Other Workspace");
        using var viewModel = new AgentWorkspacesViewModel(
            workspaceService,
            executionTargetService,
            catalog);
        await viewModel.InitializeAsync();
        viewModel.ActivateWorkspace(originalWorkspace);
        viewModel.SelectedExecutionTarget = viewModel.ExecutionTargets.Single(option =>
            string.Equals(option.TargetId, "docker", StringComparison.OrdinalIgnoreCase));
        await contributor.SectionsLoaded.WaitAsync(TimeSpan.FromSeconds(10));
        viewModel.DisplayName = "Saved Original Workspace";

        var save = viewModel.SaveWorkspaceCommand.ExecuteAsync(null);
        await contributor.SaveEntered.WaitAsync(TimeSpan.FromSeconds(10));
        viewModel.ActivateWorkspace(otherWorkspace);
        contributor.ReleaseSave();
        await save.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            "Saved Original Workspace",
            workspaceService.GetWorkspace(originalWorkspace.WorkspaceId)?.DisplayName);
        Assert.Equal(
            "Other Workspace",
            workspaceService.GetWorkspace(otherWorkspace.WorkspaceId)?.DisplayName);
        Assert.Equal(originalWorkspace.WorkspaceId, contributor.SavedWorkspaceId);
        Assert.Contains(
            workspaceService.ListBindings(originalWorkspace.WorkspaceId),
            binding => binding.ContributionId == "docker");
        Assert.Empty(workspaceService.ListBindings(otherWorkspace.WorkspaceId));
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_CreateWorkspace_CompactLayout_OpensEditor()
    {
        using var scope = TestScope.Create();
        var services = CreateWorkspaceViewServices(scope.Context);
        using var viewModel = new AgentWorkspacesViewModel(services.WorkspaceService, services.ExecutionTargetService, services.Catalog)
        {
            IsCompactLayout = true,
        };
        await viewModel.InitializeAsync();

        viewModel.CreateWorkspaceCommand.Execute(null);

        Assert.True(viewModel.IsEditorActive);
        Assert.True(viewModel.ShowCompactEditor);
        Assert.False(viewModel.ShowCompactList);
        Assert.Equal("New Workspace", viewModel.SelectedWorkspace?.DisplayName);
        Assert.Empty(viewModel.StatusText);
        Assert.False(viewModel.HasStatusText);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_CompactLayout_ClearsDefaultWorkspaceSelection()
    {
        using var scope = TestScope.Create();
        var services = CreateWorkspaceViewServices(scope.Context);
        var workspace = services.WorkspaceService.CreateWorkspace("Alpha Workspace");
        using var viewModel = new AgentWorkspacesViewModel(services.WorkspaceService, services.ExecutionTargetService, services.Catalog);
        await viewModel.InitializeAsync();
        Assert.Equal(workspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);

        viewModel.IsCompactLayout = true;

        Assert.Null(viewModel.SelectedWorkspace);
        Assert.False(viewModel.HasSelectedWorkspace);
        Assert.False(viewModel.IsEditorActive);
        Assert.True(viewModel.ShowCompactList);
        Assert.False(viewModel.ShowCompactEditor);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_WideLayout_SelectsFirstWorkspace_WhenCompactHadNoSelection()
    {
        using var scope = TestScope.Create();
        var services = CreateWorkspaceViewServices(scope.Context);
        var workspace = services.WorkspaceService.CreateWorkspace("Alpha Workspace");
        using var viewModel = new AgentWorkspacesViewModel(
            services.WorkspaceService,
            services.ExecutionTargetService,
            services.Catalog);
        await viewModel.InitializeAsync();
        viewModel.IsCompactLayout = true;
        Assert.Null(viewModel.SelectedWorkspace);

        viewModel.IsCompactLayout = false;

        Assert.Equal(workspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
        Assert.True(viewModel.ShowListPane);
        Assert.True(viewModel.ShowEditorPane);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_ActivateWorkspace_CompactLayout_OpensSelectedWorkspaceEditor()
    {
        using var scope = TestScope.Create();
        var services = CreateWorkspaceViewServices(scope.Context);
        var workspace = services.WorkspaceService.CreateWorkspace("Alpha Workspace");
        using var viewModel = new AgentWorkspacesViewModel(services.WorkspaceService, services.ExecutionTargetService, services.Catalog)
        {
            IsCompactLayout = true,
        };
        await viewModel.InitializeAsync();
        Assert.False(viewModel.IsEditorActive);

        viewModel.ActivateWorkspace(viewModel.Workspaces.Single(item => item.WorkspaceId == workspace.WorkspaceId));

        Assert.True(viewModel.IsEditorActive);
        Assert.Equal(workspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
        Assert.True(viewModel.ShowCompactEditor);

        viewModel.BackToWorkspaceListCommand.Execute(null);

        Assert.False(viewModel.IsEditorActive);
        Assert.Null(viewModel.SelectedWorkspace);
        Assert.True(viewModel.ShowCompactList);
        Assert.False(viewModel.ShowCompactEditor);

        viewModel.ActivateWorkspace(viewModel.Workspaces.Single(item => item.WorkspaceId == workspace.WorkspaceId));

        Assert.True(viewModel.IsEditorActive);
        Assert.Equal(workspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_ActivateWorkspace_WideLayout_KeepsSplitPanesVisible()
    {
        using var scope = TestScope.Create();
        var services = CreateWorkspaceViewServices(scope.Context);
        var firstWorkspace = services.WorkspaceService.CreateWorkspace("Alpha Workspace");
        var secondWorkspace = services.WorkspaceService.CreateWorkspace("Beta Workspace");
        using var viewModel = new AgentWorkspacesViewModel(services.WorkspaceService, services.ExecutionTargetService, services.Catalog);
        await viewModel.InitializeAsync();
        Assert.Equal(firstWorkspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);

        viewModel.ActivateWorkspace(viewModel.Workspaces.Single(item => item.WorkspaceId == secondWorkspace.WorkspaceId));

        Assert.False(viewModel.IsEditorActive);
        Assert.True(viewModel.ShowListPane);
        Assert.True(viewModel.ShowEditorPane);
        Assert.Equal(secondWorkspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_DeleteWorkspace_CompactLayout_ReturnsToList()
    {
        using var scope = TestScope.Create();
        var services = CreateWorkspaceViewServices(scope.Context);
        var firstWorkspace = services.WorkspaceService.CreateWorkspace("Alpha Workspace");
        var secondWorkspace = services.WorkspaceService.CreateWorkspace("Beta Workspace");
        using var viewModel = new AgentWorkspacesViewModel(services.WorkspaceService, services.ExecutionTargetService, services.Catalog)
        {
            IsCompactLayout = true,
        };
        await viewModel.InitializeAsync();
        viewModel.SelectedWorkspace = viewModel.Workspaces.Single(item => item.WorkspaceId == secondWorkspace.WorkspaceId);
        Assert.True(viewModel.IsEditorActive);

        viewModel.DeleteWorkspaceCommand.Execute(null);

        Assert.False(viewModel.IsEditorActive);
        Assert.True(viewModel.ShowCompactList);
        Assert.False(viewModel.ShowCompactEditor);
        Assert.Single(viewModel.Workspaces);
        Assert.Null(viewModel.SelectedWorkspace);
        Assert.Contains(viewModel.Workspaces, workspace => workspace.WorkspaceId == firstWorkspace.WorkspaceId);
        Assert.Null(services.WorkspaceService.GetWorkspace(secondWorkspace.WorkspaceId));
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_DeleteWorkspace_WideLayout_SelectsFirstRemainingWorkspace()
    {
        using var scope = TestScope.Create();
        var services = CreateWorkspaceViewServices(scope.Context);
        var firstWorkspace = services.WorkspaceService.CreateWorkspace("Alpha Workspace");
        var secondWorkspace = services.WorkspaceService.CreateWorkspace("Beta Workspace");
        using var viewModel = new AgentWorkspacesViewModel(services.WorkspaceService, services.ExecutionTargetService, services.Catalog);
        await viewModel.InitializeAsync();
        viewModel.SelectedWorkspace = viewModel.Workspaces.Single(item => item.WorkspaceId == secondWorkspace.WorkspaceId);

        viewModel.DeleteWorkspaceCommand.Execute(null);

        Assert.False(viewModel.IsEditorActive);
        Assert.True(viewModel.ShowListPane);
        Assert.True(viewModel.ShowEditorPane);
        Assert.Single(viewModel.Workspaces);
        Assert.Equal(firstWorkspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
        Assert.Null(services.WorkspaceService.GetWorkspace(secondWorkspace.WorkspaceId));
    }

    [Fact]
    public void AgentEditorPathListFieldViewModel_PreservesDefaultWithoutInitialSelection()
    {
        var section = CreateEditorSectionViewModel(new AgentEditorField(
            "workspace-paths",
            "Workspace paths",
            AgentEditorFieldKind.PathList,
            Items:
            [
                new AgentEditorListItem("0", "/workspace", IsDefault: true),
                new AgentEditorListItem("1", "/tmp"),
            ]));

        var field = Assert.IsType<AgentEditorPathListFieldViewModel>(Assert.Single(section.Fields));

        Assert.Null(field.SelectedItem);
        Assert.False(field.HasSelectedItem);
        Assert.True(field.Items[0].IsDefault);
        Assert.False(field.Items[1].IsDefault);
    }

    [Fact]
    public void AgentEditorPathListFieldViewModel_AddDefaultItem_SelectsNewUserItem()
    {
        var section = CreateEditorSectionViewModel(new AgentEditorField(
            "workspace-paths",
            "Workspace paths",
            AgentEditorFieldKind.PathList,
            Items:
            [
                new AgentEditorListItem("0", "/workspace", IsDefault: true),
            ],
            DefaultNewItemValue: "/workspace"));
        var field = Assert.IsType<AgentEditorPathListFieldViewModel>(Assert.Single(section.Fields));

        field.AddDefaultItem();

        Assert.NotNull(field.SelectedItem);
        Assert.True(field.HasSelectedItem);
        Assert.Equal("/workspace2", field.SelectedItem!.Value);
        Assert.True(field.Items[0].IsDefault);
        Assert.False(field.SelectedItem.IsDefault);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_RefreshField_UpdatesDockerImageSelectOnly()
    {
        using var scope = TestScope.Create();
        var readyImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runner = new FakeDockerCliRunner(scope.Context, (args, _, _, _, _) =>
        {
            var image = args.Count >= 3
                        && string.Equals(args[0], "image", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(args[1], "inspect", StringComparison.OrdinalIgnoreCase)
                ? args[2]
                : string.Empty;
            return Task.FromResult(new DockerCliRunResult(
                readyImages.Contains(image) ? 0 : 1,
                readyImages.Contains(image) ? "[]" : "No such image",
                TimedOut: false,
                WasTruncated: false));
        });
        var imageCatalog = new DockerImageCatalogService(scope.Context, runner);
        await imageCatalog.SaveImagesAsync(
        [
            new DockerImageDefinition("first:latest", DockerImageStatus.NotPulled, null, null),
            new DockerImageDefinition("second:latest", DockerImageStatus.NotPulled, null, null),
        ]);
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        var contributor = new DockerExecutionWorkspaceEditorContributor(configService, imageCatalog);
        var store = new AgentLocalStore(scope.Context);
        var catalog = new TestExtensionCatalog();
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, new CountingExecutionTarget("docker"));
        catalog.AddExtension(PackageExtensionPoints.WorkspaceEditorContributors, contributor);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog);
        using var viewModel = new AgentWorkspacesViewModel(workspaceService, executionTargetService, catalog);
        await viewModel.InitializeAsync();

        viewModel.CreateWorkspaceCommand.Execute(null);
        viewModel.SelectedExecutionTarget = viewModel.ExecutionTargets.Single(targetOption => string.Equals(targetOption.TargetId, "docker", StringComparison.OrdinalIgnoreCase));
        await WaitUntilAsync(() => viewModel.EditorSections.Any(section => string.Equals(section.SectionId, "docker-execution-settings", StringComparison.OrdinalIgnoreCase)));

        var section = viewModel.EditorSections.Single(section => string.Equals(section.SectionId, "docker-execution-settings", StringComparison.OrdinalIgnoreCase));
        var imageField = Assert.IsType<AgentEditorSelectFieldViewModel>(section.Fields.Single(field => field.FieldId == "image"));
        var shellField = Assert.IsType<AgentEditorTextFieldViewModel>(section.Fields.Single(field => field.FieldId == "shell-path"));
        shellField.Value = "/custom/sh";
        var refreshAction = Assert.Single(imageField.IconActions);
        Assert.Equal(AgentEditorActionKind.RefreshField, refreshAction.Kind);
        Assert.Equal("↻", refreshAction.Content);
        Assert.Empty(imageField.Options);
        Assert.False(imageField.HasOptions);
        Assert.True(imageField.HasNoOptions);
        Assert.True(imageField.ShowEmptyStateActions);
        Assert.Contains(imageField.TextActions, action => action.Kind == AgentEditorActionKind.OpenPackageSettings);

        readyImages.Add("second:latest");
        await viewModel.ExecuteEditorActionAsync(refreshAction);

        Assert.Same(section, viewModel.EditorSections.Single(item => string.Equals(item.SectionId, "docker-execution-settings", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("/custom/sh", shellField.Value);
        var option = Assert.Single(imageField.Options);
        Assert.Equal("second:latest", option.Value);
        Assert.Equal("second:latest", imageField.SelectedOption?.Value);
        Assert.True(imageField.HasOptions);
        Assert.False(imageField.HasNoOptions);
        Assert.False(imageField.ShowEmptyStateActions);
        Assert.Single(imageField.IconActions);
        Assert.Empty(imageField.TextActions);
        Assert.Contains("Choose a ready", imageField.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentChatViewModel_WarmsExecutionTarget_WhenWorkspaceSelectionChanges()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var catalog = new TestExtensionCatalog();
        var target = new CountingExecutionTarget("test-target");
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, target);
        var sessionService = new AgentSessionService(store);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog);
        var installedPackageToolSource = new InstalledPackageToolSource(catalog);
        var toolService = new AgentToolService(installedPackageToolSource, sessionService, workspaceService, executionTargetService, catalog);
        var profileService = new AgentProfileService(store, toolService, catalog);
        var permissionService = new AgentPermissionService(store, catalog);
        var memoryCoordinator = new AgentMemoryCoordinator(sessionService, catalog);
        var promptComposer = new AgentSystemPromptComposer(catalog);
        var attachmentService = new AgentAttachmentService(scope.Context);
        var behaviorLoop = new DefaultAgentBehaviorLoop(promptComposer, attachmentService);
        var runAttachmentStore = new AgentRunAttachmentStore(attachmentService);
        var activeRunRegistry = new AgentActiveRunRegistry();
        var runEventLogger = new AgentRunEventLogger(scope.Context);
        var providerResolver = new AgentRunProviderResolver(profileService, catalog);
        var behaviorLoopResolver = new AgentBehaviorLoopResolver(catalog, behaviorLoop);
        var stopCoordinator = new AgentRunStopCoordinator(sessionService, permissionService, memoryCoordinator, activeRunRegistry, profileService);
        var behaviorLoopHostFactory = new AgentBehaviorLoopHostFactory(sessionService, toolService, permissionService, memoryCoordinator, runEventLogger, activeRunRegistry, behaviorLoop);
        var runPreparationService = new AgentRunPreparationService(sessionService, profileService, workspaceService, runAttachmentStore, runEventLogger, providerResolver);
        var runStartService = new AgentRunStartService(sessionService, memoryCoordinator, runAttachmentStore, activeRunRegistry, runEventLogger);
        var runExecutionService = new AgentRunExecutionService(sessionService, workspaceService, memoryCoordinator, activeRunRegistry, runEventLogger, behaviorLoopHostFactory, behaviorLoopResolver);
        var childRunSessionService = new AgentChildRunSessionService(sessionService, profileService);
        var parentRunContinuationService = new AgentParentRunContinuationService(sessionService, profileService, workspaceService, providerResolver, activeRunRegistry, behaviorLoopHostFactory, behaviorLoopResolver, childRunSessionService);
        var permissionResumeCoordinator = new AgentPermissionResumeCoordinator(sessionService, workspaceService, profileService, permissionService, providerResolver, activeRunRegistry, runEventLogger, behaviorLoopHostFactory, behaviorLoopResolver, parentRunContinuationService);
        var userMessageRunCoordinator = new AgentUserMessageRunCoordinator(
            sessionService,
            runPreparationService,
            runStartService,
            runExecutionService,
            activeRunRegistry);
        var runCoordinator = new AgentRunCoordinator(userMessageRunCoordinator, stopCoordinator, childRunSessionService, permissionResumeCoordinator);
        var warmupService = new AgentExecutionTargetWarmupService(workspaceService, executionTargetService);
        var firstWorkspace = workspaceService.CreateWorkspace("First Workspace");
        workspaceService.SavePrimaryExecutionBinding(firstWorkspace.WorkspaceId, target.Descriptor.TargetId);
        var secondWorkspace = workspaceService.CreateWorkspace("Second Workspace");
        workspaceService.SavePrimaryExecutionBinding(secondWorkspace.WorkspaceId, target.Descriptor.TargetId);

        using var viewModel = new AgentChatViewModel(
            profileService,
            workspaceService,
            sessionService,
            permissionService,
            runCoordinator,
            warmupService: warmupService);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => target.ReadinessCallCount >= 1);

        viewModel.SelectedWorkspace = viewModel.Workspaces.Single(workspace => workspace.WorkspaceId == secondWorkspace.WorkspaceId);

        await WaitUntilAsync(() => target.ReadinessCallCount >= 2
                                  && string.Equals(target.LastContext?.Workspace.WorkspaceId, secondWorkspace.WorkspaceId, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PermissionService_UsesLastMatchingRule_AndUnrestrictedModeCannotBypassDeny()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var catalog = new TestExtensionCatalog();
        var permissions = new AgentPermissionService(store, catalog);
        var workspace = CreateWorkspace();
        store.SaveWorkspace(workspace);
        var session = store.CreateSession("Permission Test", workspaceId: workspace.WorkspaceId);

        catalog.AddExtension(PackageExtensionPoints.PermissionSurfaces, new TestPermissionSurface());
        permissions.SaveOverride("shell.execute", AgentPermissionBoundaryIds.SelectedExecutionTarget, AgentPermissionDecision.Deny);
        permissions.SetSessionUnrestrictedMode(session.SessionId, true);

        var denied = permissions.Evaluate(session.SessionId, new AgentPermissionRequest("shell.execute", AgentPermissionBoundaryIds.SelectedExecutionTarget, "remove", Command: "rm -rf tmp"));
        permissions.SaveOverride("shell.execute", AgentPermissionBoundaryIds.SelectedExecutionTarget, AgentPermissionDecision.Ask);
        var allowed = permissions.Evaluate(session.SessionId, new AgentPermissionRequest("shell.execute", AgentPermissionBoundaryIds.SelectedExecutionTarget, "list", Command: "ls"));

        Assert.Equal(AgentPermissionDecision.Allow, allowed.Decision);
        Assert.Equal(AgentPermissionDecision.Deny, denied.Decision);
    }

    [Fact]
    public async Task FilesToolSource_ReturnsStructuredGlobResults()
    {
        var catalog = new TestExtensionCatalog();
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, new FakeExecutionTarget("one.txt\ntwo.txt\n"));
        var source = new FilesToolSource(catalog);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding),
            new AgentToolRequest("glob", "{\"pattern\":\"*.txt\"}"));

        Assert.False(result.IsError);
        Assert.NotNull(result.StructuredPayloadJson);
        Assert.Contains("one.txt", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("glob", "{\"pattern\":\"*.txt\"}")]
    [InlineData("glob", "{\"pattern\":\"*.txt\",\"path\":null}")]
    [InlineData("glob", "{\"pattern\":\"*.txt\",\"path\":\"\"}")]
    [InlineData("grep", "{\"pattern\":\"needle\"}")]
    [InlineData("grep", "{\"pattern\":\"needle\",\"path\":null}")]
    [InlineData("grep", "{\"pattern\":\"needle\",\"path\":\"\"}")]
    public async Task FilesToolSource_ClassifiesSearchWithoutPathAsDefaultWorkspace(string toolId, string argumentsJson)
    {
        var catalog = new TestExtensionCatalog();
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, new FakeExecutionTarget(string.Empty));
        var source = new FilesToolSource(catalog);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);

        var permission = await source.BuildPermissionRequestAsync(
            new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding),
            new AgentToolRequest(toolId, argumentsJson));

        Assert.NotNull(permission);
        Assert.Equal("files.search", permission!.ActionId);
        Assert.Equal(AgentPermissionBoundaryIds.ConfiguredScope, permission.BoundaryId);
        Assert.Equal(".", permission.Path);
    }

    [Fact]
    public async Task FilesToolSource_Edit_AcceptsStringReplaceAll()
    {
        var catalog = new TestExtensionCatalog();
        var target = new MutableFileExecutionTarget("hello hello");
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, target);
        var source = new FilesToolSource(catalog);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding),
            new AgentToolRequest("edit", "{\"path\":\"notes.txt\",\"oldString\":\"hello\",\"newString\":\"bye\",\"replaceAll\":\"true\"}"));

        Assert.False(result.IsError, result.Content);
        Assert.Equal("bye bye", target.Content);
    }

    [Fact]
    public async Task FilesToolSource_Edit_EmitsPresentationPayloadWithActualLineNumbers()
    {
        var catalog = new TestExtensionCatalog();
        var target = new MutableFileExecutionTarget("one\ntwo\nold\nsame\nfive");
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, target);
        var source = new FilesToolSource(catalog);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);
        var argumentsJson = JsonSerializer.Serialize(new
        {
            path = "notes.txt",
            oldString = "old\nsame",
            newString = "new\nsame",
        });

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding),
            new AgentToolRequest("edit", argumentsJson));

        Assert.False(result.IsError, result.Content);
        Assert.NotNull(result.PresentationPayloadJson);

        var row = new AgentToolInvocationRowViewModel(
            CreateToolTurn(),
            CreateToolItem("edit", argumentsJson, textContent: result.Content, resultSummary: result.Summary, presentationPayloadJson: result.PresentationPayloadJson),
            new AgentToolPresentationService());
        var file = Assert.Single(row.ToolDiffFiles);

        Assert.Equal("Updated notes.txt (+1 -1)", row.HeaderDetailText);
        Assert.Contains(file.Lines, line => line.IsDeleted && line.LineNumberText == "3" && line.Text == "old");
        Assert.Contains(file.Lines, line => line.IsAdded && line.LineNumberText == "3" && line.Text == "new");
        Assert.Contains(file.Lines, line => line.IsContext && line.LineNumberText == "4" && line.Text == "same");
        Assert.DoesNotContain(file.Lines, line => line.MarkerText is "+" or "-" or "@@");
    }

    [Fact]
    public async Task FilesToolSource_ApplyPatch_EmitsPresentationPayloadWithActualLineNumbers()
    {
        var catalog = new TestExtensionCatalog();
        var target = new MutableFileExecutionTarget("one\ntwo\nold\nsame\nfive");
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, target);
        var source = new FilesToolSource(catalog);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);
        var patchText = """
            *** Begin Patch
            *** Update File: notes.txt
            @@
             two
            -old
            +new
             same
            *** End Patch
            """;
        var argumentsJson = ApplyPatchArgs(patchText);

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding),
            new AgentToolRequest("apply_patch", argumentsJson));

        Assert.False(result.IsError, result.Content);
        Assert.NotNull(result.PresentationPayloadJson);
        Assert.Equal("one\ntwo\nnew\nsame\nfive", target.Content);

        var row = new AgentToolInvocationRowViewModel(
            CreateToolTurn(),
            CreateToolItem("apply_patch", argumentsJson, textContent: result.Content, resultSummary: result.Summary, presentationPayloadJson: result.PresentationPayloadJson),
            new AgentToolPresentationService());
        var file = Assert.Single(row.ToolDiffFiles);

        Assert.Equal("Updated notes.txt (+1 -1)", row.HeaderDetailText);
        Assert.Equal("Patch", row.ToolDiffSectionTitle);
        Assert.False(row.ShowMarkdownDetails);
        Assert.Contains(file.Lines, line => line.IsContext && line.LineNumberText == "2" && line.Text == "two");
        Assert.Contains(file.Lines, line => line.IsDeleted && line.LineNumberText == "3" && line.Text == "old");
        Assert.Contains(file.Lines, line => line.IsAdded && line.LineNumberText == "3" && line.Text == "new");
        Assert.Contains(file.Lines, line => line.IsContext && line.LineNumberText == "4" && line.Text == "same");
        Assert.DoesNotContain(file.Lines, line => line.MarkerText is "+" or "-" or "@@");
    }

    [Fact]
    public async Task FilesToolSource_Edit_InvalidReplaceAll_ReturnsToolError()
    {
        var catalog = new TestExtensionCatalog();
        var target = new MutableFileExecutionTarget("hello");
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, target);
        var source = new FilesToolSource(catalog);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding),
            new AgentToolRequest("edit", "{\"path\":\"notes.txt\",\"oldString\":\"hello\",\"newString\":\"bye\",\"replaceAll\":\"maybe\"}"));

        Assert.True(result.IsError);
        Assert.Equal("files-arguments-invalid", result.ErrorCode);
        Assert.Contains("replaceAll", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilesToolSource_Read_AcceptsStringOffsetAndLimit()
    {
        var catalog = new TestExtensionCatalog();
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, new MutableFileExecutionTarget("one\ntwo\nthree"));
        var source = new FilesToolSource(catalog);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding),
            new AgentToolRequest("read", "{\"path\":\"notes.txt\",\"offset\":\"2\",\"limit\":\"1\"}"));

        Assert.False(result.IsError, result.Content);
        Assert.Contains("2: two", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("3: three", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShellToolSource_AcceptsStringTimeoutSeconds()
    {
        var catalog = new TestExtensionCatalog();
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, new ScriptedExecutionTarget("local", "local", [new AgentShellCommandResult(0, "ok")]));
        var source = new ShellToolSource(catalog);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding),
            new AgentToolRequest("shell", "{\"command\":\"echo ok\",\"timeoutSeconds\":\"30\"}"));

        Assert.False(result.IsError, result.Content);
        Assert.Equal("ok", result.Content);
    }

    [Theory]
    [InlineData("glob", "{\"pattern\":\"*.txt\"}")]
    [InlineData("glob", "{\"pattern\":\"*.txt\",\"path\":\".\"}")]
    [InlineData("grep", "{\"pattern\":\"needle\"}")]
    [InlineData("grep", "{\"pattern\":\"needle\",\"path\":\".\"}")]
    public async Task FilesToolSource_DockerSearchPermission_TreatsDotAsDefaultContainerRoot(string toolId, string argumentsJson)
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var target = new DockerExecutionTarget(scope.Context, configService, lifecycle);
        var catalog = new TestExtensionCatalog();
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, target);
        var source = new FilesToolSource(catalog);
        var (workspace, _) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(
            binding.BindingId,
            new DockerExecutionWorkspaceConfig("test-image:latest", null, "/bin/sh"));

        var permission = await source.BuildPermissionRequestAsync(
            new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding),
            new AgentToolRequest(toolId, argumentsJson));

        Assert.NotNull(permission);
        Assert.Equal(AgentPermissionBoundaryIds.ConfiguredScope, permission!.BoundaryId);
        Assert.Equal(".", permission.Path);
    }

    [Fact]
    public async Task FilesToolSource_Glob_UsesDockerFallback_WhenRipgrepIsMissing()
    {
        var catalog = new TestExtensionCatalog();
        var target = new ScriptedExecutionTarget("docker", "docker", [
            new AgentShellCommandResult(127, "rg: not found"),
            new AgentShellCommandResult(0, "/workspace/test/file.txt\n")
        ]);
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, target);
        var source = new FilesToolSource(catalog);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId, "docker");

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding),
            new AgentToolRequest("glob", "{\"pattern\":\"*.txt\"}"));

        Assert.False(result.IsError);
        Assert.Contains("/workspace/test/file.txt", result.Content, StringComparison.Ordinal);
        Assert.Equal(2, target.Commands.Count);
        Assert.StartsWith("find ", target.Commands[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"pattern\":\"*.txt\"}", "./*.txt", null)]
    [InlineData("{\"pattern\":\"**/*.tsx\",\"path\":\"/workspace/younics-web\"}", "/workspace/younics-web/**/*.tsx", "/workspace/younics-web/*.tsx")]
    [InlineData("{\"pattern\":\"app/**/page.tsx\",\"path\":\"/workspace/younics-web\"}", "/workspace/younics-web/app/**/page.tsx", "/workspace/younics-web/app/page.tsx")]
    [InlineData("{\"pattern\":\"younics-web/**/*.tsx\",\"path\":\"\"}", "./younics-web/**/*.tsx", "./younics-web/*.tsx")]
    public async Task FilesToolSource_Glob_DockerFallback_UsesPathPatterns(
        string argumentsJson,
        string expectedPrimaryPattern,
        string? expectedZeroDirectoryPattern)
    {
        var catalog = new TestExtensionCatalog();
        var target = new ScriptedExecutionTarget("docker", "docker", [
            new AgentShellCommandResult(127, "rg: not found"),
            new AgentShellCommandResult(0, "/workspace/younics-web/app/page.tsx\n")
        ]);
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, target);
        var source = new FilesToolSource(catalog);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId, "docker");

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding),
            new AgentToolRequest("glob", argumentsJson));

        Assert.False(result.IsError);
        Assert.Equal(2, target.Commands.Count);
        Assert.StartsWith("rg ", target.Commands[0], StringComparison.Ordinal);
        Assert.Contains("--files", target.Commands[0], StringComparison.Ordinal);
        Assert.Contains("-g", target.Commands[0], StringComparison.Ordinal);
        Assert.Contains("--", target.Commands[0], StringComparison.Ordinal);
        Assert.StartsWith("find ", target.Commands[1], StringComparison.Ordinal);
        Assert.Contains("-path", target.Commands[1], StringComparison.Ordinal);
        Assert.DoesNotContain(" -name ", target.Commands[1], StringComparison.Ordinal);
        Assert.Contains(expectedPrimaryPattern, target.Commands[1], StringComparison.Ordinal);
        if (expectedZeroDirectoryPattern is not null)
        {
            Assert.Contains(expectedZeroDirectoryPattern, target.Commands[1], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task FilesToolSource_Glob_DockerFallback_FiltersFindSlashOvermatches()
    {
        var catalog = new TestExtensionCatalog();
        var target = new ScriptedExecutionTarget("docker", "docker", [
            new AgentShellCommandResult(127, "rg: not found"),
            new AgentShellCommandResult(0, "./src/Root.cs\n./src/Nested/File.cs\n")
        ]);
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, target);
        var source = new FilesToolSource(catalog);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId, "docker");

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding),
            new AgentToolRequest("glob", "{\"pattern\":\"src/*.cs\"}"));

        Assert.False(result.IsError);
        Assert.Contains("./src/Root.cs", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("./src/Nested/File.cs", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilesToolSource_Grep_UsesDockerFallback_WhenRipgrepIsMissing()
    {
        var catalog = new TestExtensionCatalog();
        var target = new ScriptedExecutionTarget("docker", "docker", [
            new AgentShellCommandResult(127, "rg: not found"),
            new AgentShellCommandResult(0, "/workspace/test/file.txt:2:needle here\n")
        ]);
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, target);
        var source = new FilesToolSource(catalog);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId, "docker");

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding),
            new AgentToolRequest("grep", "{\"pattern\":\"needle\"}"));

        Assert.False(result.IsError);
        Assert.Contains("needle here", result.Content, StringComparison.Ordinal);
        Assert.Equal(2, target.Commands.Count);
        Assert.StartsWith("grep ", target.Commands[1], StringComparison.Ordinal);
        Assert.Contains("'-E'", target.Commands[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("class|record|interface")]
    [InlineData("Build[A-Za-z]+\\(")]
    public async Task FilesToolSource_Grep_DockerFallback_UsesExtendedRegex(string pattern)
    {
        var catalog = new TestExtensionCatalog();
        var target = new ProcessScriptedExecutionTarget("docker", "docker", [
            new AgentShellCommandResult(127, "rg: not found"),
            new AgentShellCommandResult(0, "/workspace/app/file.cs:7:match here\n")
        ]);
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, target);
        var source = new FilesToolSource(catalog);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        var argumentsJson = JsonSerializer.Serialize(new { pattern, path = "app" });

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding),
            new AgentToolRequest("grep", argumentsJson));

        Assert.False(result.IsError);
        Assert.Equal(2, target.ProcessCommands.Count);
        var fallback = target.ProcessCommands[1];
        Assert.Equal("grep", fallback.FileName);
        Assert.Contains("-E", fallback.Arguments);
        Assert.Contains(pattern, fallback.Arguments);
        Assert.Empty(target.ShellCommands);
    }

    [Fact]
    public async Task FilesToolSource_Grep_DockerFallback_WithInclude_UsesExtendedRegex()
    {
        const string pattern = "<ProjectReference|<PackageReference|<OutputType>";
        var catalog = new TestExtensionCatalog();
        var target = new ProcessScriptedExecutionTarget("docker", "docker", [
            new AgentShellCommandResult(127, "rg: not found"),
            new AgentShellCommandResult(0, "/workspace/app/App.csproj:7:<PackageReference Include=\"Avalonia\" />\n")
        ]);
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, target);
        var source = new FilesToolSource(catalog);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        var argumentsJson = JsonSerializer.Serialize(new { pattern, path = "app", include = "*.csproj" });

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding),
            new AgentToolRequest("grep", argumentsJson));

        Assert.False(result.IsError);
        Assert.Equal(2, target.ProcessCommands.Count);
        var fallback = target.ProcessCommands[1];
        Assert.Equal("find", fallback.FileName);
        Assert.Contains("grep", fallback.Arguments);
        Assert.Contains("-E", fallback.Arguments);
        Assert.Contains(pattern, fallback.Arguments);
        Assert.Contains("*.csproj", fallback.Arguments);
        Assert.Empty(target.ShellCommands);
    }

    [Fact]
    public async Task FilesToolSource_Grep_UsesProcessArguments_ForShellMetacharacterPattern()
    {
        var pattern = "TODO|FIXME|`whoami`|\"quoted\"|'single'|$HOME|test\\path";
        var catalog = new TestExtensionCatalog();
        var target = new ProcessScriptedExecutionTarget("docker", "docker", [
            new AgentShellCommandResult(0, "/workspace/app/file.ts:7:TODO here\n")
        ]);
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, target);
        var source = new FilesToolSource(catalog);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        var argumentsJson = JsonSerializer.Serialize(new { pattern, path = "younics-web/app", include = "*" });

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding),
            new AgentToolRequest("grep", argumentsJson));

        Assert.False(result.IsError);
        var request = Assert.Single(target.ProcessCommands);
        Assert.Equal("rg", request.FileName);
        Assert.Equal(pattern, request.Arguments[^2]);
        Assert.Equal("younics-web/app", request.Arguments[^1]);
        Assert.Empty(target.ShellCommands);
    }

    [Fact]
    public async Task FilesToolSource_Grep_DockerFallback_UsesProcessArguments_ForShellMetacharacterPattern()
    {
        var pattern = "TODO|FIXME|`whoami`|\"quoted\"|'single'|$HOME|test\\path";
        var catalog = new TestExtensionCatalog();
        var target = new ProcessScriptedExecutionTarget("docker", "docker", [
            new AgentShellCommandResult(127, "rg: not found"),
            new AgentShellCommandResult(0, "/workspace/app/file.ts:7:TODO here\n")
        ]);
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, target);
        var source = new FilesToolSource(catalog);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        var argumentsJson = JsonSerializer.Serialize(new { pattern, path = "younics-web/app", include = "*" });

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding),
            new AgentToolRequest("grep", argumentsJson));

        Assert.False(result.IsError);
        Assert.Equal(2, target.ProcessCommands.Count);
        Assert.Equal("rg", target.ProcessCommands[0].FileName);
        Assert.Equal("find", target.ProcessCommands[1].FileName);
        Assert.Contains(pattern, target.ProcessCommands[1].Arguments);
        Assert.Contains("*", target.ProcessCommands[1].Arguments);
        Assert.Contains("-E", target.ProcessCommands[1].Arguments);
        Assert.Empty(target.ShellCommands);
    }

    [Fact]
    public async Task FilesToolSource_ApplyPatchPermission_UsesConfiguredScope_ForSinglePathInsideAllowedRoot()
    {
        using var scope = TestScope.Create();
        var root = Path.Combine(scope.RootPath, "workspace");
        Directory.CreateDirectory(root);
        var configService = new LocalExecutionWorkspaceConfigService(scope.Context);
        var shellCatalogService = new LocalShellCatalogService(scope.Context);
        var catalog = new TestExtensionCatalog();
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, new LocalExecutionTarget(scope.Context, configService, shellCatalogService));
        var source = new FilesToolSource(catalog);
        var (workspace, binding) = await CreateLocalWorkspaceAsync(root, configService);

        var permission = await source.BuildPermissionRequestAsync(
            new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding),
            new AgentToolRequest("apply_patch", ApplyPatchArgs("""
                *** Begin Patch
                *** Add File: inside.txt
                +hello
                *** End Patch
                """)));

        Assert.NotNull(permission);
        Assert.Equal(AgentPermissionBoundaryIds.ConfiguredScope, permission!.BoundaryId);
        Assert.Equal("inside.txt", permission.Path);
        Assert.Equal("apply_patch inside.txt", permission.Summary);
    }

    [Fact]
    public async Task FilesToolSource_ApplyPatchPermission_UsesConfiguredScope_ForMultiplePathsInsideAllowedRoot()
    {
        using var scope = TestScope.Create();
        var root = Path.Combine(scope.RootPath, "workspace");
        Directory.CreateDirectory(root);
        var configService = new LocalExecutionWorkspaceConfigService(scope.Context);
        var shellCatalogService = new LocalShellCatalogService(scope.Context);
        var catalog = new TestExtensionCatalog();
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, new LocalExecutionTarget(scope.Context, configService, shellCatalogService));
        var source = new FilesToolSource(catalog);
        var (workspace, binding) = await CreateLocalWorkspaceAsync(root, configService);

        var permission = await source.BuildPermissionRequestAsync(
            new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding),
            new AgentToolRequest("apply_patch", ApplyPatchArgs("""
                *** Begin Patch
                *** Add File: first.txt
                +first
                *** Add File: nested/second.txt
                +second
                *** End Patch
                """)));

        Assert.NotNull(permission);
        Assert.Equal(AgentPermissionBoundaryIds.ConfiguredScope, permission!.BoundaryId);
        Assert.Null(permission.Path);
        Assert.Equal("apply_patch 2 workspace files", permission.Summary);
    }

    [Fact]
    public async Task FilesToolSource_ApplyPatchPermission_UsesOutsideConfiguredScope_WhenAnyPathEscapesWorkspacePaths()
    {
        using var scope = TestScope.Create();
        var root = Path.Combine(scope.RootPath, "workspace");
        Directory.CreateDirectory(root);
        var outsidePath = Path.Combine(scope.RootPath, "outside.txt");
        var configService = new LocalExecutionWorkspaceConfigService(scope.Context);
        var shellCatalogService = new LocalShellCatalogService(scope.Context);
        var catalog = new TestExtensionCatalog();
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, new LocalExecutionTarget(scope.Context, configService, shellCatalogService));
        var source = new FilesToolSource(catalog);
        var (workspace, binding) = await CreateLocalWorkspaceAsync(root, configService);

        var permission = await source.BuildPermissionRequestAsync(
            new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding),
            new AgentToolRequest("apply_patch", ApplyPatchArgs($"""
                *** Begin Patch
                *** Add File: inside.txt
                +inside
                *** Add File: {outsidePath}
                +outside
                *** End Patch
                """)));

        Assert.NotNull(permission);
        Assert.Equal(AgentPermissionBoundaryIds.OutsideConfiguredScope, permission!.BoundaryId);
        Assert.Null(permission.Path);
        Assert.Equal("apply_patch 2 workspace files", permission.Summary);
    }

    [Fact]
    public async Task FilesToolSource_ApplyPatchPermission_UsesUnknown_WhenPatchCannotBeParsed()
    {
        using var scope = TestScope.Create();
        var root = Path.Combine(scope.RootPath, "workspace");
        Directory.CreateDirectory(root);
        var configService = new LocalExecutionWorkspaceConfigService(scope.Context);
        var shellCatalogService = new LocalShellCatalogService(scope.Context);
        var catalog = new TestExtensionCatalog();
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, new LocalExecutionTarget(scope.Context, configService, shellCatalogService));
        var source = new FilesToolSource(catalog);
        var (workspace, binding) = await CreateLocalWorkspaceAsync(root, configService);

        var permission = await source.BuildPermissionRequestAsync(
            new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding),
            new AgentToolRequest("apply_patch", ApplyPatchArgs("not a patch")));

        Assert.NotNull(permission);
        Assert.Equal(AgentPermissionBoundaryIds.Unknown, permission!.BoundaryId);
        Assert.Null(permission.Path);
        Assert.Equal("apply_patch workspace files", permission.Summary);
    }

    [Fact]
    public async Task ProfileToolCatalog_DoesNotEvaluateWorkspaceReadiness()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var catalog = new TestExtensionCatalog();
        catalog.AddExtension(PackageExtensionPoints.ToolSources, new FilesToolSource(catalog));
        var sessionService = new AgentSessionService(store);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog);
        var toolService = new AgentToolService(new InstalledPackageToolSource(catalog), sessionService, workspaceService, executionTargetService, catalog);

        var tools = await toolService.ListInstalledLocalToolsAsync();

        var readTool = Assert.Single(tools, tool => tool.Descriptor.ToolId == "read");
        Assert.Equal(AgentToolReadinessStatus.Ready, readTool.Readiness.Status);
        Assert.DoesNotContain("workspace is not bound", readTool.Readiness.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AgentToolDescriptor_DefaultPriority_IsMedium()
    {
        var descriptor = new AgentToolDescriptor("test", "Test", "Test tool.");

        Assert.Equal(AgentToolPriority.Medium, descriptor.Priority);
    }

    [Fact]
    public async Task FilesToolSource_ListToolsAsync_UsesMediumPriorityAndRuntimeGuidance()
    {
        var source = new FilesToolSource(new TestExtensionCatalog());

        var tools = await source.ListToolsAsync(new AgentToolSourceContext(null, null, null, null));

        Assert.All(tools, tool => Assert.Equal(AgentToolPriority.Medium, tool.Priority));
        Assert.Contains(tools, tool => tool.ToolId == "read" && tool.RuntimeInstructions?.Contains("Use this tool to read file contents", StringComparison.Ordinal) == true);
        Assert.Contains(tools, tool => tool.ToolId == "grep" && tool.RuntimeInstructions?.Contains("Prefer this over shell commands", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ShellToolSource_ListToolsAsync_UsesLowPriorityAndFallbackGuidance()
    {
        var source = new ShellToolSource(new TestExtensionCatalog());

        var tools = await source.ListToolsAsync(new AgentToolSourceContext(null, null, null, null));
        var shell = Assert.Single(tools);

        Assert.Equal("shell", shell.ToolId);
        Assert.Equal(AgentToolPriority.Low, shell.Priority);
        Assert.Contains("Use this low-priority tool only", shell.RuntimeInstructions ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void FilesToolSource_ApplyPatchPresentation_CompactsHeaderAndKeepsPatchInDetails()
    {
        var source = new FilesToolSource(new TestExtensionCatalog());
        var patchText = """
            *** Begin Patch
            *** Add File: first.txt
            +first
            *** Update File: second.txt
            @@
            -old
            +new
            *** End Patch
            """;

        var presentation = source.ResolveToolPresentation(new AgentToolPresentationRequest(
            "apply_patch",
            ApplyPatchArgs(patchText),
            ResultSummary: null,
            TextContent: "Applied patch.",
            StructuredPayloadJson: null,
            SourcesJson: null,
            IsError: false,
            ErrorCode: null,
            BackendId: null));

        Assert.NotNull(presentation);
        Assert.Equal("Applied 2 patch operations to 2 files", presentation!.HeaderText);
        Assert.Contains("*** Add File: first.txt", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.Equal("Applied patch.", presentation.OutputText);
    }

    [Fact]
    public void FilesToolSource_GlobPresentation_UsesSemanticDetailsAndCleanSummary()
    {
        var catalog = new TestExtensionCatalog();
        var source = new FilesToolSource(catalog);
        catalog.AddExtension(PackageExtensionPoints.ToolSources, source);
        var service = new AgentToolPresentationService(extensionCatalog: catalog);
        var argumentsJson = JsonSerializer.Serialize(new { pattern = "*.html", path = "." });

        var presentation = service.Resolve(CreateToolItem("glob", argumentsJson, textContent: "index.html", resultSummary: "Found 1 match."));

        Assert.Equal("Found 1 match", presentation.HeaderText);
        Assert.Contains("**Request**", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.Contains("Pattern: *.html", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Arguments", presentation.DetailMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentToolInvocationRowViewModel_ApplyResult_UsesOriginalCallArgumentsForDetails()
    {
        var catalog = new TestExtensionCatalog();
        var source = new FilesToolSource(catalog);
        catalog.AddExtension(PackageExtensionPoints.ToolSources, source);
        var service = new AgentToolPresentationService(extensionCatalog: catalog);
        var patchText = """
            *** Begin Patch
            *** Update File: index.html
            @@
            -old
            +new
            *** End Patch
            """;
        var row = new AgentToolInvocationRowViewModel(
            CreateToolTurn(AgentTurnKind.ToolCall),
            CreateToolItem("apply_patch", ApplyPatchArgs(patchText), kind: AgentTurnItemKind.ToolCall),
            service);

        row.ApplyResult(
            CreateToolTurn(),
            CreateToolItem("apply_patch", null, textContent: "Updated index.html", resultSummary: "Applied 1 patch operation to 1 file."));

        Assert.Equal("Updated index.html (+1 -1)", row.HeaderDetailText);
        Assert.Contains("*** Update File: index.html", row.DetailMarkdownBuilder.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Arguments", row.DetailMarkdownBuilder.ToString(), StringComparison.Ordinal);
        Assert.True(row.HasToolDiff);
        Assert.False(row.ShowMarkdownDetails);
    }

    [Fact]
    public void AgentToolInvocationRowViewModel_ApplyPatch_BuildsVisualDiffAndImprovedHeader()
    {
        var catalog = new TestExtensionCatalog();
        var source = new FilesToolSource(catalog);
        catalog.AddExtension(PackageExtensionPoints.ToolSources, source);
        var service = new AgentToolPresentationService(extensionCatalog: catalog);
        var patchText = """
            *** Begin Patch
            *** Update File: /workspace/src/Foo.cs
            @@
            -old
            +new
            *** End Patch
            """;

        var row = new AgentToolInvocationRowViewModel(
            CreateToolTurn(),
            CreateToolItem("apply_patch", ApplyPatchArgs(patchText), textContent: "Updated /workspace/src/Foo.cs", resultSummary: "Applied 1 patch operation to 1 file."),
            service);

        var file = Assert.Single(row.ToolDiffFiles);
        Assert.Equal("Updated src/Foo.cs (+1 -1)", row.HeaderDetailText);
        Assert.Equal("Patch", row.ToolDiffSectionTitle);
        Assert.True(row.HasToolDiff);
        Assert.False(row.ShowMarkdownDetails);
        Assert.Equal("/workspace/src/Foo.cs", file.Path);
        Assert.Equal(1, file.AddedLineCount);
        Assert.Equal(1, file.DeletedLineCount);
        Assert.DoesNotContain(file.Lines, line => line.IsHunk || line.Text.Contains("@@", StringComparison.Ordinal));
        Assert.Contains(file.Lines, line => line.IsDeleted && line.Text == "old");
        Assert.Contains(file.Lines, line => line.IsAdded && line.Text == "new");
    }

    [Fact]
    public void AgentToolInvocationRowViewModel_ApplyPatchFailure_ImprovesHeaderAndKeepsVisualDiff()
    {
        var catalog = new TestExtensionCatalog();
        var source = new FilesToolSource(catalog);
        catalog.AddExtension(PackageExtensionPoints.ToolSources, source);
        var service = new AgentToolPresentationService(extensionCatalog: catalog);
        var patchText = """
            *** Begin Patch
            *** Update File: /workspace/src/Foo.cs
            @@
            -old
            +new
            *** End Patch
            """;

        var row = new AgentToolInvocationRowViewModel(
            CreateToolTurn(),
            CreateToolItem(
                "apply_patch",
                ApplyPatchArgs(patchText),
                textContent: "### File tool failed\n\nPatch hunk did not match the current file content.",
                resultSummary: "Patch hunk did not match the current file content.",
                isError: true),
            service);

        Assert.Equal("Patch failed: hunk did not match the current file content", row.HeaderDetailText);
        Assert.True(row.HasToolDiff);
        Assert.False(row.ShowMarkdownDetails);
    }

    [Fact]
    public void AgentToolInvocationRowViewModel_Edit_BuildsFocusedDiffAndImprovedHeader()
    {
        var service = new AgentToolPresentationService();
        var argumentsJson = JsonSerializer.Serialize(new
        {
            path = "/workspace/src/Foo.cs",
            oldString = "old\nline",
            newString = "new\nline",
            replaceAll = false,
        });

        var row = new AgentToolInvocationRowViewModel(
            CreateToolTurn(),
            CreateToolItem("edit", argumentsJson, textContent: "Wrote 42 character(s).", resultSummary: "Wrote 42 character(s)."),
            service);

        var file = Assert.Single(row.ToolDiffFiles);
        Assert.Equal("Updated src/Foo.cs (+2 -2)", row.HeaderDetailText);
        Assert.Equal("Diff", row.ToolDiffSectionTitle);
        Assert.True(row.HasToolDiff);
        Assert.True(row.ShowMarkdownDetails);
        Assert.DoesNotContain(file.Lines, line => line.IsHunk || line.Text.Contains("@@", StringComparison.Ordinal));
        Assert.Contains(file.Lines, line => line.IsDeleted && line.Text == "old");
        Assert.Contains(file.Lines, line => line.IsAdded && line.Text == "new");
    }

    [Fact]
    public void AgentToolInvocationRowViewModel_MalformedApplyPatch_FallsBackToMarkdownDetails()
    {
        var catalog = new TestExtensionCatalog();
        var source = new FilesToolSource(catalog);
        catalog.AddExtension(PackageExtensionPoints.ToolSources, source);
        var service = new AgentToolPresentationService(extensionCatalog: catalog);

        var row = new AgentToolInvocationRowViewModel(
            CreateToolTurn(),
            CreateToolItem("apply_patch", ApplyPatchArgs("not a patch"), textContent: "failed", resultSummary: null),
            service);

        Assert.False(row.HasToolDiff);
        Assert.True(row.ShowMarkdownDetails);
        Assert.Contains("not a patch", row.DetailMarkdownBuilder.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WebTools_PresentationResolvers_AreResolvedThroughInstalledPackageSource()
    {
        using var scope = TestScope.Create();
        var catalog = new TestExtensionCatalog();
        var settings = new WebToolsSettingsService(scope.Context);
        catalog.AddExtension(PackageExtensionPoints.Tools, new WebSearchTool(new ExaWebSearchBackend(settings), settings));
        catalog.AddExtension(PackageExtensionPoints.Tools, new WebFetchTool(new WebFetchService()));
        var service = new AgentToolPresentationService(new InstalledPackageToolSource(catalog));

        var search = service.Resolve(CreateToolItem("web_search", JsonSerializer.Serialize(new { query = "avalonia docs", maxResults = 3 })));
        var fetch = service.Resolve(CreateToolItem("web_fetch", JsonSerializer.Serialize(new { url = "https://example.com", format = "markdown" })));

        Assert.Equal("avalonia docs", search.HeaderText);
        Assert.Contains("Query: avalonia docs", search.DetailMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Arguments", search.DetailMarkdown, StringComparison.Ordinal);
        Assert.Equal("https://example.com", fetch.HeaderText);
        Assert.Contains("URL: https://example.com", fetch.DetailMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Arguments", fetch.DetailMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellToolSource_Presentation_CompactsLongCommandAndKeepsCommandInDetails()
    {
        var source = new ShellToolSource(new TestExtensionCatalog());
        var command = string.Join('\n', Enumerable.Range(1, 8).Select(index => $"echo line-{index}"));
        var argumentsJson = JsonSerializer.Serialize(new { command });

        var presentation = source.ResolveToolPresentation(new AgentToolPresentationRequest(
            "shell",
            argumentsJson,
            ResultSummary: null,
            TextContent: "done",
            StructuredPayloadJson: null,
            SourcesJson: null,
            IsError: false,
            ErrorCode: null,
            BackendId: null));

        Assert.NotNull(presentation);
        Assert.Equal("command: 8 lines", presentation!.HeaderText);
        Assert.Contains("echo line-8", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.Equal("done", presentation.OutputText);
    }

    [Fact]
    public void AgentToolPresentationService_Fallback_CompactsUnknownMultilineArguments()
    {
        var service = new AgentToolPresentationService();
        var argumentsJson = JsonSerializer.Serialize(new { script = string.Join('\n', Enumerable.Range(1, 6).Select(index => $"line-{index}")) });

        var presentation = service.Resolve(CreateToolItem("unknown_tool", argumentsJson));

        Assert.Equal("script: 6 lines", presentation.HeaderText);
        Assert.Contains("line-6", presentation.DetailMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentToolInvocationRowViewModel_ShowsDetailsAndOutputTogether()
    {
        var row = new AgentToolInvocationRowViewModel(
            CreateToolTurn(),
            CreateToolItem("unknown_tool", JsonSerializer.Serialize(new { query = "weather tomorrow" }), textContent: "tool output", resultSummary: "Tool completed."),
            new AgentToolPresentationService());

        Assert.True(row.HasHeaderDetail);
        Assert.True(row.HasMarkdownDetails);
        Assert.True(row.HasOutput);
        Assert.Contains("tool output", row.OutputText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentToolService_ListReadyRuntimeToolsAsync_OrdersToolsByPriorityDescending()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var catalog = new TestExtensionCatalog();
        catalog.AddExtension(PackageExtensionPoints.Tools, new TestTool("low", AgentToolPriority.Low));
        catalog.AddExtension(PackageExtensionPoints.Tools, new TestTool("high", AgentToolPriority.High));
        catalog.AddExtension(PackageExtensionPoints.Tools, new TestTool("medium", AgentToolPriority.Medium));
        var sessionService = new AgentSessionService(store);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog);
        var toolService = new AgentToolService(new InstalledPackageToolSource(catalog), sessionService, workspaceService, executionTargetService, catalog);

        var tools = await toolService.ListReadyRuntimeToolsAsync();

        Assert.Equal(new[] { "high", "medium", "low" }, tools.Select(tool => tool.Descriptor.ToolId).ToArray());
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_CanSaveWorkspaceWithoutAgentProfile()
    {
        using var scope = TestScope.Create();
        var services = CreateWorkspaceViewServices(scope.Context);
        using var viewModel = new AgentWorkspacesViewModel(services.WorkspaceService, services.ExecutionTargetService, services.Catalog);
        await viewModel.InitializeAsync();

        viewModel.CreateWorkspaceCommand.Execute(null);

        Assert.NotNull(viewModel.SelectedWorkspace);
        Assert.True(viewModel.SaveWorkspaceCommand.CanExecute(null));
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_WorkspacePath_UsesTildeDisplayAndPersistsFullPath()
    {
        using var scope = TestScope.Create();
        var services = CreateWorkspaceViewServices(scope.Context);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        using var viewModel = new AgentWorkspacesViewModel(services.WorkspaceService, services.ExecutionTargetService, services.Catalog);
        await viewModel.InitializeAsync();

        viewModel.CreateWorkspaceCommand.Execute(null);
        var workspaceId = viewModel.SelectedWorkspace!.WorkspaceId;
        viewModel.AddWorkspacePath(home);

        Assert.Equal("~", Assert.Single(viewModel.WorkspacePaths).HostPath);

        await viewModel.SaveWorkspaceCommand.ExecuteAsync(null);

        var savedPath = Assert.Single(services.WorkspaceService.GetWorkspace(workspaceId)!.Paths);
        Assert.Equal(Path.GetFullPath(home), savedPath.HostPath);
    }

    private static async Task<(AgentWorkspaceRecord Workspace, AgentWorkspaceBindingRecord Binding)> CreateLocalWorkspaceAsync(string root, LocalExecutionWorkspaceConfigService configService)
    {
        var workspace = CreateWorkspace(root);
        var binding = CreateBinding(workspace.WorkspaceId);
        await configService.SaveConfigAsync(binding.BindingId, new LocalExecutionWorkspaceConfig(null, []));
        return (workspace, binding);
    }

    private static (AgentWorkspaceRecord Workspace, string HostPath) CreateDockerWorkspace(TestScope scope, string name = "docker-workspace")
    {
        var hostPath = Path.Combine(scope.RootPath, name);
        Directory.CreateDirectory(hostPath);
        return (CreateWorkspace(hostPath), hostPath);
    }

    private static AgentWorkspaceRecord CreateWorkspace(params string[] workspacePaths)
    {
        var now = DateTimeOffset.UtcNow;
        const string workspaceId = "local-test";
        var paths = workspacePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select((path, index) => new AgentWorkspacePathRecord(
                Guid.NewGuid().ToString("N"),
                workspaceId,
                Path.GetFullPath(path),
                index == 0,
                index,
                now,
                now))
            .ToArray();
        return new AgentWorkspaceRecord("local-test", "Local Test", null, now, now, paths);
    }

    private static AgentSystemPromptRequest CreateSystemPromptRequest(AgentWorkspaceRecord? workspace)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentSystemPromptRequest(
            new AgentSessionRecord(Guid.NewGuid(), "Test Session", AgentSessionState.Active, now, now),
            new AgentProfileRecord("profile", "Test Profile", null, null, "provider", "model", null, null, now, now, [], []),
            "provider",
            "model",
            new AgentProviderRunCapabilities(
                SupportsNativeToolCalling: true,
                SupportsStreamingToolCalls: true,
                SupportsMultipleToolCalls: true,
                Summary: "Test capabilities."),
            workspace,
            null,
            [],
            [],
            Guid.NewGuid(),
            RunRevision: 1,
            now,
            "Test user message.");
    }

    private static AgentEditorSectionViewModel CreateEditorSectionViewModel(params AgentEditorField[] fields)
    {
        var workspace = CreateWorkspace();
        return new AgentEditorSectionViewModel(
            new TestWorkspaceEditorContributor("docker"),
            new AgentWorkspaceEditorContext(workspace, "docker", CreateBinding(workspace.WorkspaceId, "docker").BindingId),
            new AgentEditorSection("test-section", "Test Section", null, fields));
    }

    private static IReadOnlyList<string> GetTableColumns(string databasePath, string tableName)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(2));
        while (!condition())
        {
            timeoutCts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeoutCts.Token);
        }
    }

    private static string ApplyPatchArgs(string patchText)
        => JsonSerializer.Serialize(new { patchText });

    private static AgentTurnRecord CreateToolTurn(AgentTurnKind kind = AgentTurnKind.ToolResult)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentTurnRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            AgentMessageRole.Assistant,
            kind,
            [],
            now,
            now);
    }

    private static AgentTurnItemRecord CreateToolItem(
        string toolId,
        string? argumentsJson,
        string? textContent = null,
        string? resultSummary = null,
        AgentTurnItemKind kind = AgentTurnItemKind.ToolResult,
        bool isError = false,
        string? presentationPayloadJson = null)
    {
        var turnId = Guid.NewGuid();
        return new AgentTurnItemRecord(
            Guid.NewGuid(),
            turnId,
            SequenceNumber: 0,
            kind,
            textContent,
            CallId: "call-1",
            toolId,
            argumentsJson,
            resultSummary,
            StructuredPayloadJson: null,
            SourcesJson: null,
            WasTruncated: false,
            IsError: isError,
            ErrorCode: null,
            BackendId: null,
            PresentationPayloadJson: presentationPayloadJson);
    }

    private static WorkspaceViewServices CreateWorkspaceViewServices(TestPackageContext context)
    {
        var store = new AgentLocalStore(context);
        var catalog = new TestExtensionCatalog();
        var sessionService = new AgentSessionService(store);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog);
        var toolService = new AgentToolService(new InstalledPackageToolSource(catalog), sessionService, workspaceService, executionTargetService, catalog);
        var profileService = new AgentProfileService(store, toolService, catalog);
        return new WorkspaceViewServices(store, catalog, workspaceService, profileService, executionTargetService);
    }

    private static AgentProfileRecord CreateProfile(string profileId)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentProfileRecord(
            profileId,
            "Profile",
            null,
            null,
            null,
            null,
            null,
            null,
            now,
            now,
            []);
    }

    private static AgentWorkspaceBindingRecord CreateBinding(string workspaceId, string contributionId = "local")
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentWorkspaceBindingRecord("binding-test", workspaceId, PackageExtensionPoints.ExecutionTargets.Id, contributionId, "primary-execution-target", true, 0, now, now);
    }

    private static StackFragmentImport ToImportFragment(StackFragmentExport fragment, string contributorId)
        => new(
            fragment.FragmentId,
            "sunder.package.agent",
            contributorId,
            fragment.SchemaId,
            fragment.SchemaVersion,
            fragment.DisplayName,
            fragment.JsonPayload,
            fragment.Description,
            fragment.Files?.Select(file => new StackImportPayloadHandle(
                file.RelativePath,
                file.OpenReadAsync,
                file.Length ?? throw new InvalidOperationException("Test export payload length is required."))).ToArray());

    private sealed record WorkspaceViewServices(
        AgentLocalStore Store,
        TestExtensionCatalog Catalog,
        AgentWorkspaceService WorkspaceService,
        AgentProfileService ProfileService,
        AgentExecutionTargetService ExecutionTargetService);

    private sealed class TestTool(string toolId, AgentToolPriority priority) : IAgentTool
    {
        public AgentToolDescriptor Descriptor { get; } = new(
            toolId,
            toolId,
            "Test tool.",
            Priority: priority);

        public ValueTask<AgentToolReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentToolReadiness(Descriptor.ToolId, AgentToolReadinessStatus.Ready, "Ready."));

        public ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            AgentToolRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentToolResult(request.ToolId, "Executed.", Content: "Executed."));
    }

    private sealed class StaticToolSource(
        string sourceId,
        string sourceKind,
        string displayName,
        IReadOnlyList<AgentToolDescriptor> descriptors) : IAgentToolSource
    {
        public string SourceId { get; } = sourceId;

        public string DisplayName { get; } = displayName;

        public string SourceKind { get; } = sourceKind;

        public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(
            AgentToolSourceContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(descriptors);

        public ValueTask<AgentToolReadiness?> GetReadinessAsync(
            string toolId,
            AgentToolSourceContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentToolReadiness?>(descriptors.Any(descriptor => string.Equals(descriptor.ToolId, toolId, StringComparison.OrdinalIgnoreCase))
                ? new AgentToolReadiness(toolId, AgentToolReadinessStatus.Ready, "Ready.")
                : null);

        public ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            AgentToolRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentToolResult(request.ToolId, "Executed.", Content: "Executed."));
    }

    private sealed class DelegateProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private sealed class FakeBackgroundProcessQueue : IBackgroundProcessQueue
    {
        private readonly List<BackgroundProcessSnapshot> _snapshots = [];

        public event EventHandler<BackgroundProcessChangedEventArgs>? ProcessChanged
        {
            add { }
            remove { }
        }

        public List<BackgroundProcessRequest> Requests { get; } = [];

        public BackgroundProcessSnapshot Enqueue(BackgroundProcessRequest request)
        {
            Requests.Add(request);
            var snapshot = new BackgroundProcessSnapshot(
                Guid.NewGuid(),
                request.Title,
                request.GroupKey,
                request.Indicator,
                request.ConcurrencyMode,
                BackgroundProcessState.Queued,
                "Queued",
                null,
                request.CanCancel,
                request.Metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                null,
                DateTimeOffset.UtcNow,
                null,
                null);
            _snapshots.Add(snapshot);
            return snapshot;
        }

        public IReadOnlyList<BackgroundProcessSnapshot> ListProcesses(string? groupKey = null)
            => _snapshots
                .Where(snapshot => string.IsNullOrWhiteSpace(groupKey)
                                   || string.Equals(snapshot.GroupKey, groupKey, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        public bool Cancel(Guid processId)
            => _snapshots.Any(snapshot => snapshot.ProcessId == processId);
    }

    private static string ConfigureFakeDockerCli(TestScope scope)
    {
        var dockerPath = Path.Combine(scope.RootPath, "bin", OperatingSystem.IsWindows() ? "docker.exe" : "docker");
        Directory.CreateDirectory(Path.GetDirectoryName(dockerPath)!);
        File.WriteAllText(dockerPath, string.Empty);
        ((TestSettings)scope.Context.Settings).Seed(DockerCli.ExecutablePathConfigurationKey, dockerPath);
        return dockerPath;
    }

    private sealed class TestScope : IDisposable
    {
        private TestScope(string rootPath)
        {
            RootPath = rootPath;
            Context = new TestPackageContext(rootPath);
        }

        public string RootPath { get; }

        public TestPackageContext Context { get; }

        public static TestScope Create()
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "sunder-execution-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            return new TestScope(rootPath);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private sealed class TestExtensionCatalog : IPackageExtensionCatalog, IPackageExtensionCatalogMonitor
    {
        private readonly Dictionary<string, List<object>> _extensions = new(StringComparer.OrdinalIgnoreCase);
        private long _revision;

        public event EventHandler<PackageExtensionCatalogChangedEventArgs>? Changed;

        public void AddExtension<TContract>(PackageExtensionPoint<TContract> extensionPoint, TContract extension)
        {
            if (!_extensions.TryGetValue(extensionPoint.Id, out var entries))
            {
                entries = [];
                _extensions[extensionPoint.Id] = entries;
            }

            entries.Add(extension!);
            var args = new PackageExtensionCatalogChangedEventArgs(
                Interlocked.Increment(ref _revision),
                PackageExtensionCatalogChangeReason.PackageActivated,
                [new PackageExtensionChange("test.package", extensionPoint.Id, PackageExtensionChangeKind.Added, extension?.GetType())]);
            Changed?.Invoke(this, args);
        }

        public IReadOnlyList<TContract> GetExtensions<TContract>(PackageExtensionPoint<TContract> extensionPoint)
            => !_extensions.TryGetValue(extensionPoint.Id, out var entries)
                ? []
                : entries.Cast<TContract>().ToArray();

        public IReadOnlyList<PackageExtensionContribution<TContract>> GetExtensionContributions<TContract>(PackageExtensionPoint<TContract> extensionPoint)
            => GetExtensions(extensionPoint)
                .Select(extension => new PackageExtensionContribution<TContract>("test.package", extension))
                .ToArray();
    }

    private sealed class FakeExecutionTarget(string shellOutput) : IAgentExecutionTarget
    {
        public AgentExecutionTargetDescriptor Descriptor { get; } = new("local", "local", "Fake", null, SupportsShell: true, SupportsFiles: true, SupportsSearch: true);

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionTargetReadiness("local", "local", AgentExecutionTargetReadinessStatus.Ready, "Ready."));

        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionShellDescriptor("sh", "POSIX sh", "/bin/sh", AgentShellSyntaxKinds.PosixSh, "Run POSIX shell commands."));

        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(AgentExecutionTargetContext context, string path, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentResolvedResource("file", path, path, AgentPermissionBoundaryIds.ConfiguredScope, true));

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(AgentExecutionTargetContext context, AgentShellCommandRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentShellCommandResult(0, shellOutput));

        public ValueTask<AgentFileReadResult> ReadFileAsync(AgentExecutionTargetContext context, AgentFileReadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> WriteFileAsync(AgentExecutionTargetContext context, AgentFileWriteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> DeleteFileAsync(AgentExecutionTargetContext context, AgentFileDeleteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class MutableFileExecutionTarget(string content) : IAgentExecutionTarget
    {
        public AgentExecutionTargetDescriptor Descriptor { get; } = new("local", "local", "Mutable File Target", null, SupportsShell: true, SupportsFiles: true, SupportsSearch: true);

        public string Content { get; private set; } = content;

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionTargetReadiness("local", "local", AgentExecutionTargetReadinessStatus.Ready, "Ready."));

        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionShellDescriptor("sh", "POSIX sh", "/bin/sh", AgentShellSyntaxKinds.PosixSh, "Run POSIX shell commands."));

        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(AgentExecutionTargetContext context, string path, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentResolvedResource("file", path, path, AgentPermissionBoundaryIds.ConfiguredScope, true));

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(AgentExecutionTargetContext context, AgentShellCommandRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileReadResult> ReadFileAsync(AgentExecutionTargetContext context, AgentFileReadRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentFileReadResult(request.Path, Content));

        public ValueTask<AgentFileMutationResult> WriteFileAsync(AgentExecutionTargetContext context, AgentFileWriteRequest request, CancellationToken cancellationToken = default)
        {
            Content = request.Content;
            return ValueTask.FromResult(new AgentFileMutationResult(request.Path, $"Wrote {request.Content.Length} character(s)."));
        }

        public ValueTask<AgentFileMutationResult> DeleteFileAsync(AgentExecutionTargetContext context, AgentFileDeleteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class TestWorkspaceEditorContributor(string targetId) : IAgentWorkspaceEditorContributor
    {
        public string ContributorId => "test-workspace-editor";

        public bool CanEdit(AgentWorkspaceEditorContext context)
            => string.Equals(context.TargetId, targetId, StringComparison.OrdinalIgnoreCase);

        public ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(
            AgentWorkspaceEditorContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentEditorSection>>(
            [
                new AgentEditorSection("test-editor", "Test Editor", null, []),
            ]);

        public ValueTask<AgentEditorSaveResult> SaveSectionAsync(
            AgentWorkspaceEditorContext context,
            AgentEditorSaveRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(AgentEditorSaveResult.Ok("Saved."));
    }

    private sealed class CountingWorkspaceEditorContributor(string targetId, AgentEditorSaveResult? saveResult = null) : IAgentWorkspaceEditorContributor
    {
        private int _getSectionsCallCount;
        private int _saveSectionCallCount;

        public string ContributorId => "counting-workspace-editor";

        public int GetSectionsCallCount => Volatile.Read(ref _getSectionsCallCount);

        public int SaveSectionCallCount => Volatile.Read(ref _saveSectionCallCount);

        public bool CanEdit(AgentWorkspaceEditorContext context)
            => string.Equals(context.TargetId, targetId, StringComparison.OrdinalIgnoreCase);

        public ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(
            AgentWorkspaceEditorContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _getSectionsCallCount);
            return ValueTask.FromResult<IReadOnlyList<AgentEditorSection>>(
            [
                new AgentEditorSection("counting-editor", "Counting Editor", null, []),
            ]);
        }

        public ValueTask<AgentEditorSaveResult> SaveSectionAsync(
            AgentWorkspaceEditorContext context,
            AgentEditorSaveRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saveSectionCallCount);
            return ValueTask.FromResult(saveResult ?? AgentEditorSaveResult.Ok("Saved."));
        }
    }

    private sealed class BlockingWorkspaceEditorContributor(string targetId)
        : IAgentWorkspaceEditorContributor
    {
        private readonly TaskCompletionSource _sectionsLoaded = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _saveEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSave = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string ContributorId => "blocking-workspace-editor";

        public Task SectionsLoaded => _sectionsLoaded.Task;

        public Task SaveEntered => _saveEntered.Task;

        public string? SavedWorkspaceId { get; private set; }

        public bool CanEdit(AgentWorkspaceEditorContext context)
            => string.Equals(context.TargetId, targetId, StringComparison.OrdinalIgnoreCase);

        public ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(
            AgentWorkspaceEditorContext context,
            CancellationToken cancellationToken = default)
        {
            _sectionsLoaded.TrySetResult();
            return ValueTask.FromResult<IReadOnlyList<AgentEditorSection>>(
            [
                new AgentEditorSection("blocking-editor", "Blocking Editor", null, []),
            ]);
        }

        public async ValueTask<AgentEditorSaveResult> SaveSectionAsync(
            AgentWorkspaceEditorContext context,
            AgentEditorSaveRequest request,
            CancellationToken cancellationToken = default)
        {
            SavedWorkspaceId = context.Workspace.WorkspaceId;
            _saveEntered.TrySetResult();
            await _releaseSave.Task.WaitAsync(cancellationToken);
            return AgentEditorSaveResult.Ok("Saved.");
        }

        public void ReleaseSave() => _releaseSave.TrySetResult();
    }

    private sealed class ScriptedExecutionTarget(string targetKind, string targetId, IEnumerable<AgentShellCommandResult> results) : IAgentExecutionTarget
    {
        private readonly Queue<AgentShellCommandResult> _results = new(results);

        public AgentExecutionTargetDescriptor Descriptor { get; } = new(targetKind, targetId, "Scripted Target", null, SupportsShell: true, SupportsFiles: true, SupportsSearch: true);

        public List<string> Commands { get; } = [];

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionTargetReadiness(Descriptor.TargetKind, Descriptor.TargetId, AgentExecutionTargetReadinessStatus.Ready, "Ready."));

        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionShellDescriptor("sh", "POSIX sh", "/bin/sh", AgentShellSyntaxKinds.PosixSh, "Run POSIX shell commands."));

        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(AgentExecutionTargetContext context, string path, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentResolvedResource("file", path, path, AgentPermissionBoundaryIds.ConfiguredScope, true));

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(AgentExecutionTargetContext context, AgentShellCommandRequest request, CancellationToken cancellationToken = default)
        {
            Commands.Add(request.Command);
            return ValueTask.FromResult(_results.Count == 0
                ? new AgentShellCommandResult(0, string.Empty)
                : _results.Dequeue());
        }

        public ValueTask<AgentFileReadResult> ReadFileAsync(AgentExecutionTargetContext context, AgentFileReadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> WriteFileAsync(AgentExecutionTargetContext context, AgentFileWriteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> DeleteFileAsync(AgentExecutionTargetContext context, AgentFileDeleteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ProcessScriptedExecutionTarget(string targetKind, string targetId, IEnumerable<AgentShellCommandResult> results) : IAgentProcessExecutionTarget
    {
        private readonly Queue<AgentShellCommandResult> _results = new(results);

        public AgentExecutionTargetDescriptor Descriptor { get; } = new(targetKind, targetId, "Process Scripted Target", null, SupportsShell: true, SupportsFiles: true, SupportsSearch: true);

        public List<string> ShellCommands { get; } = [];

        public List<AgentProcessCommandRequest> ProcessCommands { get; } = [];

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionTargetReadiness(Descriptor.TargetKind, Descriptor.TargetId, AgentExecutionTargetReadinessStatus.Ready, "Ready."));

        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionShellDescriptor("sh", "POSIX sh", "/bin/sh", AgentShellSyntaxKinds.PosixSh, "Run POSIX shell commands."));

        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(AgentExecutionTargetContext context, string path, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentResolvedResource("file", path, path, AgentPermissionBoundaryIds.ConfiguredScope, true));

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(AgentExecutionTargetContext context, AgentShellCommandRequest request, CancellationToken cancellationToken = default)
        {
            ShellCommands.Add(request.Command);
            return ValueTask.FromResult(_results.Count == 0
                ? new AgentShellCommandResult(0, string.Empty)
                : _results.Dequeue());
        }

        public ValueTask<AgentShellCommandResult> ExecuteProcessAsync(AgentExecutionTargetContext context, AgentProcessCommandRequest request, CancellationToken cancellationToken = default)
        {
            ProcessCommands.Add(request);
            return ValueTask.FromResult(_results.Count == 0
                ? new AgentShellCommandResult(0, string.Empty)
                : _results.Dequeue());
        }

        public ValueTask<AgentFileReadResult> ReadFileAsync(AgentExecutionTargetContext context, AgentFileReadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> WriteFileAsync(AgentExecutionTargetContext context, AgentFileWriteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> DeleteFileAsync(AgentExecutionTargetContext context, AgentFileDeleteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class CountingExecutionTarget(string targetId) : IAgentExecutionTarget
    {
        private int _readinessCallCount;

        public AgentExecutionTargetDescriptor Descriptor { get; } = new("test", targetId, "Counting Target", null, SupportsShell: true, SupportsFiles: true, SupportsSearch: true);

        public int ReadinessCallCount => Volatile.Read(ref _readinessCallCount);

        public AgentExecutionTargetContext? LastContext { get; private set; }

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readinessCallCount);
            LastContext = context;
            return ValueTask.FromResult(new AgentExecutionTargetReadiness(Descriptor.TargetKind, Descriptor.TargetId, AgentExecutionTargetReadinessStatus.Ready, "Ready."));
        }

        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionShellDescriptor("sh", "POSIX sh", "/bin/sh", AgentShellSyntaxKinds.PosixSh, "Run POSIX shell commands."));

        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(AgentExecutionTargetContext context, string path, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentResolvedResource("file", path, path, AgentPermissionBoundaryIds.ConfiguredScope, true));

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(AgentExecutionTargetContext context, AgentShellCommandRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileReadResult> ReadFileAsync(AgentExecutionTargetContext context, AgentFileReadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> WriteFileAsync(AgentExecutionTargetContext context, AgentFileWriteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> DeleteFileAsync(AgentExecutionTargetContext context, AgentFileDeleteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class TestPackageContext(string rootPath) : IPackageContext
    {
        public string PackageId => "test";

        public string Version { get; } = "1.0.0";

        public string ContentRootPath => AppContext.BaseDirectory;

        public IPackageStorageContext Storage { get; } = new TestStorageContext(rootPath);

        public IPackageSettings Settings { get; } = new TestSettings();

        public IPackageSecrets Secrets { get; } = new TestSecrets();


        public Sunder.Sdk.Logging.IPackageLogging Logging { get; } = Sunder.Sdk.Logging.NullPackageLogging.Instance;
    }

    private sealed class TestStorageContext(string rootPath) : IPackageStorageContext
    {
        public IPackageFileStore Files { get; } = new TestPackageFileStore(Path.Combine(rootPath, "files"));

        public IPackageKeyValueStore State { get; } = new TestKeyValueStore();
        public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; } = new TestPackageRoleLocalWorkspace(rootPath);
    }

    private sealed class TestPackageFileStore(string rootPath) : TestPackageFileStoreBase(rootPath);

    private sealed class TestKeyValueStore : IPackageKeyValueStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(_values.GetValueOrDefault(key));

        public void Seed(string key, string value) => _values[key] = value;

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(_values.ContainsKey(key));

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(_values.Keys.Where(key => prefix is null || key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    private sealed class TestPermissionSurface : IAgentPermissionSurface
    {
        public string SurfaceId => "test";

        public string DisplayName => "Test";

        public IReadOnlyList<AgentPermissionActionDescriptor> ListActions()
            =>
            [
                new("shell.execute", "Execute shell commands", "Run shell commands.",
                [
                    new(AgentPermissionBoundaryIds.SelectedExecutionTarget, "Commands in selected workspace target", "Commands run by the selected execution target.", AgentPermissionDecision.Ask),
                ]),
            ];
    }

    private sealed class FakeDockerCliRunner(
        IPackageContext context,
        Func<IReadOnlyList<string>, int, CancellationToken, string?, IProgress<string>?, Task<DockerCliRunResult>> run)
        : DockerCliRunner(context)
    {
        public override Task<DockerCliRunResult> RunAsync(
            IReadOnlyList<string> args,
            int timeoutSeconds,
            CancellationToken cancellationToken,
            string? standardInput = null,
            IProgress<string>? progress = null)
            => run(args, timeoutSeconds, cancellationToken, standardInput, progress);
    }

    private sealed class TestSettings : IPackageSettings
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal)
        {
            ["shell.timeoutSeconds.default"] = "30",
        };

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(_values.GetValueOrDefault(key));
        public Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default)
            => GetValueAsync(key, cancellationToken);
        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public void Seed(string key, string value) => _values[key] = value;
    }

    private sealed class TestSecrets : InMemoryPackageSecrets;
}
