using System.Reflection;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyAgentWorkspacePaths(Assembly owner, Assembly local, Assembly docker)
    {
        using var package = RegressionTestPackageScope.Create();
        using var localPackage = RegressionTestPackageScope.Create();
        using var dockerPackage = RegressionTestPackageScope.Create();
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        await using var receiver = new GeneratedServiceTestScope(rpc);
        var migrators = receiver.Providers(owner.GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Services.IWorkspacePathMigratorService", true)!);
        var storeType = owner.GetType("Sunder.Package.Agent.Storage.AgentLocalStore", true)!;
        var store = Activator.CreateInstance(storeType, package.Context)!;
        await using var initialStore = (IAsyncDisposable)store;
        var workspaceType = owner.GetType("Sunder.Package.Agent.Services.AgentWorkspaceService", true)!;
        var workspaces = AgentRoleBoundary.Create(workspaceType, store, null, null, migrators);
        var localConfigType = local.GetType("Sunder.Package.Agent.Execution.Local.LocalExecutionWorkspaceConfigService", true)!;
        var dockerConfigType = docker.GetType("Sunder.Package.Agent.Execution.Docker.DockerExecutionWorkspaceConfigService", true)!;
        var localConfig = Activator.CreateInstance(localConfigType, localPackage.Context)!;
        var dockerConfig = Activator.CreateInstance(dockerConfigType, dockerPackage.Context, null)!;
        var localRegistration = receiver.Register(localConfig, RegressionTestBindings.WorkspacePathMigrators);
        var dockerRegistration = receiver.Register(dockerConfig, RegressionTestBindings.WorkspacePathMigrators);
        var now = DateTimeOffset.UtcNow;
        var first = Path.Combine(package.RootPath, "first 漢字"); var second = Path.Combine(package.RootPath, "second");
        var localContext = Create("local", "LOCAL");
        var dockerContext = Create("docker", "docker");
        var occupied = Create("occupied", "local");
        SurfaceCall(workspaces, "SaveWorkspacePaths", occupied.Workspace.WorkspaceId,
            new AgentWorkspacePathRecord[] { new("retained", occupied.Workspace.WorkspaceId, first, true, 0, now, now) });
        var disabled = Create("disabled", "local", enabled: false);
        var secondary = Create("secondary", "local", role: "secondary");
        var unknown = Create("unknown", "unmatched");
        var unbound = (AgentWorkspaceRecord)SurfaceCall(workspaces, "CreateWorkspace", "Unbound")!;
        await SeedLocal(localContext); await SeedLocal(occupied); await SeedLocal(disabled); await SeedLocal(secondary);
        var dockerKey = Key(dockerConfigType, dockerContext.Binding.BindingId);
        var schema = (int)docker.GetType("Sunder.Package.Agent.Execution.Docker.DockerImageCatalogService", true)!
            .GetField("CurrentSchemaVersion", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        await dockerPackage.Context.Storage.State.SetValueAsync(dockerKey, JsonSerializer.Serialize(new
        {
            SchemaVersion = schema, ImageReference = "alpine:3.22", ContainerName = "migration-fixture", ShellPath = "/bin/sh", PathEntries = new[] { "/opt/bin" },
            AllowedRoots = new[] { "/container/second", "/container/first" },
            HostRoots = new Dictionary<string, string> { ["/container/first"] = first, ["/container/second"] = second },
            DefaultWorkingDirectory = "/container/first/nested",
        }));
        await Initialize(); Assert.Empty(Current(localContext).Paths); Assert.Empty(Current(dockerContext).Paths);
        var localHandler = AddSurfaceProvider(state, "sunder.package.agent.execution.local", localRegistration);
        var dockerHandler = AddSurfaceProvider(state, "sunder.package.agent.execution.docker", dockerRegistration);
        var committed = new List<AgentWorkspaceRecord>();
        localHandler.Before = dockerHandler.Before = (method, request) =>
        {
            if (method == "complete") committed.Add((AgentWorkspaceRecord)SurfaceCall(workspaces, "GetWorkspace", request.GetProperty("workspace").GetProperty("workspaceId").GetString())!);
        };
        await Initialize();
        Assert.Equal(2, committed.Count); Assert.All(committed, value => Assert.Equal(2, value.Paths.Count));
        foreach (var context in new[] { localContext, dockerContext })
        {
            var paths = Current(context).Paths;
            Assert.Equal(new[] { second, first }, paths.Select(value => value.HostPath));
            Assert.Equal(new[] { 0, 1 }, paths.Select(value => value.SortOrder));
            Assert.Equal(first, Assert.Single(paths, value => value.IsDefault).HostPath);
            Assert.All(paths, value => { Assert.NotEmpty(value.PathId); Assert.Equal(context.Workspace.WorkspaceId, value.WorkspaceId); });
        }
        Assert.Equal("retained", Assert.Single(Current(occupied).Paths).PathId);
        foreach (var context in new[] { disabled, secondary, unknown }) Assert.Empty(Current(context).Paths);
        Assert.Empty(((AgentWorkspaceRecord)SurfaceCall(workspaces, "GetWorkspace", unbound.WorkspaceId)!).Paths);
        Assert.DoesNotContain(localHandler.Calls.Concat(dockerHandler.Calls), call => call.Method != "describe"
            && new[] { occupied.Workspace.WorkspaceId, disabled.Workspace.WorkspaceId, secondary.Workspace.WorkspaceId, unbound.WorkspaceId }
                .Contains(call.Request.GetProperty("workspace").GetProperty("workspaceId").GetString(), StringComparer.Ordinal));
        var sent = localHandler.Calls.First(call => call.Method == "list-legacy-paths").Request.Deserialize<AgentWorkspacePathMigrationContext>(MemoryProviderJson)!;
        Assert.Equal(localContext.Binding, sent.Binding); Assert.Equal(localContext.Workspace.Description, sent.Workspace.Description);
        Assert.Equal(localContext.Workspace.CreatedAtUtc, sent.Workspace.CreatedAtUtc); Assert.Equal(localContext.Workspace.Documents, sent.Workspace.Documents);
        using var localJson = JsonDocument.Parse((await localPackage.Context.Storage.State.GetValueAsync(Key(localConfigType, localContext.Binding.BindingId)))!);
        Assert.Equal("fixture.shell", localJson.RootElement.GetProperty("SelectedShellId").GetString());
        Assert.Equal(second, Assert.Single(localJson.RootElement.GetProperty("PathEntries").EnumerateArray()).GetString());
        Assert.False(localJson.RootElement.TryGetProperty("AllowedRoots", out _));
        using var dockerJson = JsonDocument.Parse((await dockerPackage.Context.Storage.State.GetValueAsync(dockerKey))!);
        Assert.Equal("alpine:3.22", dockerJson.RootElement.GetProperty("imageReference").GetString());
        Assert.Equal("migration-fixture", dockerJson.RootElement.GetProperty("containerName").GetString());
        Assert.Equal("/opt/bin", Assert.Single(dockerJson.RootElement.GetProperty("pathEntries").EnumerateArray()).GetString());
        Assert.False(dockerJson.RootElement.TryGetProperty("AllowedRoots", out _));

        // Cleanup failure cannot undo committed paths; a failed read cannot create them.
        var cleanupFailure = Create("cleanup-failure", "local"); await SeedLocal(cleanupFailure);
        localHandler.FailMethod = "complete"; await Initialize();
        Assert.Equal(2, Current(cleanupFailure).Paths.Count);
        Assert.Contains("AllowedRoots", (await localPackage.Context.Storage.State.GetValueAsync(Key(localConfigType, cleanupFailure.Binding.BindingId)))!, StringComparison.Ordinal);
        localHandler.FailMethod = "list-legacy-paths";
        var readFailure = Create("read-failure", "local"); await SeedLocal(readFailure); await Initialize(); Assert.Empty(Current(readFailure).Paths);
        localHandler.FailMethod = null;
        localHandler.BlockMethod = "list-legacy-paths"; localHandler.Gate = new();
        using (var cancelled = new CancellationTokenSource())
        {
            var pending = Initialize(cancelled.Token); await localHandler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel(); await pending; await localHandler.Gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(Current(readFailure).Paths);
        }
        localHandler.Gate = new();
        var retiring = Initialize(); await localHandler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RetireAsync(localHandler).WaitAsync(TimeSpan.FromSeconds(5)); await retiring;
        Assert.Empty(Current(readFailure).Paths);
        AddSurfaceProvider(state, "sunder.package.agent.execution.local", localRegistration);
        await Initialize(); Assert.Equal(2, Current(readFailure).Paths.Count);

        var persisted = Current(localContext).Paths.ToArray();
        await initialStore.DisposeAsync(); store = Activator.CreateInstance(storeType, package.Context)!;
        await using var reopened = (IAsyncDisposable)store;
        workspaces = AgentRoleBoundary.Create(workspaceType, store, null, null, migrators);
        await Initialize(); Assert.Equal(persisted, Current(localContext).Paths);
        Assert.Equal(2, Current(cleanupFailure).Paths.Count);

        AgentWorkspacePathMigrationContext Create(string id, string target, bool enabled = true, string role = "primary-execution-target")
        {
            var workspace = new AgentWorkspaceRecord("migration." + id, "Workspace 漢字 " + id, "Description", now, now);
            SurfaceCall(workspaces, "ImportWorkspace", workspace, null,
                new AgentWorkspaceDocumentRecord[] { new("guide." + id, workspace.WorkspaceId, Path.Combine(first, "AGENTS.md"), 0, now, now) }, target);
            var binding = Assert.Single((IReadOnlyList<AgentWorkspaceBindingRecord>)SurfaceCall(workspaces, "ListBindings", workspace.WorkspaceId)!);
            binding = binding with { IsEnabled = enabled, Role = role };
            SurfaceCall(store, "SaveWorkspaceBinding", binding);
            return new((AgentWorkspaceRecord)SurfaceCall(workspaces, "GetWorkspace", workspace.WorkspaceId)!, binding);
        }
        AgentWorkspaceRecord Current(AgentWorkspacePathMigrationContext context) => (AgentWorkspaceRecord)SurfaceCall(workspaces, "GetWorkspace", context.Workspace.WorkspaceId)!;
        Task Initialize(CancellationToken token = default) => ((Task)SurfaceCall(workspaces, "InitializeAsync", token)!).WaitAsync(TimeSpan.FromSeconds(10));
        Task SeedLocal(AgentWorkspacePathMigrationContext context) => localPackage.Context.Storage.State.SetValueAsync(Key(localConfigType, context.Binding.BindingId), JsonSerializer.Serialize(new
        { SelectedShellId = "fixture.shell", PathEntries = new[] { second }, AllowedRoots = new[] { second, first, second }, DefaultWorkingDirectory = Path.Combine(first, "nested") }));
        static string Key(Type type, string binding) => (string)type.GetMethod("BuildKey", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [binding])!;
    }
}
