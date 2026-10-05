using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyMcpWorkspaceConsumer(Assembly worker, Assembly owner)
    {
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new McpWorkspaceRpc(new RegressionTestRpcClient(state, "sunder.package.agent.mcp"));
        var type = worker.GetType("Sunder.Package.Agent.Mcp.Rpc.RuntimeWorkspaceCatalog", true)!;
        var instance = Activator.CreateInstance(type, rpc)!;
        await using var lifetime = (IAsyncDisposable)instance;
        Assert.Empty(await discover(default));
        Assert.Empty(rpc.Scopes);
        await type.GetMethod("StartAsync")!.CreateDelegate<Func<CancellationToken, ValueTask>>(instance)(default);
        Assert.Empty(await discover(default));
        rpc.DiscoveryFailure = SunderRpcErrorKind.Unavailable;
        Assert.Empty(await discover(default));
        rpc.DiscoveryFailure = SunderRpcErrorKind.PermissionDenied;
        Assert.Equal(SunderRpcErrorKind.PermissionDenied, (await Assert.ThrowsAsync<SunderRpcException>(() => discover(default))).Error.Kind);
        rpc.DiscoveryFailure = null;

        using var package = RegressionTestPackageScope.Create();
        var home = Directory.CreateDirectory(Path.Combine(package.RootPath, "home")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(package.RootPath, "project")).FullName;
        var retiredProject = Directory.CreateDirectory(Path.Combine(package.RootPath, "retired-project")).FullName;
        WriteConfiguration(Path.Combine(home, ".config/sunder/mcp.json"), "global_server");
        WriteConfiguration(Path.Combine(project, ".sunder/mcp.json"), "workspace_server");
        WriteConfiguration(Path.Combine(retiredProject, ".sunder/mcp.json"), "retired_server");
        var now = DateTimeOffset.UtcNow;
        var workspace = new AgentWorkspaceRecord("workspace.mcp", "Workspace λ", null, now, now,
            [new("path.one", "workspace.mcp", project, true, 1, now, now), new("path.two", "workspace.mcp", project, false, 2, now, now)]);
        var domain = new McpWorkspaceRuntime([workspace]);
        AddSubagentSourceProvider(state, owner, "RuntimeCatalogService", domain, AgentRpcContractIds.RuntimeCatalog);
        Assert.Equal(workspace.WorkspaceId, Assert.Single(await discover(default)).WorkspaceId);
        Assert.Equal(2, Assert.Single(await discover(default)).Paths.Count);
        rpc.LookupFailure = SunderRpcErrorKind.Unavailable;
        Assert.Empty(await discover(default));
        rpc.LookupFailure = SunderRpcErrorKind.PermissionDenied;
        Assert.Equal(SunderRpcErrorKind.PermissionDenied, (await Assert.ThrowsAsync<SunderRpcException>(() => discover(default))).Error.Kind);
        rpc.LookupFailure = null;
        foreach (var failure in new Exception[] { new IOException("private provider failure"), new SunderRpcException(new(SunderRpcErrorKind.PermissionDenied, "test.query-denied", "Denied")) })
        {
            domain.Failure = failure;
            Assert.Empty(await discover(default));
        }
        domain.Failure = null;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); var scopes = rpc.Scopes.Count;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discover(cancelled.Token));
            Assert.Equal(scopes, rpc.Scopes.Count);
        }

        var context = new TestPackageContext("sunder.package.agent.mcp", package.RootPath, version: "2.0.0", storage: package.Context.Storage);
        var catalogType = worker.GetType("Sunder.Package.Agent.Mcp.Services.McpServerCatalogService", true)!;
        var catalog = Activator.CreateInstance(catalogType, context)!;
        var importer = Activator.CreateInstance(worker.GetType("Sunder.Package.Agent.Mcp.Services.McpEcosystemConfigurationImporter", true)!, catalog)!;
        var syncType = worker.GetType("Sunder.Package.Agent.Mcp.Services.McpSunderConfigurationSyncService", true)!;
        Assert.Single(syncType.GetConstructors());
        var sync = Activator.CreateInstance(syncType, importer, instance, home, home)!;
        var initial = (await Synchronize())!;
        Assert.Equal(2, initial.GetType().GetProperty("ImportedCount")!.GetValue(initial));
        Assert.Equal(new[] { "global_server", "workspace_server" }, (await Servers()).Select(value => (string)value.GetType().GetProperty("Name")!.GetValue(value)!).Order(StringComparer.Ordinal));
        await Synchronize();
        Assert.Equal(2, (await Servers()).Length);
        domain.Workspaces = [workspace with { WorkspaceId = "retired", Paths = [workspace.Paths[0] with { WorkspaceId = "retired", HostPath = retiredProject }] }];
        domain.AfterReply = () => rpc.LookupTransform = snapshot => snapshot with { SessionGeneration = snapshot.SessionGeneration + 1 };
        await Synchronize();
        Assert.DoesNotContain(await Servers(), value => (string)value.GetType().GetProperty("Name")!.GetValue(value)! == "retired_server");
        domain.AfterReply = null; rpc.LookupTransform = null;
        domain.Workspaces = [workspace];
        domain.Hold();
        using (var cancelled = new CancellationTokenSource())
        {
            var pending = Synchronize(cancelled.Token);
            await domain.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        domain.Hold();
        var retiredRead = discover(default);
        await domain.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RetireAsync(domain).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(await retiredRead.WaitAsync(TimeSpan.FromSeconds(5)));
        var replacement = new McpWorkspaceRuntime([workspace with { DisplayName = "Replacement" }]);
        AddSubagentSourceProvider(state, owner, "RuntimeCatalogService", replacement, AgentRpcContractIds.RuntimeCatalog);
        Assert.Equal("Replacement", Assert.Single(await discover(default)).DisplayName);

        // Startup remains nonblocking and owns cancellation of its initialization.
        replacement.Hold();
        var coordinator = Activator.CreateInstance(worker.GetType("Sunder.Package.Agent.Mcp.Services.McpConfigurationCoordinator", true)!, importer, sync, null)!;
        await Task.Run(() => coordinator.GetType().GetMethod("Start")!.Invoke(coordinator, null)).WaitAsync(TimeSpan.FromSeconds(2));
        await replacement.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ((IAsyncDisposable)coordinator).DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        replacement.Hold();
        var stoppingRead = discover(default);
        await replacement.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = lifetime.DisposeAsync().AsTask();
        Assert.Same(stopping, lifetime.DisposeAsync().AsTask());
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stoppingRead.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.All(rpc.Scopes, scope => Assert.True(scope.Disposed));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => discover(default));

        async Task<IReadOnlyList<AgentWorkspaceRecord>> discover(CancellationToken token)
        {
            var result = (await Call(instance, "ListKnownWorkspacesAsync", token))!;
            Assert.All(((IEnumerable)result).Cast<object>(), value => Assert.Same(worker, value.GetType().Assembly));
            return JsonSerializer.SerializeToElement(result, MemoryProviderJson).Deserialize<AgentWorkspaceRecord[]>(MemoryProviderJson)!;
        }
        static void WriteConfiguration(string path, string name)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new { mcp = new Dictionary<string, object> { [name] = new { type = "remote", url = "https://example.invalid/" + name } } }));
        }
        async Task<object?> Synchronize(CancellationToken token = default)
        {
            var task = (Task)syncType.GetMethod("SyncAsync")!.Invoke(sync, [token])!;
            await task.WaitAsync(TimeSpan.FromSeconds(10));
            return task.GetType().GetProperty("Result")!.GetValue(task);
        }
        async Task<object[]> Servers()
        {
            var task = (Task)catalogType.GetMethod("ListServersAsync")!.Invoke(catalog, [CancellationToken.None])!;
            await task;
            return ((IEnumerable)task.GetType().GetProperty("Result")!.GetValue(task)!).Cast<object>().ToArray();
        }
    }

    private sealed class McpWorkspaceRuntime(IReadOnlyList<AgentWorkspaceRecord> workspaces) : MemoryConsumerRuntime
    {
        internal IReadOnlyList<AgentWorkspaceRecord> Workspaces = workspaces;
        internal Exception? Failure;
        internal Action? AfterReply;
        internal bool Block;
        internal TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Hold() { Block = true; Started = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public override async ValueTask<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            if (Block) { Started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            AfterReply?.Invoke();
            return Workspaces;
        }
    }

    private sealed class McpWorkspaceRpc(ISunderRpcClient inner) : ISunderRpcClient
    {
        internal SunderRpcErrorKind? DiscoveryFailure;
        internal SunderRpcErrorKind? LookupFailure;
        internal Func<SunderRpcProviderSnapshot, SunderRpcProviderSnapshot>? LookupTransform;
        internal ConcurrentBag<Scope> Scopes { get; } = [];
        public async ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        {
            var scope = new Scope(this, await inner.CreateCallScopeAsync(options, cancellationToken));
            Scopes.Add(scope); return scope;
        }
        private static InvalidOperationException Unscoped() => new("Workspace discovery must use its operation-owned scope.");
        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default) => throw Unscoped();
        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default) => throw Unscoped();
        public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default) => throw Unscoped();
        public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default) => throw Unscoped();
        public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default) => throw Unscoped();
        public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default) => throw Unscoped();

        internal sealed class Scope(McpWorkspaceRpc owner, ISunderRpcCallScope inner) : ISunderRpcCallScope
        {
            internal bool Disposed;
            public DateTimeOffset DeadlineUtc => inner.DeadlineUtc;
            public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
            {
                Assert.Equal(AgentRpcContractIds.RuntimeCatalog, contractId);
                if (owner.DiscoveryFailure is { } kind) throw new SunderRpcException(new(kind, "test.workspace.discovery", "Test discovery failure."));
                return inner.DiscoverAsync(contractId, cancellationToken);
            }
            public async ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
            {
                if (owner.LookupFailure is { } kind) throw new SunderRpcException(new(kind, "test.workspace.lookup", "Test lookup failure."));
                var value = await inner.GetProviderAsync(endpoint, cancellationToken);
                return value is not null && owner.LookupTransform is not null ? owner.LookupTransform(value) : value;
            }
            public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
            {
                Assert.Equal("runtime-catalog", serviceId); Assert.Equal("list-workspaces", methodId);
                return inner.InvokeAsync(endpoint, serviceId, methodId, request, options, cancellationToken);
            }
            public async ValueTask DisposeAsync() { await inner.DisposeAsync(); Disposed = true; }
            public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default) => inner.TryReportInvariantViolationAsync(endpoint, exception, cancellationToken);
            public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<SunderRpcContentReference> RegisterContentAsync(SunderRpcEndpointReference endpoint, Stream source, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(SunderRpcEndpointReference endpoint, string filePath, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<Stream> OpenContentAsync(SunderRpcContentReference reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
    }
}
