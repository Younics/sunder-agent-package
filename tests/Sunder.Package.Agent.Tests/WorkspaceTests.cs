using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
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
    public void AgentLocalStore_PreservesSessions_WhenStoreReopens()
    {
        using var scope = TestScope.Create();
        var firstStore = new AgentLocalStore(scope.Context);
        var workspace = CreateWorkspace();
        firstStore.SaveWorkspace(workspace);
        var session = firstStore.CreateSession("Persistent Session");

        var reopenedStore = new AgentLocalStore(scope.Context);

        var persistedSession = reopenedStore.GetSession(session.SessionId);
        Assert.NotNull(persistedSession);
        Assert.Equal(session.SessionId, persistedSession!.SessionId);
        Assert.Contains(reopenedStore.ListSessions(), item => item.SessionId == session.SessionId);
    }

    [Fact]
    public void AgentLocalStore_MigratesLegacySessionWorkspaceColumn()
    {
        using var scope = TestScope.Create();
        Directory.CreateDirectory(scope.Context.Storage.DataRootPath);
        var databasePath = Path.Combine(scope.Context.Storage.DataRootPath, "agent.db");
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
        Assert.DoesNotContain(GetTableColumns(databasePath, "AgentSessions"), column => string.Equals(column, "WorkspaceId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AgentLocalStore_MigratesActiveSessionWithFailedLatestCheckpoint()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var session = store.CreateSession("Failed Legacy Session");
        var databasePath = Path.Combine(scope.Context.Storage.DataRootPath, "agent.db");
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO AgentRunCheckpoints (CheckpointId, SessionId, RunRevision, Status, Summary, CreatedAtUtc)
                VALUES ($checkpointId, $sessionId, 1, 'Failed', 'Provider stream failed.', $createdAtUtc);
                """;
            command.Parameters.AddWithValue("$checkpointId", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("$sessionId", session.SessionId.ToString());
            command.Parameters.AddWithValue("$createdAtUtc", now);
            command.ExecuteNonQuery();
        }

        var reopenedStore = new AgentLocalStore(scope.Context);

        Assert.Equal(AgentSessionState.Failed, reopenedStore.GetSession(session.SessionId)?.State);
    }

    [Fact]
    public void AgentLocalStore_SaveCheckpoint_MarksFailedSessionFailed()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var session = store.CreateSession("Failed Session");

        store.SaveCheckpoint(session.SessionId, 1, AgentRunStatus.Failed, "Provider stream failed.");

        Assert.Equal(AgentSessionState.Failed, store.GetSession(session.SessionId)?.State);
    }

    [Fact]
    public void AgentSessionService_SaveCheckpoint_IgnoresThrowingSessionChangedSubscriber()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store);
        var session = sessionService.CreateSession("Event Isolation Session");
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
        var session = sessionService.CreateSession("Turn Event Isolation Session");
        var observed = false;
        sessionService.TurnChanged += (_, _) => throw new InvalidOperationException("Subscriber failed.");
        sessionService.TurnChanged += (_, _) => observed = true;

        var turn = sessionService.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, "hello");

        Assert.Equal(AgentMessageRole.Assistant, turn.Role);
        Assert.True(observed);
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
    public void AgentLocalStore_AllowsWorkspaceDeletion_WhenSessionsExist()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = CreateWorkspace();
        store.SaveWorkspace(workspace);
        var session = store.CreateSession("Workspace Session");

        store.DeleteWorkspace(workspace.WorkspaceId);

        Assert.Null(store.GetWorkspace(workspace.WorkspaceId));
        Assert.NotNull(store.GetSession(session.SessionId));
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
        var session = store.CreateSession("Tool Args");

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
        var session = sessionService.CreateSession("MCP Session");

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
        var (workspace, binding) = CreateLocalWorkspace(root, configService);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await target.ReadFileAsync(new AgentExecutionTargetContext(null, null, workspace, binding), new AgentFileReadRequest(Path.Combine(scope.RootPath, "outside.txt"))));
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
        var (workspace, binding) = CreateLocalWorkspace(root, configService);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await target.ReadFileAsync(new AgentExecutionTargetContext(null, null, workspace, binding), new AgentFileReadRequest(binaryPath)));
    }

    [Fact]
    public void DockerExecutionWorkspaceConfigService_SaveConfig_CreatesPackageFileRootsAndShellPath()
    {
        using var scope = TestScope.Create();
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);

        configService.SaveConfig(
            "workspace:primary-execution-target",
            new DockerExecutionWorkspaceConfig(
                "test-image:latest",
                ["/workspace/test"],
                "/workspace/test",
                null,
                "/bin/bash"));

        var config = configService.GetConfig("workspace:primary-execution-target");

        Assert.Equal("/bin/bash", config.ShellPath);
        Assert.Equal(["/workspace/test"], config.AllowedRoots);
        Assert.Equal("/workspace/test", config.DefaultWorkingDirectory);
        Assert.NotNull(config.ContainerName);
        Assert.DoesNotContain(':', config.ContainerName!);
        var defaultHostRoot = Path.Combine(scope.RootPath, "files", "workspace", "test");
        var mount = Assert.Single(configService.ResolveMounts(config));
        Assert.Equal(Path.GetFullPath(defaultHostRoot), mount.HostPath);
        Assert.Equal("/workspace/test", mount.ContainerPath);
        Assert.True(Directory.Exists(defaultHostRoot));
    }

    [Fact]
    public void DockerExecutionWorkspaceConfigService_SaveConfig_UsesCustomHostRoot()
    {
        using var scope = TestScope.Create();
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var hostRoot = Path.Combine(scope.RootPath, "custom-docker-root");

        configService.SaveConfig(
            "workspace:primary-execution-target",
            new DockerExecutionWorkspaceConfig(
                "test-image:latest",
                ["/workspace"],
                "/workspace",
                null,
                "/bin/sh",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["/workspace"] = hostRoot,
                }));

        var config = configService.GetConfig("workspace:primary-execution-target");
        var mount = Assert.Single(configService.ResolveMounts(config));

        Assert.Equal(Path.GetFullPath(hostRoot), config.HostRoots!["/workspace"]);
        Assert.Equal(Path.GetFullPath(hostRoot), mount.HostPath);
        Assert.Equal("/workspace", mount.ContainerPath);
        Assert.True(Directory.Exists(hostRoot));
    }

    [Fact]
    public void DockerExecutionWorkspaceConfigService_RejectsNestedContainerRoots()
    {
        using var scope = TestScope.Create();
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);

        Assert.Throws<InvalidOperationException>(() => configService.SaveConfig(
            "workspace:primary-execution-target",
            new DockerExecutionWorkspaceConfig(
                "test-image:latest",
                ["/workspace", "/workspace/test"],
                "/workspace",
                null,
                "/bin/sh")));
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

        viewModel.CreateWorkspaceCommand.Execute(null);
        Assert.False(viewModel.HasExecutionTargetChoices);

        var target = new CountingExecutionTarget("docker");
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, target);

        await WaitUntilAsync(() => viewModel.ExecutionTargets.Any(targetOption => string.Equals(targetOption.TargetId, "docker", StringComparison.OrdinalIgnoreCase)));
        Assert.True(viewModel.HasExecutionTargetChoices);
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
        var behaviorLoopHostFactory = new AgentBehaviorLoopHostFactory(sessionService, toolService, permissionService, memoryCoordinator, runEventLogger, activeRunRegistry);
        var childRunSessionService = new AgentChildRunSessionService(sessionService, profileService);
        var parentRunContinuationService = new AgentParentRunContinuationService(sessionService, profileService, workspaceService, providerResolver, activeRunRegistry, behaviorLoopHostFactory, behaviorLoopResolver, childRunSessionService);
        var permissionResumeCoordinator = new AgentPermissionResumeCoordinator(sessionService, workspaceService, profileService, permissionService, providerResolver, activeRunRegistry, runEventLogger, behaviorLoopHostFactory, behaviorLoopResolver, parentRunContinuationService);
        var userMessageRunCoordinator = new AgentUserMessageRunCoordinator(sessionService, profileService, workspaceService, memoryCoordinator, runAttachmentStore, activeRunRegistry, runEventLogger, providerResolver, behaviorLoopHostFactory, behaviorLoopResolver);
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
        var session = store.CreateSession("Permission Test");

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
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        configService.SaveConfig(
            binding.BindingId,
            new DockerExecutionWorkspaceConfig("test-image:latest", ["/workspace"], "/workspace", null, "/bin/sh"));

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
        var (workspace, binding) = CreateLocalWorkspace(root, configService);

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
        var (workspace, binding) = CreateLocalWorkspace(root, configService);

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
    public async Task FilesToolSource_ApplyPatchPermission_UsesOutsideConfiguredScope_WhenAnyPathEscapesAllowedRoots()
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
        var (workspace, binding) = CreateLocalWorkspace(root, configService);

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
        var (workspace, binding) = CreateLocalWorkspace(root, configService);

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

        Assert.Equal("Applied 1 patch operation to 1 file", row.HeaderDetailText);
        Assert.Contains("*** Update File: index.html", row.DetailMarkdownBuilder.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Arguments", row.DetailMarkdownBuilder.ToString(), StringComparison.Ordinal);
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
    public void AgentWorkspacesViewModel_CanSaveWorkspaceWithoutAgentProfile()
    {
        using var scope = TestScope.Create();
        var services = CreateWorkspaceViewServices(scope.Context);
        using var viewModel = new AgentWorkspacesViewModel(services.WorkspaceService, services.ExecutionTargetService, services.Catalog);

        viewModel.CreateWorkspaceCommand.Execute(null);

        Assert.NotNull(viewModel.SelectedWorkspace);
        Assert.True(viewModel.SaveWorkspaceCommand.CanExecute(null));
    }

    private static (AgentWorkspaceRecord Workspace, AgentWorkspaceBindingRecord Binding) CreateLocalWorkspace(string root, LocalExecutionWorkspaceConfigService configService)
    {
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);
        configService.SaveConfig(binding.BindingId, new LocalExecutionWorkspaceConfig([root], root));
        return (workspace, binding);
    }

    private static AgentWorkspaceRecord CreateWorkspace()
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentWorkspaceRecord("local-test", "Local Test", null, now, now);
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
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
        AgentTurnItemKind kind = AgentTurnItemKind.ToolResult)
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
            IsError: false,
            ErrorCode: null,
            BackendId: null);
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

    private sealed class TestExtensionCatalog : IPackageExtensionCatalog, IPackageExtensionCatalogChangeNotifier, IPackageExtensionCatalogMonitor
    {
        private readonly Dictionary<string, List<object>> _extensions = new(StringComparer.OrdinalIgnoreCase);
        private long _revision;

        public event EventHandler? ExtensionsChanged;

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
            ExtensionsChanged?.Invoke(this, EventArgs.Empty);
            Changed?.Invoke(this, args);
        }

        public IReadOnlyList<TContract> GetExtensions<TContract>(PackageExtensionPoint<TContract> extensionPoint)
            => !_extensions.TryGetValue(extensionPoint.Id, out var entries)
                ? []
                : entries.Cast<TContract>().ToArray();
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

        public Version Version { get; } = new(1, 0, 0);

        public string InstallPath => AppContext.BaseDirectory;

        public IPackageStorageContext Storage { get; } = new TestStorageContext(rootPath);

        public IPackageConfiguration Configuration { get; } = new TestConfiguration();

        public IPackageSecrets Secrets { get; } = new TestSecrets();

        public ILoggerFactory LoggerFactory => Logging.LoggerFactory;

        public Sunder.Sdk.Logging.IPackageLogging Logging { get; } = Sunder.Sdk.Logging.NullPackageLogging.Instance;
    }

    private sealed class TestStorageContext(string rootPath) : IPackageStorageContext
    {
        public string DataRootPath { get; } = Path.Combine(rootPath, "data");

        public string CacheRootPath { get; } = Path.Combine(rootPath, "cache");

        public string LogsRootPath { get; } = Path.Combine(rootPath, "logs");

        public IPackageFileStore Files { get; } = new TestPackageFileStore(Path.Combine(rootPath, "files"));

        public IPackageKeyValueStore State { get; } = new TestKeyValueStore();
    }

    private sealed class TestPackageFileStore(string rootPath) : IPackageFileStore
    {
        public string RootPath { get; } = rootPath;

        public string GetPath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return RootPath;
            }

            if (Path.IsPathRooted(relativePath))
            {
                throw new InvalidOperationException("Package file paths must be relative.");
            }

            var segments = relativePath
                .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(segment => segment is not ".")
                .ToArray();
            if (segments.Any(segment => segment == ".."))
            {
                throw new InvalidOperationException("Package file paths must not contain parent traversal.");
            }

            return Path.Combine([RootPath, .. segments]);
        }
    }

    private sealed class TestKeyValueStore : IPackageKeyValueStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? GetValue(string key) => _values.GetValueOrDefault(key);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(GetValue(key));

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

    private sealed class TestConfiguration : IPackageConfiguration
    {
        public string? GetValue(string key) => key switch
        {
            "shell.timeoutSeconds.default" => "30",
            _ => null,
        };
    }

    private sealed class TestSecrets : IPackageSecrets
    {
        public string? GetSecret(string key) => null;

        public void SetSecret(string key, string value)
        {
        }

        public void DeleteSecret(string key)
        {
        }
    }
}
