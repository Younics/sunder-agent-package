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
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Package.Agent.Storage;
using Sunder.Package.Agent.Tools.Files;
using Sunder.Package.Agent.Tools.Shell;
using Sunder.Package.Agent.Tools.Web;
using Sunder.Package.Agent.Tools.Web.Backends;
using Sunder.Package.Agent.Tools.Web.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Storage;
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
    public async Task AgentProfileStackContributor_ExportAsync_UsesExplicitDetailsAndMinimumVersion()
    {
        using var scope = TestScope.Create();
        var services = CreateWorkspaceViewServices(scope.Context);
        using var profileService = services.ProfileService;
        services.Store.SaveProfile(CreateProfile("profile.stack") with
        {
            DisplayName = "Stack Profile",
            Description = "Profile description.",
            Instructions = "Use concise responses.",
            ChatProviderId = "openai",
            ChatModelId = "gpt-test",
        });
        var contributor = new AgentProfileStackContributor(
            profileService,
            scope.Context,
            services.Catalog,
            services.Catalog.BehaviorLoops);

        var item = Assert.Single(await contributor.ListExportItemsAsync(
            new StackExportDiscoveryContext(scope.Context.PackageId)));
        var contribution = await contributor.ExportAsync(new StackExportRequest(
            [new StackExportItemSelection("profile.stack")]));

        Assert.All(item.Details ?? [], detail => Assert.NotNull(detail.Sensitivity));
        Assert.Single(contribution.Fragments);
        var requirement = Assert.Single(contribution.PackageRequirements);
        Assert.Equal("sunder.package.agent", requirement.PackageId);
        Assert.Equal("1.1.0", requirement.MinimumVersion);
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
        var selections = new List<StackExportItemSelection>
        {
            new("workspace.stack"),
        };
        var request = new StackExportRequest(selections);
        selections.Clear();

        var item = Assert.Single(await contributor.ListExportItemsAsync(
            new StackExportDiscoveryContext(scope.Context.PackageId)));
        var contribution = await contributor.ExportAsync(request);

        var fragment = Assert.Single(contribution.Fragments);
        Assert.Single(request.ItemSelections);
        Assert.Throws<NotSupportedException>(() => ((IList<StackExportItemSelection>)request.ItemSelections).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<StackFragmentExport>)contribution.Fragments).Clear());
        Assert.All(item.Details ?? [], detail => Assert.NotNull(detail.Sensitivity));
        var requirement = Assert.Single(contribution.PackageRequirements);
        Assert.Equal("sunder.package.agent", requirement.PackageId);
        Assert.Equal("1.1.0", requirement.MinimumVersion);
        Assert.Equal("sunder.package.agent.workspaces", contributor.ContributorId);
        Assert.Empty(fragment.RequiredInputs ?? []);
        Assert.Contains(workspaceRoot, fragment.JsonPayload, StringComparison.Ordinal);
        Assert.Contains(documentPath, fragment.JsonPayload, StringComparison.Ordinal);
        Assert.Contains("Stack Workspace", fragment.JsonPayload, StringComparison.Ordinal);
        Assert.Contains("local", fragment.JsonPayload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentWorkspaceStackContributor_ExportAsync_DoesNotInventThirdPartyMinimumVersion()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var service = new AgentWorkspaceService(store);
        var workspace = service.CreateWorkspace("Third-party target workspace");
        service.SavePrimaryExecutionBinding(workspace.WorkspaceId, "contoso-target");
        var catalog = new TestExtensionCatalog();
        catalog.AddProvider(
            AgentRpcServices.ExecutionTargets,
            new CountingExecutionTarget("contoso-target"),
            packageId: "contoso.execution");
        var contributor = new AgentWorkspaceStackContributor(service, scope.Context, catalog);

        var contribution = await contributor.ExportAsync(new StackExportRequest(
            [new StackExportItemSelection(workspace.WorkspaceId)]));

        var core = Assert.Single(
            contribution.PackageRequirements,
            requirement => requirement.PackageId == "sunder.package.agent");
        var thirdParty = Assert.Single(
            contribution.PackageRequirements,
            requirement => requirement.PackageId == "contoso.execution");
        Assert.Equal("1.1.0", core.MinimumVersion);
        Assert.Null(thirdParty.CreatedWithVersion);
        Assert.Null(thirdParty.MinimumVersion);
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
        var fragment = Assert.Single((await sourceContributor.ExportAsync(new StackExportRequest(
            [new StackExportItemSelection("workspace.stack")]))).Fragments);

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
    public async Task AgentWorkspaceStackContributor_ImportFailureRollsBackWorkspaceAndBindingTogether()
    {
        using var scope = TestScope.Create();
        var sourceContext = new TestPackageContext(Path.Combine(scope.RootPath, "source-atomic"));
        var sourceService = new AgentWorkspaceService(new AgentLocalStore(sourceContext));
        sourceService.ImportWorkspace(
            new AgentWorkspaceRecord("workspace.atomic", "Atomic Workspace", null, default, default),
            [new AgentWorkspacePathRecord("root", "workspace.atomic", scope.RootPath, true, 0, default, default)],
            primaryExecutionTargetId: "local");
        var sourceContributor = new AgentWorkspaceStackContributor(
            sourceService,
            sourceContext,
            new TestExtensionCatalog());
        var fragment = Assert.Single((await sourceContributor.ExportAsync(new StackExportRequest(
            [new StackExportItemSelection("workspace.atomic")]))).Fragments);
        var importFragment = ToImportFragment(fragment, sourceContributor.ContributorId);

        var targetContext = new TestPackageContext(Path.Combine(scope.RootPath, "target-atomic"));
        var targetStore = new AgentLocalStore(targetContext)
        {
            WorkspaceAggregateStageCompleted = stage =>
            {
                if (stage == "bindings")
                {
                    throw new InvalidOperationException("injected binding-stage failure");
                }
            },
        };
        var targetService = new AgentWorkspaceService(targetStore);
        var targetContributor = new AgentWorkspaceStackContributor(
            targetService,
            targetContext,
            new TestExtensionCatalog());
        var preview = await targetContributor.PreviewImportAsync(new StackImportPreviewRequest(
            [importFragment],
            new Dictionary<string, string>(),
            new Dictionary<string, string>()));

        var result = await targetContributor.ImportAsync(new StackImportRequest(
            [importFragment],
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            [Assert.Single(preview.Actions).ActionId]));

        Assert.Equal(StackImportOutcome.Failed, result.Outcome);
        Assert.Null(targetService.GetWorkspace("workspace.atomic"));
        Assert.Empty(targetService.ListBindings("workspace.atomic"));
    }

    [Fact]
    public async Task WorkspaceDocumentationContextService_ContributeContextAsync_LoadsOnlyExplicitDocsInConfiguredOrder()
    {
        using var scope = TestScope.Create();
        var workspaceRoot = Path.Combine(scope.RootPath, "repo");
        var docsRoot = Path.Combine(workspaceRoot, ".sunder", "docs");
        var explicitDocumentPath = Path.Combine(scope.RootPath, "project-guide.md");
        var autoDocumentPath = Path.Combine(docsRoot, "implicit-guide.md");
        var explicitNestedDocumentPath = Path.Combine(docsRoot, "explicit-guide.md");
        Directory.CreateDirectory(docsRoot);
        File.WriteAllText(explicitDocumentPath, "Explicit project guidance.");
        File.WriteAllText(autoDocumentPath, "Auto workspace guidance.");
        File.WriteAllText(explicitNestedDocumentPath, "Explicit nested guidance.");
        var now = DateTimeOffset.UtcNow;
        var workspace = new AgentWorkspaceRecord(
            "workspace.docs",
            "Docs Workspace",
            null,
            now,
            now,
            [new AgentWorkspacePathRecord("path", "workspace.docs", workspaceRoot, true, 0, now, now)],
            [
                new AgentWorkspaceDocumentRecord("doc-nested", "workspace.docs", explicitNestedDocumentPath, 0, now, now),
                new AgentWorkspaceDocumentRecord("doc", "workspace.docs", explicitDocumentPath, 1, now, now),
            ]);
        var service = new WorkspaceDocumentationContextService();

        var contribution = await service.ContributeContextAsync(CreatePromptContextRequest(workspace));

        var block = Assert.Single(Assert.IsType<AgentPromptContextContribution>(contribution).Blocks);
        Assert.Equal("Workspace Documentation", block.Title);
        Assert.Equal(AgentPromptContextUsage.Reference, block.Usage);
        Assert.True(
            block.Content.IndexOf("explicit-guide.md", StringComparison.Ordinal)
            < block.Content.IndexOf("project-guide.md", StringComparison.Ordinal));
        Assert.Contains("project-guide.md", block.Content, StringComparison.Ordinal);
        Assert.Contains("Explicit project guidance.", block.Content, StringComparison.Ordinal);
        Assert.Contains("explicit-guide.md", block.Content, StringComparison.Ordinal);
        Assert.Contains("Explicit nested guidance.", block.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("implicit-guide.md", block.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Auto workspace guidance.", block.Content, StringComparison.Ordinal);
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
    public void AgentLocalStore_GetTranscriptToolDetailJoinsExactCallAndResultWithoutHydratingPages()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store);
        var workspace = CreateWorkspace();
        var argumentsJson = JsonSerializer.Serialize(new { path = "src/App.cs" });
        var presentationJson = JsonSerializer.Serialize(new { schema = "sunder.file-diff.v1" });
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
        var runId = Guid.NewGuid();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = store.DatabasePath,
            Pooling = false,
        }.ToString()))
        using (var command = connection.CreateCommand())
        {
            connection.Open();
            command.CommandText = """
                UPDATE AgentTurns
                SET RunId = $runId, RunRevision = 7
                WHERE TurnId IN ($callTurnId, $resultTurnId);
                """;
            command.Parameters.AddWithValue("$runId", runId.ToString());
            command.Parameters.AddWithValue("$callTurnId", callTurn.TurnId.ToString());
            command.Parameters.AddWithValue("$resultTurnId", resultTurn.TurnId.ToString());
            command.ExecuteNonQuery();
        }

        var fromCallItem = store.GetTranscriptToolDetail(new AgentTranscriptToolDetailRequest(
            session.SessionId,
            ItemId: callItem.ItemId));
        var fromResultItem = store.GetTranscriptToolDetail(new AgentTranscriptToolDetailRequest(
            session.SessionId,
            ItemId: resultItem.ItemId));
        var fromCallId = store.GetTranscriptToolDetail(new AgentTranscriptToolDetailRequest(
            session.SessionId,
            CallId: "detail-call",
            RunId: runId,
            RunRevision: 7));

        Assert.NotNull(fromCallItem);
        Assert.Equal(fromCallItem, fromResultItem);
        Assert.Equal(fromCallItem, fromCallId);
        Assert.Equal(callItem.ItemId, fromCallItem!.CallItemId);
        Assert.Equal(resultItem.ItemId, fromCallItem.ResultItemId);
        Assert.Equal(argumentsJson, fromCallItem.ArgumentsJson);
        Assert.Equal("updated", fromCallItem.OutputText);
        Assert.Equal("Updated src/App.cs.", fromCallItem.ResultSummary);
        Assert.Equal("{\"changed\":true}", fromCallItem.StructuredPayloadJson);
        Assert.Equal(presentationJson, fromCallItem.PresentationPayloadJson);
        Assert.Equal("local", fromCallItem.BackendId);
        Assert.Equal(runId, fromCallItem.RunId);
        Assert.Equal(7, fromCallItem.RunRevision);
        var latestTimestamp = callTurn.UpdatedAtUtc > resultTurn.UpdatedAtUtc
            ? callTurn.UpdatedAtUtc
            : resultTurn.UpdatedAtUtc;
        Assert.Equal(
            (latestTimestamp.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks) / 10,
            fromCallItem.Revision);
        Assert.Null(store.GetTranscriptToolDetail(new AgentTranscriptToolDetailRequest(
            Guid.NewGuid(),
            CallId: "detail-call")));
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
        catalog.AddProvider(AgentRpcServices.ToolSources, new StaticToolSource("mcp", "mcp", "Model Context Protocol", [toolDescriptor]));
        var toolService = new AgentToolService(sessionService, workspaceService, executionTargetService, catalog);
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
    public async Task DockerExecutionWorkspaceConfigService_GetConfig_LeavesImageUnconfigured()
    {
        using var scope = TestScope.Create();
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);

        var config = await configService.GetConfigAsync("workspace:primary-execution-target");

        Assert.Null(config.ImageReference);
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

    public static TheoryData<string> ImportedBindingIds => new()
    {
        "workspace:binding",
        "workspace / binding",
        "workspace-\u65E5\u672C\u8A9E-\u00E9",
        " workspace\twith whitespace ",
        new string('b', PackageStorageValidation.MaximumKeyLength + 300),
    };

    [Theory]
    [MemberData(nameof(ImportedBindingIds))]
    public async Task LocalWorkspaceConfig_UsesPortablePhysicalKeyForOpaqueBindingId(string bindingId)
    {
        using var scope = TestScope.Create();
        var service = new LocalExecutionWorkspaceConfigService(scope.Context);

        await service.SaveConfigAsync(bindingId, new LocalExecutionWorkspaceConfig("bash", []));
        var loaded = await service.GetConfigAsync(bindingId);

        Assert.Equal("bash", loaded.SelectedShellId);
        var key = Assert.Single(await scope.Context.Storage.State.ListKeysAsync());
        Assert.Equal(LocalExecutionWorkspaceConfigService.BuildKey(bindingId), key);
        Assert.True(PackageStorageValidation.IsValidKey(key));
    }

    [Theory]
    [MemberData(nameof(ImportedBindingIds))]
    public async Task DockerWorkspaceConfig_UsesPortablePhysicalKeyForOpaqueBindingId(string bindingId)
    {
        using var scope = TestScope.Create();
        var service = new DockerExecutionWorkspaceConfigService(scope.Context);

        await service.SaveConfigAsync(
            bindingId,
            new DockerExecutionWorkspaceConfig(null, null, "/bin/sh", []));
        var loaded = await service.GetConfigAsync(bindingId);

        Assert.Equal("/bin/sh", loaded.ShellPath);
        var keys = await scope.Context.Storage.State.ListKeysAsync();
        Assert.Contains(DockerExecutionWorkspaceConfigService.BuildKey(bindingId), keys);
        Assert.All(keys, key => Assert.True(PackageStorageValidation.IsValidKey(key)));
    }

    [Fact]
    public async Task DockerImageCatalogService_StartsEmpty()
    {
        using var scope = TestScope.Create();
        var imageCatalog = new DockerImageCatalogService(scope.Context);

        Assert.Empty(await imageCatalog.ListImagesAsync());
        Assert.Empty(await new DockerImageCatalogService(scope.Context).ListImagesAsync());
        Assert.Null((await new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog).GetConfigAsync("workspace:primary-execution-target")).ImageReference);
        Assert.All(
            await scope.Context.Storage.State.ListKeysAsync(),
            key => Assert.True(PackageStorageValidation.IsValidKey(key)));
    }

    [Theory]
    [InlineData("custom")]
    public async Task DockerImageCatalogService_RejectsTaglessImageReferences(string imageReference)
    {
        using var scope = TestScope.Create();
        var imageCatalog = new DockerImageCatalogService(scope.Context);

        var exception = await Assert.ThrowsAsync<DockerExecutionDomainException>(() => imageCatalog.AddImageAsync(imageReference));

        Assert.StartsWith("docker.image-reference.", exception.Code, StringComparison.Ordinal);
        Assert.Contains("explicit tag or sha256 digest", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await imageCatalog.ListImagesAsync());
    }

    [Fact]
    public async Task DockerImageStackContributor_ExportAsync_ExportsSelectedImageReferencesOnly()
    {
        using var scope = TestScope.Create();
        var imageCatalog = new DockerImageCatalogService(scope.Context);
        await imageCatalog.SaveImagesAsync(
        [
            new DockerImageDefinition("custom:1.0", DockerImageStatus.Ready, DateTimeOffset.UtcNow, "ready"),
            new DockerImageDefinition("other:1.0", DockerImageStatus.NotPulled, null, null),
        ]);
        var contributor = new DockerImageStackContributor(imageCatalog, scope.Context);

        var item = Assert.Single(await contributor.ListExportItemsAsync(
            new StackExportDiscoveryContext(scope.Context.PackageId)));
        var contribution = await contributor.ExportAsync(new StackExportRequest(
            [new StackExportItemSelection("docker-images",
            [
                new StackExportDetailSelection("custom:1.0"),
                new StackExportDetailSelection("other:1.0", IsSelected: false),
            ])]));

        var fragment = Assert.Single(contribution.Fragments);
        Assert.All(item.Details ?? [], detail => Assert.NotNull(detail.Sensitivity));
        Assert.Equal("docker-images", fragment.FragmentId);
        Assert.Contains("custom:1.0", fragment.JsonPayload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("other:1.0", fragment.JsonPayload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ready", fragment.JsonPayload, StringComparison.OrdinalIgnoreCase);
        var requirement = Assert.Single(contribution.PackageRequirements);
        Assert.Equal(scope.Context.PackageId, requirement.PackageId);
        Assert.Equal("1.1.0", requirement.MinimumVersion);
    }

    [Fact]
    public async Task DockerImageStackContributor_ImportAsync_ConfiguresReferencesWithoutMarkingReady()
    {
        using var sourceScope = TestScope.Create();
        var sourceCatalog = new DockerImageCatalogService(sourceScope.Context);
        await sourceCatalog.SaveImagesAsync([new DockerImageDefinition("custom:1.0", DockerImageStatus.Ready, DateTimeOffset.UtcNow, "ready")]);
        var sourceContributor = new DockerImageStackContributor(sourceCatalog, sourceScope.Context);
        var fragment = Assert.Single((await sourceContributor.ExportAsync(new StackExportRequest(
            [new StackExportItemSelection("docker-images")]))).Fragments);

        using var targetScope = TestScope.Create();
        var targetCatalog = new DockerImageCatalogService(targetScope.Context);
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
        Assert.Equal("custom:1.0", image.ImageReference);
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
        await imageCatalog.AddImageAsync("custom:1.0");

        var result = await imageCatalog.PullImageAsync("custom:1.0", new DelegateProgress(progressLines.Add));

        Assert.True(result.Success, result.Message);
        Assert.Equal(DockerImageStatus.Ready, (await imageCatalog.ListImagesAsync()).Single(image => image.ImageReference == "custom:1.0").Status);
        Assert.Contains(progressLines, line => line.Contains("pulling layer", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(calls, args => args.SequenceEqual(["pull", "custom:1.0"]));
        Assert.Contains(calls, args => args.SequenceEqual(["image", "inspect", "custom:1.0"]));
    }

    [Fact]
    public async Task DockerExecutionTarget_WriteFileAsync_UsesApprovedVerifiedHostBind()
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var runner = CreateReadyDockerCliRunner(scope.Context);
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var mountVerifier = new PassThroughDockerMountIdentityVerifier();
        var target = new DockerExecutionTarget(
            scope.Context,
            configService,
            lifecycle,
            imageCatalogService: null,
            runner,
            mountVerifier);
        var (workspace, hostPath) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(
            binding.BindingId,
            new DockerExecutionWorkspaceConfig("test-image:1.0", "sunder-agent-test", "/bin/sh"));
        var content = string.Join("\n", Enumerable.Range(0, 5000).Select(index => $"line-{index}"));

        var context = new AgentExecutionTargetContext(null, null, workspace, binding);
        var (resource, approvedContext) = await ApproveDockerResourceAsync(
            target,
            context,
            "app/page.tsx",
            "files.mutate");
        var result = await target.WriteFileAsync(
            approvedContext,
            new AgentFileWriteRequest("app/page.tsx", content));
        var replay = await target.WriteFileAsync(
            approvedContext,
            new AgentFileWriteRequest("app/page.tsx", "replay"));

        Assert.False(result.IsError, result.Summary);
        Assert.Empty(resource.AuthorityReferences);
        Assert.Equal(AgentToolResultErrorCodes.PermissionReapprovalRequired, replay.ErrorCode);
        Assert.True(mountVerifier.VerifyCount >= 3);
        Assert.Equal(content, await File.ReadAllTextAsync(Path.Combine(hostPath, "app", "page.tsx")));
    }

    [Fact]
    public async Task DockerExecutionTarget_ReadFileAsync_ReportsMissingFileLikeLocal()
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var runner = CreateReadyDockerCliRunner(scope.Context);
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var target = new DockerExecutionTarget(
            scope.Context,
            configService,
            lifecycle,
            imageCatalogService: null,
            runner,
            new PassThroughDockerMountIdentityVerifier());
        var (workspace, _) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(binding.BindingId, new DockerExecutionWorkspaceConfig("test-image:1.0", "sunder-agent-test", "/bin/sh"));
        var containerRoot = (await target.GetExecutionScopeAsync(new AgentExecutionTargetContext(null, null, workspace, binding))).DefaultWorkingDirectory!;

        var context = new AgentExecutionTargetContext(null, null, workspace, binding);
        var (_, approvedContext) = await ApproveDockerResourceAsync(
            target,
            context,
            "missing.txt",
            "files.read");
        var result = await target.ReadFileAsync(
            approvedContext,
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
        var runner = CreateReadyDockerCliRunner(scope.Context);
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var target = new DockerExecutionTarget(
            scope.Context,
            configService,
            lifecycle,
            imageCatalogService: null,
            runner,
            new PassThroughDockerMountIdentityVerifier());
        var (workspace, hostPath) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(binding.BindingId, new DockerExecutionWorkspaceConfig("test-image:1.0", "sunder-agent-test", "/bin/sh"));
        await File.WriteAllBytesAsync(Path.Combine(hostPath, "image.png"), [1, 0, 2]);

        var context = new AgentExecutionTargetContext(null, null, workspace, binding);
        var (_, approvedContext) = await ApproveDockerResourceAsync(
            target,
            context,
            "image.png",
            "files.read");
        var result = await target.ReadFileAsync(
            approvedContext,
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
        var runner = CreateReadyDockerCliRunner(scope.Context);
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var target = new DockerExecutionTarget(
            scope.Context,
            configService,
            lifecycle,
            imageCatalogService: null,
            runner,
            new PassThroughDockerMountIdentityVerifier());
        var (workspace, _) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(binding.BindingId, new DockerExecutionWorkspaceConfig("test-image:1.0", "sunder-agent-test", "/bin/sh"));
        var containerRoot = (await target.GetExecutionScopeAsync(new AgentExecutionTargetContext(null, null, workspace, binding))).DefaultWorkingDirectory!;

        var context = new AgentExecutionTargetContext(null, null, workspace, binding);
        var (_, approvedContext) = await ApproveDockerResourceAsync(
            target,
            context,
            "missing.txt",
            "files.mutate");
        var result = await target.DeleteFileAsync(
            approvedContext,
            new AgentFileDeleteRequest("missing.txt"));

        Assert.True(result.IsError);
        Assert.Equal("path-not-found", result.ErrorCode);
        Assert.Equal("Path does not exist.", result.Summary);
        Assert.Equal($"{containerRoot}/missing.txt", result.Path);
    }

    [Fact]
    public async Task DockerExecutionTarget_OutsideApprovalContextCannotBypassHostBindRequirement()
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var runner = CreateReadyDockerCliRunner(scope.Context);
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var target = new DockerExecutionTarget(
            scope.Context,
            configService,
            lifecycle,
            imageCatalogService: null,
            runner,
            new PassThroughDockerMountIdentityVerifier());
        var (workspace, _) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(
            binding.BindingId,
            new DockerExecutionWorkspaceConfig("test-image:1.0", "sunder-agent-test", "/bin/sh"));
        var context = new AgentExecutionTargetContext(
            null,
            null,
            workspace,
            binding,
            AllowOutsideConfiguredScope: true)
        {
            ApprovedResourceReferences = ["docker-resource-v1:legacy"],
        };

        var result = await target.ReadFileAsync(
            context,
            new AgentFileReadRequest("/container-private/secret.txt"));

        Assert.True(result.IsError);
        Assert.Equal(DockerPathResolver.StructuredBindRequiredErrorCode, result.ErrorCode);
        Assert.Equal(DockerPathResolver.StructuredBindRequiredMessage, result.ErrorMessage);
    }

    [Fact]
    public async Task DockerExecutionTarget_ScopedDiscoveryUsesVerifiedHostBind()
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var runner = CreateReadyDockerCliRunner(scope.Context);
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var target = new DockerExecutionTarget(
            scope.Context,
            configService,
            lifecycle,
            imageCatalogService: null,
            runner,
            new PassThroughDockerMountIdentityVerifier());
        var (workspace, hostPath) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(
            binding.BindingId,
            new DockerExecutionWorkspaceConfig("test-image:1.0", "sunder-agent-test", "/bin/sh"));
        await File.WriteAllTextAsync(Path.Combine(hostPath, "AGENTS.md"), "container policy");
        var context = new AgentExecutionTargetContext(null, null, workspace, binding);
        var containerRoot = (await target.GetExecutionScopeAsync(context)).DefaultWorkingDirectory!;

        var result = await target.DiscoverScopedInstructionsAsync(
            context,
            new AgentScopedInstructionDiscoveryRequest(
                [new AgentScopedInstructionProbe(containerRoot, IsDirectory: true)]));

        var document = Assert.Single(Assert.Single(result.Scopes).Documents);
        Assert.Equal($"{containerRoot}/AGENTS.md", document.Path);
        Assert.Equal("container policy", document.Content);
    }

    [Fact]
    public async Task DockerExecutionTarget_RealDaemon_VerifiesMountAndContainerCanReadNestedHostCreation()
    {
        if (OperatingSystem.IsWindows()
            || !string.Equals(
                Environment.GetEnvironmentVariable("SUNDER_DOCKER_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var scope = TestScope.Create();
        await using var lifecycle = new DockerContainerLifecycleService();
        var runner = new DockerCliRunner(scope.Context);
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var target = new DockerExecutionTarget(scope.Context, configService, lifecycle, dockerCliRunner: runner);
        var (workspace, hostPath) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        var container = "sunder-strict-integration-" + Guid.NewGuid().ToString("N");
        await configService.SaveConfigAsync(
            binding.BindingId,
            new DockerExecutionWorkspaceConfig("node:22-alpine", container, "/bin/sh"));
        var context = new AgentExecutionTargetContext(null, null, workspace, binding);
        try
        {
            var (_, approvedContext) = await ApproveDockerResourceAsync(
                target,
                context,
                "nested/file.txt",
                "files.mutate");
            var write = await target.WriteFileAsync(
                approvedContext,
                new AgentFileWriteRequest("nested/file.txt", "container-readable", Overwrite: false));
            Assert.False(write.IsError, write.Summary);

            var scopeDescriptor = await target.GetExecutionScopeAsync(context);
            var shell = await target.ExecuteShellAsync(
                context,
                new AgentShellCommandRequest(
                    $"cat {DockerCommandRunner.Quote(scopeDescriptor.DefaultWorkingDirectory + "/nested/file.txt")}"));
            Assert.Equal(0, shell.ExitCode);
            Assert.Contains("container-readable", shell.Output, StringComparison.Ordinal);
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(hostPath, "nested", "file.txt")) & (UnixFileMode)0x0fff);
        }
        finally
        {
            await runner.RunAsync(["rm", "-f", container], 30, CancellationToken.None);
        }
    }

    [Fact]
    public async Task DockerExecutionTarget_PinsExplicitEndpointAcrossAmbientChange()
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var ambientEndpoint = "unix:///first.sock";
        var endpointCalls = 0;
        var runner = new FakeDockerCliRunner(scope.Context, (args, _, _, _, _) =>
        {
            if (args.Count > 0 && args[0] == "info")
            {
                return Task.FromResult(new DockerCliRunResult(0, "test-daemon", false, false));
            }
            if (args.Count > 1 && args[0] == "image" && args[1] == "inspect")
            {
                return Task.FromResult(new DockerCliRunResult(
                    0,
                    "sha256:" + new string('a', 64),
                    false,
                    false));
            }
            if (args.Count > 0 && args[0] == "inspect")
            {
                return Task.FromResult(new DockerCliRunResult(1, string.Empty, false, false));
            }
            return Task.FromResult(new DockerCliRunResult(0, string.Empty, false, false));
        }, () =>
        {
            endpointCalls++;
            return ambientEndpoint;
        });
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var target = new DockerExecutionTarget(
            scope.Context,
            configService,
            lifecycle,
            imageCatalogService: null,
            runner,
            new PassThroughDockerMountIdentityVerifier());
        var (workspace, _) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(
            binding.BindingId,
            new DockerExecutionWorkspaceConfig("test-image:1.0", "sunder-agent-test", "/bin/sh"));
        var context = new AgentExecutionTargetContext(null, null, workspace, binding);
        var (_, approvedContext) = await ApproveDockerResourceAsync(
            target,
            context,
            "file.txt",
            "files.read");
        ambientEndpoint = "unix:///second.sock";

        var result = await target.ReadFileAsync(
            approvedContext,
            new AgentFileReadRequest("file.txt"));

        Assert.Equal(AgentFileReadErrorCodes.FileNotFound, result.ErrorCode);
        Assert.Equal(1, endpointCalls);
        Assert.NotEmpty(runner.RawCalls);
        Assert.All(runner.RawCalls, args =>
        {
            Assert.True(args.Count >= 3);
            Assert.Equal("--host", args[0]);
            Assert.Equal("unix:///first.sock", args[1]);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DockerExecutionTarget_VerifiesMountAfterCreateAndReuseOrStart(bool runningOnSecondAcquire)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var inspectCount = 0;
        string? signature = null;
        IReadOnlyList<string>? runArgs = null;
        var runner = new FakeDockerCliRunner(scope.Context, (args, _, _, _, _) =>
        {
            if (args.Count > 0 && args[0] == "context")
            {
                return Task.FromResult(new DockerCliRunResult(0, "unix:///var/run/docker.sock", false, false));
            }
            if (args.Count > 0 && args[0] == "info")
            {
                return Task.FromResult(new DockerCliRunResult(0, "test-daemon", false, false));
            }
            if (args.Count > 1 && args[0] == "image" && args[1] == "inspect")
            {
                return Task.FromResult(new DockerCliRunResult(0, "sha256:" + new string('a', 64), false, false));
            }
            if (args.Count > 0 && args[0] == "inspect")
            {
                inspectCount++;
                return Task.FromResult(inspectCount == 1
                    ? new DockerCliRunResult(1, string.Empty, false, false)
                    : new DockerCliRunResult(0, $"{runningOnSecondAcquire.ToString().ToLowerInvariant()} {signature}", false, false));
            }
            if (args.Count > 0 && args[0] == "run")
            {
                runArgs = args.ToArray();
                var labelIndex = args.ToList().IndexOf("--label");
                signature = args[labelIndex + 1].Split('=', 2)[1];
            }
            return Task.FromResult(new DockerCliRunResult(0, string.Empty, false, false));
        });
        var verifier = new PassThroughDockerMountIdentityVerifier();
        var imageRunner = new FakeDockerCliRunner(scope.Context, (_, _, _, _, _) =>
            Task.FromResult(new DockerCliRunResult(0, string.Empty, false, false)));
        var imageCatalog = new DockerImageCatalogService(scope.Context, imageRunner);
        await imageCatalog.AddImageAsync("test-image:1.0");
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        var target = new DockerExecutionTarget(
            scope.Context,
            configService,
            lifecycle,
            imageCatalog,
            runner,
            verifier);
        var (workspace, _) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(
            binding.BindingId,
            new DockerExecutionWorkspaceConfig("test-image:1.0", "sunder-agent-test", "/bin/sh"));
        var context = new AgentExecutionTargetContext(null, null, workspace, binding);

        var first = await target.GetReadinessAsync(context);
        var second = await target.GetReadinessAsync(context);

        Assert.True(first.Status == AgentExecutionTargetReadinessStatus.Ready, first.Message);
        Assert.True(second.Status == AgentExecutionTargetReadinessStatus.Ready, second.Message);

        Assert.Equal(2, verifier.VerifyCount);
        var createdWith = Assert.IsAssignableFrom<IReadOnlyList<string>>(runArgs);
        var userIndex = createdWith.ToList().IndexOf("--user");
        Assert.True(userIndex >= 0);
        Assert.Equal(
            DockerHostAccessPolicy.Resolve().ContainerUser,
            createdWith[userIndex + 1]);
        Assert.Equal(
            runningOnSecondAcquire ? 0 : 1,
            runner.Calls.Count(args => args.Count > 0 && args[0] == "start"));
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

        var startInfo = await DockerCli.CreateStartInfoAsync(scope.Context, ["pull", "custom:1.0"], redirectStandardInput: false);

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
            new DockerExecutionWorkspaceConfig("test-image:1.0", null, "/bin/bash"));

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
            new DockerExecutionWorkspaceConfig("test-image:1.0", null, "/bin/sh"));

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
        var config = new DockerExecutionWorkspaceConfig("test-image:1.0", null, "/bin/sh");

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
            new DockerExecutionWorkspaceConfig("test-image:1.0", null, "/bin/sh"));

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
            var exitCode = string.Equals(image, "custom:1.0", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            return Task.FromResult(new DockerCliRunResult(exitCode, string.Empty, TimedOut: false, WasTruncated: false));
        });
        var imageCatalog = new DockerImageCatalogService(scope.Context, runner);
        await imageCatalog.AddImageAsync("custom:1.0");
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        var contributor = new DockerExecutionWorkspaceEditorContributor(configService, imageCatalog);
        var workspace = CreateWorkspace();
        var bindingId = AgentWorkspaceService.BuildPrimaryBindingId(workspace.WorkspaceId);
        await configService.SaveConfigAsync(
            bindingId,
            new DockerExecutionWorkspaceConfig("custom:1.0", null, "/bin/sh"));

        var sections = await contributor.GetSectionsAsync(new AgentWorkspaceEditorContext(workspace, "docker", bindingId));

        var imageField = sections.Single().Fields.Single(field => field.FieldId == "image");
        Assert.Equal(AgentEditorFieldKind.Select, imageField.Kind);
        Assert.Equal("custom:1.0", imageField.Value);
        var option = Assert.Single(imageField.Options!);
        Assert.Equal("custom:1.0", option.Value);
        Assert.Equal("custom:1.0", option.Label);
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
                    ["image"] = new("missing:1.0"),
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
        await imageCatalog.AddImageAsync("custom:1.0");
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        var contributor = new DockerExecutionWorkspaceEditorContributor(configService, imageCatalog);
        var workspace = CreateWorkspace();

        var result = await contributor.SaveSectionAsync(
            new AgentWorkspaceEditorContext(workspace, "docker", AgentWorkspaceService.BuildPrimaryBindingId(workspace.WorkspaceId)),
            new AgentEditorSaveRequest(
                "docker-execution-settings",
                new Dictionary<string, AgentEditorFieldValue>(StringComparer.OrdinalIgnoreCase)
                {
                    ["image"] = new("custom:1.0"),
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
            Assert.Equal(["image", "inspect", "custom:1.0"], args);
            return Task.FromResult(new DockerCliRunResult(1, "No such image", TimedOut: false, WasTruncated: false));
        });
        var targetRunner = new FakeDockerCliRunner(scope.Context, (_, _, _, _, _) =>
        {
            targetCallCount++;
            return Task.FromResult(new DockerCliRunResult(0, string.Empty, TimedOut: false, WasTruncated: false));
        });
        var imageCatalog = new DockerImageCatalogService(scope.Context, imageRunner);
        await imageCatalog.AddImageAsync("custom:1.0");
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        var target = new DockerExecutionTarget(
            scope.Context,
            configService,
            lifecycle,
            imageCatalog,
            targetRunner,
            new PassThroughDockerMountIdentityVerifier());
        var (workspace, _) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(binding.BindingId, new DockerExecutionWorkspaceConfig("custom:1.0", "sunder-agent-test", "/bin/sh"));

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
            if (args.Count > 0 && string.Equals(args[0], "context", StringComparison.Ordinal))
            {
                return Task.FromResult(new DockerCliRunResult(0, "unix:///var/run/docker.sock", TimedOut: false, WasTruncated: false));
            }
            if (args.Count > 0 && string.Equals(args[0], "info", StringComparison.Ordinal))
            {
                return Task.FromResult(new DockerCliRunResult(0, "test-daemon", TimedOut: false, WasTruncated: false));
            }
            if (args.Count > 1 && args[0] == "image" && args[1] == "inspect")
            {
                return Task.FromResult(new DockerCliRunResult(0, "sha256:" + new string('a', 64), TimedOut: false, WasTruncated: false));
            }
            if (args.Count > 0 && string.Equals(args[0], "inspect", StringComparison.Ordinal))
            {
                return Task.FromResult(new DockerCliRunResult(1, string.Empty, TimedOut: false, WasTruncated: false));
            }

            if (args.Count > 0 && string.Equals(args[0], "run", StringComparison.Ordinal))
            {
                runArgs = args.ToArray();
                return Task.FromResult(new DockerCliRunResult(1, "No such image: custom:1.0", TimedOut: false, WasTruncated: false));
            }

            return Task.FromResult(new DockerCliRunResult(0, string.Empty, TimedOut: false, WasTruncated: false));
        });
        var imageCatalog = new DockerImageCatalogService(scope.Context, imageRunner);
        await imageCatalog.AddImageAsync("custom:1.0");
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        var target = new DockerExecutionTarget(
            scope.Context,
            configService,
            lifecycle,
            imageCatalog,
            targetRunner,
            new PassThroughDockerMountIdentityVerifier());
        var (workspace, _) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(binding.BindingId, new DockerExecutionWorkspaceConfig("custom:1.0", "sunder-agent-test", "/bin/sh"));

        var readiness = await target.GetReadinessAsync(new AgentExecutionTargetContext(null, null, workspace, binding));

        Assert.Equal(AgentExecutionTargetReadinessStatus.Failed, readiness.Status);
        Assert.Contains("failed to start from image 'custom:1.0'", readiness.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No such image", readiness.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--pull", runArgs);
        Assert.Contains("never", runArgs);
    }

    [Fact]
    public async Task DockerExecutionTarget_MutableTagChangeRecreatesContainerByImageId()
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var firstImageId = "sha256:" + new string('a', 64);
        var secondImageId = "sha256:" + new string('b', 64);
        var imageIdentityCallCount = 0;
        var containerInspectCallCount = 0;
        string? firstSignature = null;
        var runCalls = new List<IReadOnlyList<string>>();
        var targetRunner = new FakeDockerCliRunner(scope.Context, (args, _, _, _, _) =>
        {
            if (args.Count > 0 && args[0] == "context")
            {
                return Task.FromResult(new DockerCliRunResult(0, "unix:///var/run/docker.sock", TimedOut: false, WasTruncated: false));
            }
            if (args.Count > 0 && args[0] == "info")
            {
                return Task.FromResult(new DockerCliRunResult(0, "test-daemon", TimedOut: false, WasTruncated: false));
            }
            if (args.Count >= 5
                && args[0] == "image"
                && args[1] == "inspect"
                && args[2] == "--format")
            {
                imageIdentityCallCount++;
                return Task.FromResult(new DockerCliRunResult(
                    0,
                    imageIdentityCallCount == 1 ? firstImageId : secondImageId,
                    TimedOut: false,
                    WasTruncated: false));
            }

            if (args.Count > 0 && args[0] == "inspect")
            {
                containerInspectCallCount++;
                return Task.FromResult(containerInspectCallCount == 1
                    ? new DockerCliRunResult(1, string.Empty, TimedOut: false, WasTruncated: false)
                    : new DockerCliRunResult(0, $"true {firstSignature}", TimedOut: false, WasTruncated: false));
            }

            if (args.Count > 0 && args[0] == "run")
            {
                runCalls.Add(args.ToArray());
                var labelIndex = args.ToList().IndexOf("--label");
                firstSignature ??= args[labelIndex + 1].Split('=', 2)[1];
            }

            return Task.FromResult(new DockerCliRunResult(0, string.Empty, TimedOut: false, WasTruncated: false));
        });
        var imageRunner = new FakeDockerCliRunner(scope.Context, (_, _, _, _, _) =>
            Task.FromResult(new DockerCliRunResult(0, string.Empty, TimedOut: false, WasTruncated: false)));
        var imageCatalog = new DockerImageCatalogService(scope.Context, imageRunner);
        await imageCatalog.AddImageAsync("custom:1.0");
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        var target = new DockerExecutionTarget(
            scope.Context,
            configService,
            lifecycle,
            imageCatalog,
            targetRunner,
            new PassThroughDockerMountIdentityVerifier());
        var (workspace, _) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(
            binding.BindingId,
            new DockerExecutionWorkspaceConfig("custom:1.0", "sunder-agent-test", "/bin/sh"));
        var context = new AgentExecutionTargetContext(null, null, workspace, binding);

        Assert.Equal(AgentExecutionTargetReadinessStatus.Ready, (await target.GetReadinessAsync(context)).Status);
        var generation = await target.GetConfigurationGenerationAsync(context);
        using (var restartedLifecycle = new DockerContainerLifecycleService())
        {
            var restartedTarget = new DockerExecutionTarget(
                scope.Context,
                configService,
                restartedLifecycle,
                imageCatalog,
                targetRunner,
                new PassThroughDockerMountIdentityVerifier());
            Assert.Equal(generation, await restartedTarget.GetConfigurationGenerationAsync(context));
        }
        var staleReadiness = await target.GetReadinessAsync(context with
        {
            ExpectedConfigurationGeneration = generation,
        });
        Assert.Equal(AgentExecutionTargetReadinessStatus.Failed, staleReadiness.Status);
        Assert.Contains("image identity changed", staleReadiness.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AgentExecutionTargetReadinessStatus.Ready, (await target.GetReadinessAsync(context)).Status);

        Assert.Equal(2, runCalls.Count);
        Assert.Contains(firstImageId, runCalls[0]);
        Assert.Contains(secondImageId, runCalls[1]);
        Assert.DoesNotContain("custom:1.0", runCalls[0]);
        Assert.DoesNotContain("custom:1.0", runCalls[1]);
        Assert.Contains(targetRunner.Calls, args => args.SequenceEqual(["rm", "-f", "sunder-agent-test"]));
    }

    [Fact]
    public async Task DockerExecutionTarget_RemovesContainerWhenMountRootChangesDuringCreate()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var imageRunner = new FakeDockerCliRunner(scope.Context, (_, _, _, _, _) =>
            Task.FromResult(new DockerCliRunResult(0, string.Empty, TimedOut: false, WasTruncated: false)));
        var imageCatalog = new DockerImageCatalogService(scope.Context, imageRunner);
        await imageCatalog.AddImageAsync("custom:1.0");
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        var (workspace, hostPath) = CreateDockerWorkspace(scope);
        var parked = hostPath + "-parked";
        var targetRunner = new FakeDockerCliRunner(scope.Context, (args, _, _, _, _) =>
        {
            if (args.Count > 0 && args[0] == "context")
            {
                return Task.FromResult(new DockerCliRunResult(0, "unix:///var/run/docker.sock", TimedOut: false, WasTruncated: false));
            }
            if (args.Count > 0 && args[0] == "info")
            {
                return Task.FromResult(new DockerCliRunResult(0, "test-daemon", TimedOut: false, WasTruncated: false));
            }
            if (args.Count > 1 && args[0] == "image" && args[1] == "inspect")
            {
                return Task.FromResult(new DockerCliRunResult(0, "sha256:" + new string('a', 64), TimedOut: false, WasTruncated: false));
            }
            if (args.Count > 0 && args[0] == "inspect")
            {
                return Task.FromResult(new DockerCliRunResult(1, string.Empty, TimedOut: false, WasTruncated: false));
            }
            if (args.Count > 0 && args[0] == "run")
            {
                Directory.Move(hostPath, parked);
                Directory.CreateDirectory(hostPath);
            }
            return Task.FromResult(new DockerCliRunResult(0, string.Empty, TimedOut: false, WasTruncated: false));
        });
        var target = new DockerExecutionTarget(scope.Context, configService, lifecycle, imageCatalog, targetRunner);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(
            binding.BindingId,
            new DockerExecutionWorkspaceConfig("custom:1.0", "sunder-agent-test", "/bin/sh"));

        var readiness = await target.GetReadinessAsync(
            new AgentExecutionTargetContext(null, null, workspace, binding));

        Assert.Equal(AgentExecutionTargetReadinessStatus.Failed, readiness.Status);
        Assert.Contains("retained-root identity challenge", readiness.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(targetRunner.Calls, args => args.SequenceEqual(["rm", "-f", "sunder-agent-test"]));
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
    public async Task DockerContainerLifecycleService_SerializesContainerAndStructuredOperations()
    {
        using var lifecycle = new DockerContainerLifecycleService(TimeSpan.FromMinutes(1));
        var containerLease = await lifecycle.AcquireAsync(
            "container-key",
            _ => Task.FromResult("container-name"),
            (_, _) => Task.CompletedTask);
        var structuredTask = lifecycle.AcquireOperationAsync("container-key");

        await Task.Delay(25);
        Assert.False(structuredTask.IsCompleted);
        containerLease.Dispose();
        using (await structuredTask)
        {
        }

        var structuredLease = await lifecycle.AcquireOperationAsync("container-key");
        var ensureCount = 0;
        var containerTask = lifecycle.AcquireAsync(
            "container-key",
            _ =>
            {
                Interlocked.Increment(ref ensureCount);
                return Task.FromResult("container-name");
            },
            (_, _) => Task.CompletedTask);
        await Task.Delay(25);
        Assert.False(containerTask.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref ensureCount));
        structuredLease.Dispose();
        using (await containerTask)
        {
        }
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
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
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
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
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
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);

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
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        await WaitUntilAsync(() => viewModel.ExecutionTargets.Any(targetOption => string.Equals(targetOption.TargetId, "docker", StringComparison.OrdinalIgnoreCase)));
        viewModel.SelectedExecutionTarget = viewModel.ExecutionTargets.Single(targetOption => string.Equals(targetOption.TargetId, "docker", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(viewModel.EditorSections);

        catalog.AddProvider(AgentRpcServices.WorkspaceEditors, new TestWorkspaceEditorContributor("docker"));

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
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        catalog.AddProvider(AgentRpcServices.WorkspaceEditors, contributor);
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
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        catalog.AddProvider(AgentRpcServices.WorkspaceEditors, contributor);
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
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        catalog.AddProvider(AgentRpcServices.WorkspaceEditors, contributor);
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
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        catalog.AddProvider(AgentRpcServices.WorkspaceEditors, contributor);
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
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        catalog.AddProvider(AgentRpcServices.WorkspaceEditors, contributor);
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

        Assert.Equal(otherWorkspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
        Assert.Equal("Other Workspace", viewModel.DisplayName);
        Assert.Empty(viewModel.StatusText);
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
    public async Task AgentWorkspacesViewModel_DirtyKeyedDraftSurvivesRuntimeRefreshAndLateSave()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-workspace-draft-tests", Guid.NewGuid().ToString("N"));
        var firstPath = Path.Combine(root, "first");
        var secondPath = Path.Combine(root, "second");
        var firstDocument = Path.Combine(root, "first.md");
        var secondDocument = Path.Combine(root, "second.md");
        Directory.CreateDirectory(firstPath);
        Directory.CreateDirectory(secondPath);
        await File.WriteAllTextAsync(firstDocument, "first");
        await File.WriteAllTextAsync(secondDocument, "second");
        try
        {
            using var scope = TestScope.Create();
            var store = new AgentLocalStore(scope.Context);
            var catalog = new TestExtensionCatalog();
            var target = new CountingExecutionTarget("docker");
            var contributor = new BlockingWorkspaceEditorContributor("docker");
            catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
            catalog.AddProvider(AgentRpcServices.WorkspaceEditors, contributor);
            var workspaceService = new AgentWorkspaceService(store);
            var executionTargetService = new AgentExecutionTargetService(catalog);
            var workspace = workspaceService.CreateWorkspace("Original Workspace");
            workspaceService.SavePrimaryExecutionBinding(workspace.WorkspaceId, "docker");
            using var viewModel = new AgentWorkspacesViewModel(
                workspaceService,
                executionTargetService,
                catalog);
            await viewModel.InitializeAsync();
            await contributor.SectionsLoaded.WaitAsync(TimeSpan.FromSeconds(2));
            var section = Assert.Single(viewModel.EditorSections);
            var contributedField = Assert.IsType<AgentEditorTextFieldViewModel>(Assert.Single(section.Fields));
            viewModel.DisplayName = "Snapshot name";
            viewModel.Description = "Snapshot description";
            viewModel.AddWorkspacePath(firstPath);
            viewModel.AddWorkspaceDocument(firstDocument);
            contributedField.Value = "snapshot contributed value";

            var save = viewModel.SaveWorkspaceCommand.ExecuteAsync(null);
            await contributor.SaveEntered.WaitAsync(TimeSpan.FromSeconds(2));
            viewModel.DisplayName = "Newer draft name";
            viewModel.Description = "Newer draft description";
            viewModel.AddWorkspacePath(secondPath);
            viewModel.AddWorkspaceDocument(secondDocument);
            contributedField.Value = "newer contributed value";
            workspaceService.SaveWorkspace(workspace.WorkspaceId, "Runtime refresh value", "Runtime refresh description");
            await viewModel.CurrentRuntimeRefresh.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal("Newer draft name", viewModel.DisplayName);
            Assert.Equal("Newer draft description", viewModel.Description);
            Assert.Equal(2, viewModel.WorkspacePaths.Count);
            Assert.Equal(2, viewModel.WorkspaceDocuments.Count);
            Assert.Same(section, Assert.Single(viewModel.EditorSections));
            Assert.Equal("newer contributed value", contributedField.Value);

            viewModel.SelectedExecutionTarget = ExecutionTargetOption.Unconfigured;
            contributor.ReleaseSave();
            await save.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(workspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
            Assert.Equal("Newer draft name", viewModel.DisplayName);
            Assert.Equal("Newer draft description", viewModel.Description);
            Assert.Equal(2, viewModel.WorkspacePaths.Count);
            Assert.Equal(2, viewModel.WorkspaceDocuments.Count);
            Assert.True(viewModel.SelectedExecutionTarget?.IsUnconfigured);
            Assert.Contains("remain unsaved", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_ResizeDuringSaveCannotCloseCurrentDraft()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var catalog = new TestExtensionCatalog();
        var target = new CountingExecutionTarget("docker");
        var contributor = new BlockingWorkspaceEditorContributor("docker");
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        catalog.AddProvider(AgentRpcServices.WorkspaceEditors, contributor);
        var workspaceService = new AgentWorkspaceService(store);
        var workspace = workspaceService.CreateWorkspace("Workspace");
        workspaceService.SavePrimaryExecutionBinding(workspace.WorkspaceId, "docker");
        using var viewModel = new AgentWorkspacesViewModel(
            workspaceService,
            new AgentExecutionTargetService(catalog),
            catalog);
        await viewModel.InitializeAsync();
        await contributor.SectionsLoaded.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.DisplayName = "Saved workspace";

        var save = viewModel.SaveWorkspaceCommand.ExecuteAsync(null);
        await contributor.SaveEntered.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.IsCompactLayout = true;
        contributor.ReleaseSave();
        await save.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(workspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
        Assert.True(viewModel.IsEditorActive);
        Assert.True(viewModel.ShowCompactEditor);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_StaleEditorSectionsCannotReplaceNewSelectionSections()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var catalog = new TestExtensionCatalog();
        var target = new CountingExecutionTarget("docker");
        var contributor = new SelectionRaceWorkspaceEditorContributor("docker");
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        catalog.AddProvider(AgentRpcServices.WorkspaceEditors, contributor);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog);
        var first = workspaceService.CreateWorkspace("First Workspace");
        var second = workspaceService.CreateWorkspace("Second Workspace");
        workspaceService.SavePrimaryExecutionBinding(first.WorkspaceId, "docker");
        workspaceService.SavePrimaryExecutionBinding(second.WorkspaceId, "docker");
        using var viewModel = new AgentWorkspacesViewModel(
            workspaceService,
            executionTargetService,
            catalog);
        await viewModel.InitializeAsync();
        await contributor.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var staleRefresh = viewModel.CurrentEditorSectionRefresh;

        viewModel.ActivateWorkspace(viewModel.Workspaces.Single(workspace =>
            workspace.WorkspaceId == second.WorkspaceId));
        var currentRefresh = viewModel.CurrentEditorSectionRefresh;
        await currentRefresh.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Second Workspace", Assert.Single(viewModel.EditorSections).Title);

        contributor.ReleaseFirst.TrySetResult();
        await staleRefresh.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(second.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
        Assert.Equal("Second Workspace", viewModel.DisplayName);
        Assert.Equal("Second Workspace", Assert.Single(viewModel.EditorSections).Title);
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
    public async Task AgentWorkspacesViewModel_FirstEditPromotesAutomaticSelectionBeforeCompactResize()
    {
        using var scope = TestScope.Create();
        var services = CreateWorkspaceViewServices(scope.Context);
        var workspace = services.WorkspaceService.CreateWorkspace("Alpha Workspace");
        using var viewModel = new AgentWorkspacesViewModel(
            services.WorkspaceService,
            services.ExecutionTargetService,
            services.Catalog);
        await viewModel.InitializeAsync();

        viewModel.DisplayName = "Edited automatic workspace";
        viewModel.IsCompactLayout = true;

        Assert.Equal(workspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
        Assert.Equal("Edited automatic workspace", viewModel.DisplayName);
        Assert.True(viewModel.IsEditorActive);
        Assert.True(viewModel.ShowCompactEditor);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_SupersededInitializationRetriesLatestWorkspaceSnapshot()
    {
        var gateway = new BlockingWorkspaceInitializationGateway();
        var catalog = new TestExtensionCatalog();
        using var viewModel = new AgentWorkspacesViewModel(
            gateway,
            new AgentExecutionTargetService(catalog),
            catalog);

        var initialization = viewModel.InitializeAsync();
        try
        {
            await gateway.FirstInitializationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            viewModel.CreateWorkspaceCommand.Execute(null);
            var selectionBeforeRetry = Assert.IsType<AgentWorkspaceRecord>(viewModel.SelectedWorkspace);
            await gateway.SecondInitializationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            viewModel.DisplayName = "Unsaved workspace draft";
            gateway.AdvanceWorkspaceSnapshot();

            gateway.ReleaseSecondInitialization.TrySetResult();
            await initialization.WaitAsync(TimeSpan.FromSeconds(2));
            gateway.ReleaseFirstInitialization.TrySetResult();
            await gateway.FirstInitializationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Yield();

            Assert.Equal(2, gateway.InitializeCount);
            Assert.Equal("ExistingDetail", viewModel.Route.ToString());
            Assert.Equal("Ready", viewModel.DetailPhase.ToString());
            var currentWorkspace = Assert.Single(viewModel.Workspaces);
            Assert.Equal("New Workspace", currentWorkspace.DisplayName);
            Assert.Same(currentWorkspace, viewModel.SelectedWorkspace);
            Assert.NotSame(selectionBeforeRetry, viewModel.SelectedWorkspace);
            Assert.Equal("Unsaved workspace draft", viewModel.DisplayName);
        }
        finally
        {
            gateway.ReleaseSecondInitialization.TrySetResult();
            gateway.ReleaseFirstInitialization.TrySetResult();
        }
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
            new DockerImageDefinition("first:1.0", DockerImageStatus.NotPulled, null, null),
            new DockerImageDefinition("second:1.0", DockerImageStatus.NotPulled, null, null),
        ]);
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        var contributor = new DockerExecutionWorkspaceEditorContributor(configService, imageCatalog);
        var store = new AgentLocalStore(scope.Context);
        var catalog = new TestExtensionCatalog();
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, new CountingExecutionTarget("docker"));
        catalog.AddProvider(AgentRpcServices.WorkspaceEditors, contributor);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog);
        using var viewModel = new AgentWorkspacesViewModel(workspaceService, executionTargetService, catalog);
        await viewModel.InitializeAsync();

        viewModel.CreateWorkspaceCommand.Execute(null);
        viewModel.SelectedExecutionTarget = viewModel.ExecutionTargets.Single(targetOption => string.Equals(targetOption.TargetId, "docker", StringComparison.OrdinalIgnoreCase));
        await WaitUntilAsync(() => viewModel.EditorSections.Any(section => string.Equals(section.SectionId, "docker-execution-settings", StringComparison.OrdinalIgnoreCase)));
        await viewModel.CurrentEditorSectionRefresh.WaitAsync(TimeSpan.FromSeconds(2));

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

        readyImages.Add("second:1.0");
        await viewModel.ExecuteEditorActionAsync(refreshAction);

        Assert.Same(section, viewModel.EditorSections.Single(item => string.Equals(item.SectionId, "docker-execution-settings", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("/custom/sh", shellField.Value);
        var option = Assert.Single(imageField.Options);
        Assert.Equal("second:1.0", option.Value);
        Assert.Equal("second:1.0", imageField.SelectedOption?.Value);
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
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        var sessionService = new AgentSessionService(store);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog);
        var toolService = new AgentToolService(sessionService, workspaceService, executionTargetService, catalog);
        var profileService = new AgentProfileService(store, toolService, catalog, catalog.BehaviorLoops);
        var permissionService = new AgentPermissionService(store, catalog);
        var memoryCoordinator = new AgentMemoryCoordinator(sessionService, catalog);
        var promptComposer = new AgentSystemPromptComposer(catalog);
        var attachmentService = new AgentAttachmentService(scope.Context);
        var behaviorLoop = new DefaultAgentBehaviorLoop(promptComposer, attachmentService);
        var runAttachmentStore = new AgentRunAttachmentStore(attachmentService);
        var activeRunRegistry = new AgentActiveRunRegistry();
        var runEventLogger = new AgentRunEventLogger(scope.Context);
        var providerResolver = new AgentRunProviderResolver(profileService, catalog);
        var behaviorLoopResolver = new AgentBehaviorLoopResolver(catalog, catalog.BehaviorLoops);
        var stopCoordinator = new AgentRunStopCoordinator(sessionService, permissionService, memoryCoordinator, activeRunRegistry, profileService);
        var behaviorLoopHostFactory = new AgentBehaviorLoopHostFactory(sessionService, toolService, permissionService, memoryCoordinator, runEventLogger, activeRunRegistry, behaviorLoop);
        var runPreparationService = new AgentRunPreparationService(sessionService, profileService, workspaceService, runAttachmentStore, runEventLogger, providerResolver);
        var runStartService = new AgentRunStartService(
            sessionService,
            activeRunRegistry,
            runEventLogger);
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

        catalog.AddProvider(AgentRpcServices.PermissionSurfaces, new TestPermissionSurface());
        permissions.SaveOverride("shell.execute", AgentPermissionBoundaryIds.SelectedExecutionTarget, AgentPermissionDecision.Deny);
        permissions.SetSessionUnrestrictedMode(session.SessionId, true);

        var denied = permissions.Evaluate(session.SessionId, new AgentPermissionRequest("shell.execute", AgentPermissionBoundaryIds.SelectedExecutionTarget, "remove", Command: "rm -rf tmp"));
        permissions.SaveOverride("shell.execute", AgentPermissionBoundaryIds.SelectedExecutionTarget, AgentPermissionDecision.Ask);
        var allowed = permissions.Evaluate(session.SessionId, new AgentPermissionRequest("shell.execute", AgentPermissionBoundaryIds.SelectedExecutionTarget, "list", Command: "ls"));

        Assert.Equal(AgentPermissionDecision.Allow, allowed.Decision);
        Assert.Equal(AgentPermissionDecision.Deny, denied.Decision);
        Assert.Equal(AgentPermissionDecision.Ask, allowed.BaseDecision);
        Assert.Equal(AgentPermissionDecisionSource.UnrestrictedMode, allowed.Source);
        Assert.Equal(session.SessionId, allowed.SourceSessionId);
        Assert.Equal(AgentPermissionDecisionSource.ConfiguredOverride, denied.Source);
    }

    [Fact]
    public async Task FilesToolSource_ReturnsStructuredGlobResults()
    {
        using var scope = TestScope.Create();
        var catalog = new TestExtensionCatalog();
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, new FakeExecutionTarget("/workspace/one.txt\0/workspace/two.txt\0"));
        var source = new FilesToolSource(scope.Context);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);

        var result = await source.ExecuteAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
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
        using var scope = TestScope.Create();
        var catalog = new TestExtensionCatalog();
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, new FakeExecutionTarget(string.Empty));
        var source = new FilesToolSource(scope.Context);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);

        var permission = await source.BuildPermissionRequestAsync(
            CreateToolExecutionContext(catalog, workspace, binding) with
            {
                ResourceOperation = CreateResourceOperation("files.search"),
            },
            new AgentToolRequest(toolId, argumentsJson));

        Assert.NotNull(permission);
        Assert.Equal("files.search", permission!.ActionId);
        Assert.Equal(AgentPermissionBoundaryIds.ConfiguredScope, permission.BoundaryId);
        Assert.Equal(".", permission.Path);
    }

    [Fact]
    public async Task FilesToolSource_Edit_AcceptsStringReplaceAll()
    {
        using var scope = TestScope.Create();
        var catalog = new TestExtensionCatalog();
        var target = new MutableFileExecutionTarget("hello hello");
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        var source = new FilesToolSource(scope.Context);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);

        var result = await source.ExecuteAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest("edit", "{\"path\":\"notes.txt\",\"oldString\":\"hello\",\"newString\":\"bye\",\"replaceAll\":\"true\"}"));

        Assert.False(result.IsError, result.Content);
        Assert.Equal("bye bye", target.Content);
    }

    [Fact]
    public async Task FilesToolSource_Edit_EmitsPresentationPayloadWithActualLineNumbers()
    {
        using var scope = TestScope.Create();
        var catalog = new TestExtensionCatalog();
        var target = new MutableFileExecutionTarget("one\ntwo\nold\nsame\nfive");
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        var source = new FilesToolSource(scope.Context);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);
        var argumentsJson = JsonSerializer.Serialize(new
        {
            path = "notes.txt",
            oldString = "old\nsame",
            newString = "new\nsame",
        });

        var result = await source.ExecuteAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest("edit", argumentsJson));

        Assert.False(result.IsError, result.Content);
        Assert.NotNull(result.PresentationPayloadJson);

        using var row = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem("edit", argumentsJson, textContent: result.Content, resultSummary: result.Summary, presentationPayloadJson: result.PresentationPayloadJson),
            new AgentToolPresentationService());
        var details = await TranscriptToolTestHarness.ExpandAsync(row);
        var file = Assert.Single(details.ToolDiffFiles);

        Assert.Equal("Wrote 21 character(s).", row.HeaderDetailText);
        Assert.Contains(file.Lines, line => line.IsDeleted && line.LineNumberText == "3" && line.Text == "old");
        Assert.Contains(file.Lines, line => line.IsAdded && line.LineNumberText == "3" && line.Text == "new");
        Assert.Contains(file.Lines, line => line.IsContext && line.LineNumberText == "4" && line.Text == "same");
        Assert.DoesNotContain(file.Lines, line => line.MarkerText is "+" or "-" or "@@");
    }

    [Fact]
    public async Task FilesToolSource_ApplyPatch_EmitsPresentationPayloadWithActualLineNumbers()
    {
        using var scope = TestScope.Create();
        var catalog = new TestExtensionCatalog();
        var target = new MutableFileExecutionTarget("one\ntwo\nold\nsame\nfive");
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        var source = new FilesToolSource(scope.Context);
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
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest("apply_patch", argumentsJson));

        Assert.False(result.IsError, result.Content);
        Assert.NotNull(result.PresentationPayloadJson);
        Assert.Equal("one\ntwo\nnew\nsame\nfive", target.Content);

        using var row = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem("apply_patch", argumentsJson, textContent: result.Content, resultSummary: result.Summary, presentationPayloadJson: result.PresentationPayloadJson),
            new AgentToolPresentationService());
        var details = await TranscriptToolTestHarness.ExpandAsync(row);
        var file = Assert.Single(details.ToolDiffFiles);

        Assert.Equal("Applied 1 patch operation to 1 file", row.HeaderDetailText);
        Assert.Equal("Patch", details.ToolDiffSectionTitle);
        Assert.False(details.ShowMarkdownDetails);
        Assert.Contains(file.Lines, line => line.IsContext && line.LineNumberText == "2" && line.Text == "two");
        Assert.Contains(file.Lines, line => line.IsDeleted && line.LineNumberText == "3" && line.Text == "old");
        Assert.Contains(file.Lines, line => line.IsAdded && line.LineNumberText == "3" && line.Text == "new");
        Assert.Contains(file.Lines, line => line.IsContext && line.LineNumberText == "4" && line.Text == "same");
        Assert.DoesNotContain(file.Lines, line => line.MarkerText is "+" or "-" or "@@");
    }

    [Fact]
    public async Task FilesToolSource_Edit_InvalidReplaceAll_ReturnsToolError()
    {
        using var scope = TestScope.Create();
        var catalog = new TestExtensionCatalog();
        var target = new MutableFileExecutionTarget("hello");
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        var source = new FilesToolSource(scope.Context);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);

        var result = await source.ExecuteAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest("edit", "{\"path\":\"notes.txt\",\"oldString\":\"hello\",\"newString\":\"bye\",\"replaceAll\":\"maybe\"}"));

        Assert.True(result.IsError);
        Assert.Equal("files-arguments-invalid", result.ErrorCode);
        Assert.Contains("replaceAll", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilesToolSource_Read_AcceptsStringOffsetAndLimit()
    {
        using var scope = TestScope.Create();
        var catalog = new TestExtensionCatalog();
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, new MutableFileExecutionTarget("one\ntwo\nthree"));
        var source = new FilesToolSource(scope.Context);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);

        var result = await source.ExecuteAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest("read", "{\"path\":\"notes.txt\",\"offset\":\"2\",\"limit\":\"1\"}"));

        Assert.False(result.IsError, result.Content);
        Assert.Contains("2: two", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("3: three", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShellToolSource_AcceptsStringTimeoutSeconds()
    {
        var catalog = new TestExtensionCatalog();
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, new ScriptedExecutionTarget("local", "local", [new AgentShellCommandResult(0, "ok")]));
        var source = new ShellToolSource();
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);

        var result = await source.ExecuteAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
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
        var runner = CreateReadyDockerCliRunner(scope.Context);
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var target = new DockerExecutionTarget(
            scope.Context,
            configService,
            lifecycle,
            imageCatalogService: null,
            runner,
            new PassThroughDockerMountIdentityVerifier());
        var catalog = new TestExtensionCatalog();
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        var source = new FilesToolSource(scope.Context);
        var (workspace, _) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(
            binding.BindingId,
            new DockerExecutionWorkspaceConfig("test-image:1.0", null, "/bin/sh"));

        var permission = await source.BuildPermissionRequestAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest(toolId, argumentsJson));

        Assert.NotNull(permission);
        Assert.Equal(AgentPermissionBoundaryIds.ConfiguredScope, permission!.BoundaryId);
        Assert.Equal(".", permission.Path);
        Assert.StartsWith(DockerFileSystemExecutor.ClaimNamespacePrefix, permission.ResourceReference, StringComparison.Ordinal);
        Assert.Single(permission.ResourceClaims);
        Assert.Empty(permission.ResourceCapabilities);
    }

    [Fact]
    public async Task FilesToolSource_Glob_UsesDockerFallback_WhenRipgrepIsMissing()
    {
        using var scope = TestScope.Create();
        var catalog = new TestExtensionCatalog();
        var target = new ScriptedExecutionTarget("docker", "docker", [
            new AgentShellCommandResult(127, "rg: not found"),
            new AgentShellCommandResult(0, "/workspace/test/file.txt\n")
        ]);
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        var source = new FilesToolSource(scope.Context);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId, "docker");

        var result = await source.ExecuteAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest("glob", "{\"pattern\":\"*.txt\"}"));

        Assert.False(result.IsError);
        Assert.Contains("/workspace/test/file.txt", result.Content, StringComparison.Ordinal);
        Assert.Equal(2, target.Commands.Count);
        Assert.StartsWith("find ", target.Commands[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"pattern\":\"*.txt\"}", "/workspace")]
    [InlineData("{\"pattern\":\"**/*.tsx\",\"path\":\"/workspace/younics-web\"}", "/workspace/younics-web")]
    [InlineData("{\"pattern\":\"app/**/page.tsx\",\"path\":\"/workspace/younics-web\"}", "/workspace/younics-web")]
    [InlineData("{\"pattern\":\"younics-web/**/*.tsx\",\"path\":\"\"}", "/workspace")]
    public async Task FilesToolSource_Glob_DockerFallback_BindsCanonicalRootAndFiltersManaged(
        string argumentsJson,
        string expectedCanonicalRoot)
    {
        using var scope = TestScope.Create();
        var catalog = new TestExtensionCatalog();
        var target = new ScriptedExecutionTarget("docker", "docker", [
            new AgentShellCommandResult(127, "rg: not found"),
            new AgentShellCommandResult(0, "/workspace/younics-web/app/page.tsx\n")
        ]);
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        var source = new FilesToolSource(scope.Context);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId, "docker");

        var result = await source.ExecuteAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest("glob", argumentsJson));

        Assert.False(result.IsError);
        Assert.Equal(2, target.Commands.Count);
        Assert.StartsWith("rg ", target.Commands[0], StringComparison.Ordinal);
        Assert.Contains("--files", target.Commands[0], StringComparison.Ordinal);
        Assert.Contains("-g", target.Commands[0], StringComparison.Ordinal);
        Assert.Contains("--", target.Commands[0], StringComparison.Ordinal);
        Assert.StartsWith("find ", target.Commands[1], StringComparison.Ordinal);
        Assert.Contains(expectedCanonicalRoot, target.Commands[1], StringComparison.Ordinal);
        Assert.DoesNotContain("-path", target.Commands[1], StringComparison.Ordinal);
        Assert.DoesNotContain(" -name ", target.Commands[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilesToolSource_Glob_DockerFallback_FiltersFindSlashOvermatches()
    {
        using var scope = TestScope.Create();
        var catalog = new TestExtensionCatalog();
        var target = new ScriptedExecutionTarget("docker", "docker", [
            new AgentShellCommandResult(127, "rg: not found"),
            new AgentShellCommandResult(0, "/workspace/src/Root.cs\0/workspace/src/Nested/File.cs\0")
        ]);
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        var source = new FilesToolSource(scope.Context);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId, "docker");

        var result = await source.ExecuteAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest("glob", "{\"pattern\":\"src/*.cs\"}"));

        Assert.False(result.IsError);
        Assert.Contains("/workspace/src/Root.cs", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("/workspace/src/Nested/File.cs", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilesToolSource_Grep_UsesDockerFallback_WhenRipgrepIsMissing()
    {
        using var scope = TestScope.Create();
        var catalog = new TestExtensionCatalog();
        var target = new ScriptedExecutionTarget("docker", "docker", [
            new AgentShellCommandResult(127, "rg: not found"),
            new AgentShellCommandResult(0, "/workspace/test/file.txt\0" + "2:needle here\n")
        ]);
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        var source = new FilesToolSource(scope.Context);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId, "docker");

        var result = await source.ExecuteAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
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
        using var scope = TestScope.Create();
        var catalog = new TestExtensionCatalog();
        var target = new ProcessScriptedExecutionTarget("docker", "docker", [
            new AgentShellCommandResult(127, "rg: not found"),
            new AgentShellCommandResult(0, "/workspace/app/file.cs\0" + "7:match here\n")
        ]);
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        var source = new FilesToolSource(scope.Context);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        var argumentsJson = JsonSerializer.Serialize(new { pattern, path = "app" });

        var result = await source.ExecuteAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
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
        using var scope = TestScope.Create();
        const string pattern = "<ProjectReference|<PackageReference|<OutputType>";
        var catalog = new TestExtensionCatalog();
        var target = new ProcessScriptedExecutionTarget("docker", "docker", [
            new AgentShellCommandResult(127, "rg: not found"),
            new AgentShellCommandResult(0, "/workspace/app/App.csproj\0" + "7:<PackageReference Include=\"Avalonia\" />\n")
        ]);
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        var source = new FilesToolSource(scope.Context);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        var argumentsJson = JsonSerializer.Serialize(new { pattern, path = "app", include = "*.csproj" });

        var result = await source.ExecuteAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
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
        using var scope = TestScope.Create();
        var pattern = "TODO|FIXME|`whoami`|\"quoted\"|'single'|$HOME|test\\path";
        var catalog = new TestExtensionCatalog();
        var target = new ProcessScriptedExecutionTarget("docker", "docker", [
            new AgentShellCommandResult(0, JsonSerializer.Serialize(new
            {
                type = "match",
                data = new
                {
                    path = new { text = "/workspace/younics-web/app/file.ts" },
                    lines = new { text = "TODO here\n" },
                    line_number = 7,
                },
            }) + "\n")
        ]);
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        var source = new FilesToolSource(scope.Context);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        var argumentsJson = JsonSerializer.Serialize(new { pattern, path = "younics-web/app", include = "*" });

        var result = await source.ExecuteAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest("grep", argumentsJson));

        Assert.False(result.IsError);
        var request = Assert.Single(target.ProcessCommands);
        Assert.Equal("rg", request.FileName);
        Assert.Equal(pattern, request.Arguments[^2]);
        Assert.Equal("/workspace/younics-web/app", request.Arguments[^1]);
        Assert.Empty(target.ShellCommands);
    }

    [Fact]
    public async Task FilesToolSource_Grep_DockerFallback_UsesProcessArguments_ForShellMetacharacterPattern()
    {
        using var scope = TestScope.Create();
        var pattern = "TODO|FIXME|`whoami`|\"quoted\"|'single'|$HOME|test\\path";
        var catalog = new TestExtensionCatalog();
        var target = new ProcessScriptedExecutionTarget("docker", "docker", [
            new AgentShellCommandResult(127, "rg: not found"),
            new AgentShellCommandResult(0, "/workspace/younics-web/app/file.ts\0" + "7:TODO here\n")
        ]);
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        var source = new FilesToolSource(scope.Context);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        var argumentsJson = JsonSerializer.Serialize(new { pattern, path = "younics-web/app", include = "*" });

        var result = await source.ExecuteAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
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
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, new LocalExecutionTarget(scope.Context, configService, shellCatalogService));
        var source = new FilesToolSource(scope.Context);
        var (workspace, binding) = await CreateLocalWorkspaceAsync(root, configService);

        var permission = await source.BuildPermissionRequestAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
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
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        var configService = new LocalExecutionWorkspaceConfigService(scope.Context);
        var shellCatalogService = new LocalShellCatalogService(scope.Context);
        var catalog = new TestExtensionCatalog();
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, new LocalExecutionTarget(scope.Context, configService, shellCatalogService));
        var source = new FilesToolSource(scope.Context);
        var (workspace, binding) = await CreateLocalWorkspaceAsync(root, configService);

        var permission = await source.BuildPermissionRequestAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
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
        Assert.Equal(2, permission.ResourceReferences.Count);
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
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, new LocalExecutionTarget(scope.Context, configService, shellCatalogService));
        var source = new FilesToolSource(scope.Context);
        var (workspace, binding) = await CreateLocalWorkspaceAsync(root, configService);

        var permission = await source.BuildPermissionRequestAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
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
        Assert.Equal(2, permission.ResourceReferences.Count);
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
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, new LocalExecutionTarget(scope.Context, configService, shellCatalogService));
        var source = new FilesToolSource(scope.Context);
        var (workspace, binding) = await CreateLocalWorkspaceAsync(root, configService);

        var permission = await source.BuildPermissionRequestAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest("apply_patch", ApplyPatchArgs("not a patch")));

        Assert.NotNull(permission);
        Assert.Equal(AgentPermissionBoundaryIds.Unknown, permission!.BoundaryId);
        Assert.Null(permission.Path);
        Assert.Equal("apply_patch workspace files", permission.Summary);
    }

    [Fact]
    public async Task FilesToolSource_ApplyPatchPermission_LegacyDeleteMetadataFallsBackToCanonicalBoundary()
    {
        using var scope = TestScope.Create();
        var catalog = new TestExtensionCatalog();
        var target = new FakeExecutionTarget(string.Empty);
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        var source = new FilesToolSource(scope.Context);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);

        var permission = await source.BuildPermissionRequestAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest("apply_patch", ApplyPatchArgs("""
                *** Begin Patch
                *** Delete File: obsolete.txt
                *** End Patch
                """)));

        Assert.NotNull(permission);
        Assert.Equal(AgentPermissionBoundaryIds.ConfiguredScope, permission!.BoundaryId);
        Assert.Equal(["obsolete.txt"], permission.ResourceReferences);
    }

    [Fact]
    public async Task FilesToolSource_ApplyPatchPermission_PreservesCaseDistinctTargetPaths()
    {
        using var scope = TestScope.Create();
        var catalog = new TestExtensionCatalog();
        var target = new FakeExecutionTarget(string.Empty);
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        var source = new FilesToolSource(scope.Context);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);

        var permission = await source.BuildPermissionRequestAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest("apply_patch", ApplyPatchArgs("""
                *** Begin Patch
                *** Add File: Case.txt
                +upper
                *** Add File: case.txt
                +lower
                *** End Patch
                """)));

        Assert.NotNull(permission);
        Assert.Equal(["Case.txt", "case.txt"], target.ResolvedPaths);
        Assert.Equal(2, permission!.ResourceReferences.Count);
    }

    [Fact]
    public async Task ProfileToolCatalog_DoesNotEvaluateWorkspaceReadiness()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var catalog = new TestExtensionCatalog();
        catalog.AddProvider(AgentRpcServices.ToolSources, new FilesToolSource(scope.Context));
        var sessionService = new AgentSessionService(store);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog);
        var toolService = new AgentToolService(sessionService, workspaceService, executionTargetService, catalog);

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
        using var scope = TestScope.Create();
        var source = new FilesToolSource(scope.Context);

        var tools = await source.ListToolsAsync(new AgentToolSourceContext(null, null, null, null));

        Assert.All(tools, tool => Assert.Equal(AgentToolPriority.Medium, tool.Priority));
        Assert.Contains(tools, tool => tool.ToolId == "read" && tool.RuntimeInstructions?.Contains("Use this tool to read file contents", StringComparison.Ordinal) == true);
        Assert.Contains(tools, tool => tool.ToolId == "grep" && tool.RuntimeInstructions?.Contains("Prefer this over shell commands", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ShellToolSource_ListToolsAsync_UsesLowPriorityAndFallbackGuidance()
    {
        var source = new ShellToolSource();

        var tools = await source.ListToolsAsync(new AgentToolSourceContext(null, null, null, null));
        var shell = Assert.Single(tools);

        Assert.Equal("shell", shell.ToolId);
        Assert.Equal(AgentToolPriority.Low, shell.Priority);
        Assert.Contains("Use this low-priority tool only", shell.RuntimeInstructions ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void FilesToolSource_ApplyPatchPresentation_CompactsHeaderAndKeepsPatchInDetails()
    {
        using var scope = TestScope.Create();
        var source = new FilesToolSource(scope.Context);
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
        using var scope = TestScope.Create();
        var source = new FilesToolSource(scope.Context);
        catalog.AddProvider(AgentRpcServices.ToolSources, source);
        var service = new AgentToolPresentationService(rpcCatalog: catalog);
        var argumentsJson = JsonSerializer.Serialize(new { pattern = "*.html", path = "." });

        var presentation = service.Resolve(CreateToolItem("glob", argumentsJson, textContent: "index.html", resultSummary: "Found 1 match."));

        Assert.Equal("Found 1 match", presentation.HeaderText);
        Assert.Contains("**Request**", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.Contains("Pattern: *.html", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Arguments", presentation.DetailMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentToolInvocationRowViewModel_LazyResultDetailsUseAuthoritativeCallArguments()
    {
        var catalog = new TestExtensionCatalog();
        using var scope = TestScope.Create();
        var source = new FilesToolSource(scope.Context);
        catalog.AddProvider(AgentRpcServices.ToolSources, source);
        var service = new AgentToolPresentationService(rpcCatalog: catalog);
        var patchText = """
            *** Begin Patch
            *** Update File: index.html
            @@
            -old
            +new
            *** End Patch
            """;
        using var row = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem(
                "apply_patch",
                ApplyPatchArgs(patchText),
                textContent: "Updated index.html",
                resultSummary: "Applied 1 patch operation to 1 file."),
            service);

        Assert.False(row.HasMaterializedDetails);
        var details = await TranscriptToolTestHarness.ExpandAsync(row);

        Assert.Equal("Applied 1 patch operation to 1 file.", row.HeaderDetailText);
        Assert.Contains("*** Update File: index.html", details.DetailMarkdownBuilder.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Arguments", details.DetailMarkdownBuilder.ToString(), StringComparison.Ordinal);
        Assert.True(details.HasToolDiff);
        Assert.False(details.ShowMarkdownDetails);
    }

    [Fact]
    public async Task AgentToolInvocationRowViewModel_ApplyPatchBuildsVisualDiffAfterExpansion()
    {
        var catalog = new TestExtensionCatalog();
        using var scope = TestScope.Create();
        var source = new FilesToolSource(scope.Context);
        catalog.AddProvider(AgentRpcServices.ToolSources, source);
        var service = new AgentToolPresentationService(rpcCatalog: catalog);
        var patchText = """
            *** Begin Patch
            *** Update File: /workspace/src/Foo.cs
            @@
            -old
            +new
            *** End Patch
            """;

        using var row = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem("apply_patch", ApplyPatchArgs(patchText), textContent: "Updated /workspace/src/Foo.cs", resultSummary: "Applied 1 patch operation to 1 file."),
            service);

        var details = await TranscriptToolTestHarness.ExpandAsync(row);
        var file = Assert.Single(details.ToolDiffFiles);
        Assert.Equal("Applied 1 patch operation to 1 file.", row.HeaderDetailText);
        Assert.Equal("Patch", details.ToolDiffSectionTitle);
        Assert.True(details.HasToolDiff);
        Assert.False(details.ShowMarkdownDetails);
        Assert.Equal("/workspace/src/Foo.cs", file.Path);
        Assert.Equal(1, file.AddedLineCount);
        Assert.Equal(1, file.DeletedLineCount);
        Assert.DoesNotContain(file.Lines, line => line.IsHunk || line.Text.Contains("@@", StringComparison.Ordinal));
        Assert.Contains(file.Lines, line => line.IsDeleted && line.Text == "old");
        Assert.Contains(file.Lines, line => line.IsAdded && line.Text == "new");
    }

    [Fact]
    public async Task AgentToolInvocationRowViewModel_ApplyPatchFailureKeepsLazyVisualDiff()
    {
        var catalog = new TestExtensionCatalog();
        using var scope = TestScope.Create();
        var source = new FilesToolSource(scope.Context);
        catalog.AddProvider(AgentRpcServices.ToolSources, source);
        var service = new AgentToolPresentationService(rpcCatalog: catalog);
        var patchText = """
            *** Begin Patch
            *** Update File: /workspace/src/Foo.cs
            @@
            -old
            +new
            *** End Patch
            """;

        using var row = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem(
                "apply_patch",
                ApplyPatchArgs(patchText),
                textContent: "### File tool failed\n\nPatch hunk did not match the current file content.",
                resultSummary: "Patch hunk did not match the current file content.",
                isError: true),
            service);

        var details = await TranscriptToolTestHarness.ExpandAsync(row);
        Assert.Equal("Patch hunk did not match the current file content.", row.HeaderDetailText);
        Assert.True(details.HasToolDiff);
        Assert.False(details.ShowMarkdownDetails);
    }

    [Fact]
    public async Task AgentToolInvocationRowViewModel_EditBuildsFocusedDiffAfterExpansion()
    {
        var service = new AgentToolPresentationService();
        var argumentsJson = JsonSerializer.Serialize(new
        {
            path = "/workspace/src/Foo.cs",
            oldString = "old\nline",
            newString = "new\nline",
            replaceAll = false,
        });

        using var row = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem("edit", argumentsJson, textContent: "Wrote 42 character(s).", resultSummary: "Wrote 42 character(s)."),
            service);

        var details = await TranscriptToolTestHarness.ExpandAsync(row);
        var file = Assert.Single(details.ToolDiffFiles);
        Assert.Equal("Wrote 42 character(s).", row.HeaderDetailText);
        Assert.Equal("Diff", details.ToolDiffSectionTitle);
        Assert.True(details.HasToolDiff);
        Assert.True(details.ShowMarkdownDetails);
        Assert.DoesNotContain(file.Lines, line => line.IsHunk || line.Text.Contains("@@", StringComparison.Ordinal));
        Assert.Contains(file.Lines, line => line.IsDeleted && line.Text == "old");
        Assert.Contains(file.Lines, line => line.IsAdded && line.Text == "new");
    }

    [Fact]
    public async Task AgentToolInvocationRowViewModel_MalformedApplyPatchFallsBackToLazyMarkdownDetails()
    {
        var catalog = new TestExtensionCatalog();
        using var scope = TestScope.Create();
        var source = new FilesToolSource(scope.Context);
        catalog.AddProvider(AgentRpcServices.ToolSources, source);
        var service = new AgentToolPresentationService(rpcCatalog: catalog);

        using var row = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem("apply_patch", ApplyPatchArgs("not a patch"), textContent: "failed", resultSummary: null),
            service);

        var details = await TranscriptToolTestHarness.ExpandAsync(row);
        Assert.False(details.HasToolDiff);
        Assert.True(details.ShowMarkdownDetails);
        Assert.Contains("not a patch", details.DetailMarkdownBuilder.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WebTools_PresentationResolvers_AreResolvedThroughInstalledPackageSource()
    {
        using var scope = TestScope.Create();
        var catalog = new TestExtensionCatalog();
        var settings = new WebToolsSettingsService(scope.Context);
        catalog.AddTool(new WebSearchTool(new ExaWebSearchBackend(settings), settings));
        catalog.AddTool(new WebFetchTool(new WebFetchService()));
        var service = new AgentToolPresentationService(catalog);

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
        var source = new ShellToolSource();
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
    public async Task AgentToolInvocationRowViewModel_ShowsDetailsAndOutputTogetherAfterExpansion()
    {
        using var row = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem("unknown_tool", JsonSerializer.Serialize(new { query = "weather tomorrow" }), textContent: "tool output", resultSummary: "Tool completed."),
            new AgentToolPresentationService());

        Assert.True(row.HasHeaderDetail);
        Assert.False(row.HasMaterializedDetails);
        var details = await TranscriptToolTestHarness.ExpandAsync(row);
        Assert.True(details.HasMarkdownDetails);
        Assert.True(details.HasOutput);
        Assert.Contains("tool output", details.OutputText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentToolInvocationRowViewModel_LazyDetailPredicatesIncludeEveryVisualKind()
    {
        using var outputOnly = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem("output_only", "{}", textContent: "output"),
            new AgentToolPresentationService());
        using var metadataOnly = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem(
                "metadata_only",
                "{}",
                errorCode: "tool-failed",
                backendId: "local"),
            new AgentToolPresentationService());
        using var diffOnly = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem(
                "edit",
                JsonSerializer.Serialize(new
                {
                    path = "src/Foo.cs",
                    oldString = "old",
                    newString = "new",
                })),
            new AgentToolPresentationService());
        using var markdownOnly = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem("markdown_only", "{\"query\":\"details\"}"),
            new AgentToolPresentationService());

        var outputDetails = await TranscriptToolTestHarness.ExpandAsync(outputOnly);
        var metadataDetails = await TranscriptToolTestHarness.ExpandAsync(metadataOnly);
        var diffDetails = await TranscriptToolTestHarness.ExpandAsync(diffOnly);
        diffDetails.DetailMarkdownBuilder.Clear();
        var markdownDetails = await TranscriptToolTestHarness.ExpandAsync(markdownOnly);

        Assert.True(outputDetails.HasOutput);
        Assert.False(outputDetails.HasMarkdownDetails);
        Assert.True(metadataDetails.HasMetadata);
        Assert.False(metadataDetails.HasMarkdownDetails);
        Assert.True(diffDetails.HasToolDiff);
        Assert.False(diffDetails.HasMarkdownDetails);
        Assert.True(markdownDetails.HasMarkdownDetails);
        Assert.All(new[] { outputOnly, metadataOnly, diffOnly, markdownOnly }, row =>
        {
            Assert.True(row.HasDetails);
            Assert.True(row.IsExpanded);
            Assert.NotNull(row.ExpandedDetails);
        });
    }

    [Fact]
    public void AgentTextTranscriptRowViewModel_UnchangedAttachmentsDoNotResetCollection()
    {
        var metadata = new AgentAttachmentMetadata(
            Guid.NewGuid(),
            "diagram.png",
            "image/png",
            AgentAttachmentKind.Image,
            128,
            "hash",
            "attachments/diagram.png",
            IsText: false,
            WasTruncated: false);
        var attachment = new AgentTranscriptAttachmentViewModel(metadata);
        var row = new AgentTextTranscriptRowViewModel(
            new AgentTurnRecord(
                Guid.NewGuid(),
                Guid.NewGuid(),
                AgentMessageRole.User,
                AgentTurnKind.Message,
                [],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow),
            string.Empty,
            [attachment]);
        var collectionChanges = 0;
        var propertyChanges = 0;
        row.Attachments.CollectionChanged += (_, _) => collectionChanges++;
        row.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AgentTextTranscriptRowViewModel.HasAttachments))
            {
                propertyChanges++;
            }
        };

        row.ReplaceAttachments([new AgentTranscriptAttachmentViewModel(metadata)]);

        Assert.Same(attachment, Assert.Single(row.Attachments));
        Assert.Equal(0, collectionChanges);
        Assert.Equal(0, propertyChanges);
    }

    [Fact]
    public async Task AgentToolService_ListReadyRuntimeToolsAsync_OrdersToolsByPriorityDescending()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var catalog = new TestExtensionCatalog();
        catalog.AddTool(new TestTool("low", AgentToolPriority.Low));
        catalog.AddTool(new TestTool("high", AgentToolPriority.High));
        catalog.AddTool(new TestTool("medium", AgentToolPriority.Medium));
        var sessionService = new AgentSessionService(store);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog);
        var toolService = new AgentToolService(sessionService, workspaceService, executionTargetService, catalog);

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

    private static async Task<(AgentResolvedResource Resource, AgentExecutionTargetContext Context)>
        ApproveDockerResourceAsync(
            DockerExecutionTarget target,
            AgentExecutionTargetContext context,
            string path,
            string actionId)
    {
        var planningContext = context with
        {
            ResourceOperation = CreateResourceOperation(actionId),
        };
        var resource = await target.ResolveFileResourceAsync(planningContext, path);
        return (resource, planningContext with
        {
            ApprovedResourceReferences = [resource.CanonicalReference],
            ApprovedResourceClaims = resource.ResourceClaim is null ? [] : [resource.ResourceClaim],
        });
    }

    private static AgentToolExecutionContext CreateToolExecutionContext(
        TestExtensionCatalog catalog,
        AgentWorkspaceRecord workspace,
        AgentWorkspaceBindingRecord binding)
        => new(null, Workspace: workspace, ExecutionBinding: binding)
        {
            ExecutionTargetReference = catalog.GetRequiredReference(AgentRpcServices.ExecutionTargets),
        };

    private static AgentResourceOperationContext CreateResourceOperation(string actionId)
        => new(
            Guid.NewGuid(),
            1,
            "tool-call",
            actionId,
            0,
            "workspace-generation",
            "binding-generation",
            "sunder.package.agent.tools.files",
            "sunder.package.agent.execution.docker",
            Guid.NewGuid().ToString("N"),
            CanIssueOutsideAuthority: true);

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

    private static AgentPromptContextRequest CreatePromptContextRequest(AgentWorkspaceRecord? workspace)
    {
        var now = DateTimeOffset.UtcNow;
        var profile = new AgentProfileRecord(
            "profile", "Test Profile", null, null, "provider", "model", null, null, now, now, [], []);
        var session = new AgentSessionContextRecord(
            Guid.NewGuid(), profile.ProfileId, profile.DisplayName, "Test Session", AgentSessionState.Active, null);
        var run = new AgentRunContextRecord(Guid.NewGuid(), 1, AgentRunStatus.Running, IsInterrupted: false, now);
        return new AgentPromptContextRequest(
            session,
            run,
            new AgentTurnContextRecord(session, run, "Test user message.", null),
            [],
            [],
            new AgentPromptContextPlan("general", "Test user message."))
        {
            Profile = profile,
            Workspace = workspace,
        };
    }

    private static AgentEditorSectionViewModel CreateEditorSectionViewModel(params AgentEditorField[] fields)
    {
        var workspace = CreateWorkspace();
        var catalog = new TestExtensionCatalog();
        var contributor = new TestWorkspaceEditorContributor("docker");
        catalog.AddProvider(AgentRpcServices.WorkspaceEditors, contributor);
        return new AgentEditorSectionViewModel(
            catalog.GetRequiredReference(AgentRpcServices.WorkspaceEditors),
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
        string? presentationPayloadJson = null,
        string? errorCode = null,
        string? backendId = null)
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
            ErrorCode: errorCode,
            BackendId: backendId,
            PresentationPayloadJson: presentationPayloadJson);
    }

    private static WorkspaceViewServices CreateWorkspaceViewServices(TestPackageContext context)
    {
        var store = new AgentLocalStore(context);
        var catalog = new TestExtensionCatalog();
        var sessionService = new AgentSessionService(store);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog);
        var toolService = new AgentToolService(sessionService, workspaceService, executionTargetService, catalog);
        var profileService = new AgentProfileService(store, toolService, catalog, catalog.BehaviorLoops);
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
        return new AgentWorkspaceBindingRecord("binding-test", workspaceId, AgentRpcContractIds.ExecutionTarget, contributionId, "primary-execution-target", true, 0, now, now);
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
            var temporaryRoot = OperatingSystem.IsMacOS()
                ? "/private" + Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
                : Path.GetTempPath();
            var rootPath = Path.Combine(temporaryRoot, "sunder-execution-tests", Guid.NewGuid().ToString("N"));
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

    private sealed class TestExtensionCatalog : RegressionTestExtensionCatalog;

    private static string ResolveScriptedSearchPath(string path)
    {
        if (path.StartsWith("/", StringComparison.Ordinal))
        {
            return path.TrimEnd('/') is { Length: > 0 } absolute ? absolute : "/";
        }

        var relative = path.Replace('\\', '/').Trim('/');
        return relative is "" or "." ? "/workspace" : "/workspace/" + relative;
    }

    private static AgentProcessCommandRequest BindScriptedSearchCommand(
        AgentFileSearchProcessRequest request,
        string canonicalPath)
    {
        var arguments = request.Command.Arguments.ToList();
        arguments.Insert(request.PathArgumentIndex, canonicalPath);
        return request.Command with { Arguments = arguments };
    }

    private static string QuoteScriptedArgument(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private sealed class FakeExecutionTarget(string shellOutput) : IAgentExecutionTarget, IAgentFileSearchExecutionTarget
    {
        public AgentExecutionTargetDescriptor Descriptor { get; } = new("local", "local", "Fake", null, SupportsShell: true, SupportsFiles: true);

        public List<string> ResolvedPaths { get; } = [];

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionTargetReadiness("local", "local", AgentExecutionTargetReadinessStatus.Ready, "Ready."));

        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionShellDescriptor("sh", "POSIX sh", "/bin/sh", AgentShellSyntaxKinds.PosixSh, "Run POSIX shell commands."));

        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(AgentExecutionTargetContext context, string path, CancellationToken cancellationToken = default)
        {
            ResolvedPaths.Add(path);
            return ValueTask.FromResult(new AgentResolvedResource("file", path, path, AgentPermissionBoundaryIds.ConfiguredScope, true));
        }

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(AgentExecutionTargetContext context, AgentShellCommandRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentShellCommandResult(0, shellOutput));

        public ValueTask<AgentFileSearchProcessResult> ExecuteFileSearchProcessAsync(
            AgentExecutionTargetContext context,
            AgentFileSearchProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            var canonicalPath = ResolveScriptedSearchPath(request.Path);
            return ValueTask.FromResult(new AgentFileSearchProcessResult(
                new AgentShellCommandResult(0, shellOutput),
                canonicalPath,
                canonicalPath,
                AgentFileSearchPathStyle.Posix));
        }

        public ValueTask<AgentFileReadResult> ReadFileAsync(AgentExecutionTargetContext context, AgentFileReadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> WriteFileAsync(AgentExecutionTargetContext context, AgentFileWriteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> DeleteFileAsync(AgentExecutionTargetContext context, AgentFileDeleteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class MutableFileExecutionTarget(string content) : IAgentExecutionTarget
    {
        public AgentExecutionTargetDescriptor Descriptor { get; } = new("local", "local", "Mutable File Target", null, SupportsShell: true, SupportsFiles: true);

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
                new AgentEditorSection(
                    "blocking-editor",
                    "Blocking Editor",
                    null,
                    [new AgentEditorField("value", "Value", AgentEditorFieldKind.Text, Value: "initial")]),
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

    private sealed class BlockingWorkspaceInitializationGateway : IAgentWorkspaceGateway
    {
        private readonly object _syncRoot = new();
        private readonly List<AgentWorkspaceRecord> _workspaces = [];
        private int _initializeCount;
        private long _snapshotVersion;

        public int InitializeCount => Volatile.Read(ref _initializeCount);

        public TaskCompletionSource FirstInitializationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstInitialization { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstInitializationCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondInitializationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseSecondInitialization { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action? WorkspacesChanged;

        public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces()
        {
            lock (_syncRoot)
            {
                return _workspaces.Select(workspace => workspace with
                {
                    UpdatedAtUtc = workspace.UpdatedAtUtc.AddTicks(_snapshotVersion),
                }).ToArray();
            }
        }

        public void AdvanceWorkspaceSnapshot()
        {
            lock (_syncRoot)
            {
                _snapshotVersion++;
            }
        }

        public AgentWorkspaceRecord? GetWorkspace(string workspaceId)
        {
            lock (_syncRoot)
            {
                return _workspaces.FirstOrDefault(workspace => workspace.WorkspaceId == workspaceId);
            }
        }

        public AgentWorkspaceRecord CreateWorkspace(string displayName)
        {
            var now = DateTimeOffset.UtcNow;
            var workspace = new AgentWorkspaceRecord(
                Guid.NewGuid().ToString("N"),
                displayName,
                null,
                now,
                now);
            lock (_syncRoot)
            {
                _workspaces.Add(workspace);
            }
            WorkspacesChanged?.Invoke();
            return workspace;
        }

        public void SaveWorkspace(string workspaceId, string displayName, string? description)
        {
            lock (_syncRoot)
            {
                var index = _workspaces.FindIndex(workspace => workspace.WorkspaceId == workspaceId);
                _workspaces[index] = _workspaces[index] with
                {
                    DisplayName = displayName,
                    Description = description,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
            }
            WorkspacesChanged?.Invoke();
        }

        public void SaveWorkspaceAggregate(
            string workspaceId,
            string displayName,
            string? description,
            IReadOnlyList<AgentWorkspacePathRecord> paths,
            IReadOnlyList<AgentWorkspaceDocumentRecord> documents,
            string? executionTargetId)
            => SaveWorkspace(workspaceId, displayName, description);

        public void DeleteWorkspace(string workspaceId)
        {
            lock (_syncRoot)
            {
                _workspaces.RemoveAll(workspace => workspace.WorkspaceId == workspaceId);
            }
            WorkspacesChanged?.Invoke();
        }

        public IReadOnlyList<AgentWorkspaceBindingRecord> ListBindings(string workspaceId) => [];

        public AgentWorkspaceBindingRecord SavePrimaryExecutionBinding(
            string workspaceId,
            string contributionId,
            string displayRole = AgentWorkspaceBindingRoles.PrimaryExecutionTarget)
            => throw new NotSupportedException();

        public void RemovePrimaryExecutionBinding(string workspaceId)
        {
        }

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            var initialization = Interlocked.Increment(ref _initializeCount);
            if (initialization == 1)
            {
                FirstInitializationStarted.TrySetResult();
                await ReleaseFirstInitialization.Task;
                FirstInitializationCompleted.TrySetResult();
            }
            else if (initialization == 2)
            {
                SecondInitializationStarted.TrySetResult();
                await ReleaseSecondInitialization.Task.WaitAsync(cancellationToken);
            }
        }
    }

    private sealed class SelectionRaceWorkspaceEditorContributor(string targetId)
        : IAgentWorkspaceEditorContributor
    {
        private int _calls;

        public string ContributorId => "selection-race-workspace-editor";

        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CanEdit(AgentWorkspaceEditorContext context)
            => string.Equals(context.TargetId, targetId, StringComparison.OrdinalIgnoreCase);

        public async ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(
            AgentWorkspaceEditorContext context,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstStarted.TrySetResult();
                await ReleaseFirst.Task;
            }

            return
            [
                new AgentEditorSection(
                    "selection-race-editor",
                    context.Workspace.DisplayName,
                    null,
                    []),
            ];
        }

        public ValueTask<AgentEditorSaveResult> SaveSectionAsync(
            AgentWorkspaceEditorContext context,
            AgentEditorSaveRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(AgentEditorSaveResult.Ok("Saved."));
    }

    private sealed class ScriptedExecutionTarget(string targetKind, string targetId, IEnumerable<AgentShellCommandResult> results) : IAgentExecutionTarget, IAgentFileSearchExecutionTarget
    {
        private readonly Queue<AgentShellCommandResult> _results = new(results);

        public AgentExecutionTargetDescriptor Descriptor { get; } = new(targetKind, targetId, "Scripted Target", null, SupportsShell: true, SupportsFiles: true);

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

        public ValueTask<AgentFileSearchProcessResult> ExecuteFileSearchProcessAsync(
            AgentExecutionTargetContext context,
            AgentFileSearchProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            var canonicalPath = ResolveScriptedSearchPath(request.Path);
            var command = BindScriptedSearchCommand(request, canonicalPath);
            Commands.Add(command.FileName + " " + string.Join(" ", command.Arguments.Select(QuoteScriptedArgument)));
            var result = _results.Count == 0
                ? new AgentShellCommandResult(0, string.Empty)
                : _results.Dequeue();
            return ValueTask.FromResult(new AgentFileSearchProcessResult(
                result,
                canonicalPath,
                canonicalPath,
                AgentFileSearchPathStyle.Posix));
        }

        public ValueTask<AgentFileReadResult> ReadFileAsync(AgentExecutionTargetContext context, AgentFileReadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> WriteFileAsync(AgentExecutionTargetContext context, AgentFileWriteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> DeleteFileAsync(AgentExecutionTargetContext context, AgentFileDeleteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ProcessScriptedExecutionTarget(string targetKind, string targetId, IEnumerable<AgentShellCommandResult> results) : IAgentProcessExecutionTarget, IAgentFileSearchExecutionTarget
    {
        private readonly Queue<AgentShellCommandResult> _results = new(results);

        public AgentExecutionTargetDescriptor Descriptor { get; } = new(targetKind, targetId, "Process Scripted Target", null, SupportsShell: true, SupportsFiles: true);

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

        public ValueTask<AgentFileSearchProcessResult> ExecuteFileSearchProcessAsync(
            AgentExecutionTargetContext context,
            AgentFileSearchProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            var canonicalPath = ResolveScriptedSearchPath(request.Path);
            var command = BindScriptedSearchCommand(request, canonicalPath);
            ProcessCommands.Add(command);
            var result = _results.Count == 0
                ? new AgentShellCommandResult(0, string.Empty)
                : _results.Dequeue();
            return ValueTask.FromResult(new AgentFileSearchProcessResult(
                result,
                canonicalPath,
                canonicalPath,
                AgentFileSearchPathStyle.Posix));
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

        public AgentExecutionTargetDescriptor Descriptor { get; } = new("test", targetId, "Counting Target", null, SupportsShell: true, SupportsFiles: true);

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
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.FromResult(_values.GetValueOrDefault(key));
        }

        public void Seed(string key, string value)
        {
            TestPackageStorageGuards.Key(key);
            TestPackageStorageGuards.Value(value);
            _values[key] = value;
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            TestPackageStorageGuards.Value(value);
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.FromResult(_values.ContainsKey(key));
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Prefix(prefix);
            return Task.FromResult<IReadOnlyList<string>>(_values.Keys
                .Where(key => prefix is null || key.StartsWith(prefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray());
        }
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

    private static FakeDockerCliRunner CreateReadyDockerCliRunner(IPackageContext context)
        => new(context, (args, _, _, _, _) =>
        {
            if (args.Count > 0 && args[0] == "context")
            {
                return Task.FromResult(new DockerCliRunResult(0, "unix:///var/run/docker.sock", false, false));
            }
            if (args.Count > 0 && args[0] == "info")
            {
                return Task.FromResult(new DockerCliRunResult(0, "test-daemon", false, false));
            }
            if (args.Count > 1 && args[0] == "image" && args[1] == "inspect")
            {
                return Task.FromResult(new DockerCliRunResult(
                    0,
                    "sha256:" + new string('a', 64),
                    false,
                    false));
            }
            if (args.Count > 0 && args[0] == "inspect")
            {
                return Task.FromResult(new DockerCliRunResult(1, string.Empty, false, false));
            }
            return Task.FromResult(new DockerCliRunResult(0, string.Empty, false, false));
        });

    private sealed class PassThroughDockerMountIdentityVerifier : IDockerMountIdentityVerifier
    {
        public int VerifyCount { get; private set; }

        public Task VerifyAsync(
            string container,
            DockerExecutionRuntimeConfig config,
            IReadOnlyList<DockerVerifiedMount> mounts,
            Func<IReadOnlyList<string>, CancellationToken, Task<DockerCliRunResult>> runDockerAsync,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotEmpty(mounts);
            VerifyCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDockerCliRunner(
        IPackageContext context,
        Func<IReadOnlyList<string>, int, CancellationToken, string?, IProgress<string>?, Task<DockerCliRunResult>> run,
        Func<string>? endpoint = null)
        : DockerCliRunner(context)
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public List<IReadOnlyList<string>> RawCalls { get; } = [];

        internal override Task<DockerCliResolution> ResolveExecutableAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DockerCliResolution(
                Path.Combine(AppContext.BaseDirectory, "fake-docker"),
                [],
                null));

        protected override Task<string> ResolveEndpointAsync(CancellationToken cancellationToken)
            => Task.FromResult(endpoint?.Invoke() ?? "unix:///var/run/docker.sock");

        protected override async Task<DockerCliRunResult> RunCoreAsync(
            IReadOnlyList<string> args,
            int timeoutSeconds,
            CancellationToken cancellationToken,
            string? standardInput = null,
            IProgress<string>? progress = null)
        {
            RawCalls.Add(args.ToArray());
            var logicalArgs = args.Count >= 2 && args[0] == "--host"
                ? args.Skip(2).ToArray()
                : args.ToArray();
            Calls.Add(logicalArgs);
            var result = await run(logicalArgs, timeoutSeconds, cancellationToken, standardInput, progress);
            return result.ExitCode == 0
                   && string.IsNullOrWhiteSpace(result.Output)
                   && logicalArgs.Length >= 5
                   && logicalArgs[0] == "image"
                   && logicalArgs[1] == "inspect"
                   && logicalArgs[2] == "--format"
                ? result with { Output = "sha256:" + new string('0', 64) }
                : result;
        }
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
