extern alias McpWorker;

using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using McpWorker::Sunder.Package.Agent.Mcp;
using McpWorker::Sunder.Package.Agent.Mcp.Runtime;
using McpWorker::Sunder.Package.Agent.Mcp.Services;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class McpManagementSourceMigrationTests
{
    internal static readonly string[] SourcePaths =
    [
        "src/Sunder.Package.Agent.Mcp/Rpc/ManagementService.cs",
        "src/Sunder.Package.Agent.Mcp/Rpc/ManagementClient.cs",
        "src/Sunder.Package.Agent.Mcp/Runtime/McpRuntimeManagementClient.cs",
    ];

    [AgentPublicSourceMigrationFact]
    public async Task ManagementSource_PreservesContractAndContentOperationsAcrossIsolatedApp()
    {
        using var source = PrivateManagementSourceFixture.Load("Mcp", RegressionTestRpcDescriptors.Get("sunder.package.agent.mcp.management"));
        Assert.Equal("sunder.package.agent.mcp.management", source.Descriptor.ContractId);
        await Verify(source.Worker, source.App, source.Descriptor);
    }

    private static async Task Verify(Assembly worker, Assembly app, SunderRpcContractDescriptor descriptor)
    {
        using var package = RegressionTestPackageScope.Create();
        var catalog = new McpServerCatalogService(package.Context);
        var editor = new McpSettingsEditorService(catalog);
        await using var connections = new McpClientConnectionManager(NullLoggerFactory.Instance);
        var connectionService = new McpServerConnectionService(catalog, connections, oauthService: null);
        var operations = new McpRuntimeHandler(catalog, editor, configuration: null!, connectionService, oauth: null!, package.Context);
        var domain = new McpRuntimeManagementClient(operations, new McpRuntimeChangeStream(editor, connectionService));
        var type = worker.GetType("Sunder.Package.Agent.Mcp.Rpc.ManagementService", true)!;
        var service = AgentRoleBoundary.Create(type, domain);
        var registration = Assert.IsType<SunderWorkerProviderRegistration>(type.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(service, null));
        Assert.Equal("mcp.management", registration.ProviderId);
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent.mcp");
        state.Add("sunder.package.agent.mcp", descriptor, service, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
        var client = AgentRoleBoundary.As<IMcpOwnerManagementClient>(AgentRoleBoundary.Create(app.GetType("Sunder.Package.Agent.Mcp.Runtime.McpOwnerManagementRpcClient", true)!, rpc));
        var initial = await client.QueryAsync(new(McpQueryKind.Catalog));
        Assert.Empty(initial.Servers!);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.CommandAsync(new(McpCommandKind.ImportDocument,
            Document: new string('x', McpManagementContent.MaximumPayloadBytes + 1))).AsTask());
        state.FailNextResponseContentRegistration(new IOException("private MCP content failure"));
        var contentFailure = await Assert.ThrowsAsync<SunderRpcException>(() => client.QueryAsync(new(McpQueryKind.Catalog)).AsTask());
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, contentFailure.Error.Kind);
        Assert.DoesNotContain("private MCP content failure", contentFailure.Message, StringComparison.Ordinal);
        using var lifetime = new CancellationTokenSource();
        await using var changes = client.WatchChangesAsync(lifetime.Token).GetAsyncEnumerator();
        Assert.True(await changes.MoveNextAsync()); Assert.Equal(McpChangeKind.Servers, changes.Current.Kind);
        Assert.True(await changes.MoveNextAsync()); Assert.Equal(McpChangeKind.Servers, changes.Current.Kind);
        Assert.True(await changes.MoveNextAsync()); Assert.Equal(McpChangeKind.Status, changes.Current.Kind);
        var next = changes.MoveNextAsync().AsTask();
        var parsed = McpConfigurationDocument.Parse("source-server", "source_server", """{"type":"remote","url":"https://example.com/mcp","headers":{"X-Secret":"test-only-secret"}}""");
        var saved = await client.CommandAsync(new(McpCommandKind.Save, Configuration: parsed));
        Assert.Equal("source-server", Assert.Single(saved.Servers!).ServerId);
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        var document = await client.QueryAsync(new(McpQueryKind.Document, "source-server"));
        Assert.DoesNotContain("test-only-secret", document.Document!, StringComparison.Ordinal);
        var missing = await Assert.ThrowsAsync<SunderRpcException>(() => client.QueryAsync(new(McpQueryKind.Document, "missing")).AsTask());
        Assert.Equal("mcp.management.failed", missing.Error.Code);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CommandAsync(new(McpCommandKind.Delete, "source-server"), cancelled.Token).AsTask());
            Assert.NotNull(await catalog.GetServerAsync("source-server"));
        }
        var deleted = await client.CommandAsync(new(McpCommandKind.Delete, "source-server"));
        Assert.Empty(deleted.Servers!);
        await lifetime.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => changes.MoveNextAsync().AsTask());
        await state.RetireAsync(service);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.QueryAsync(new(McpQueryKind.Catalog)).AsTask());
    }

    private sealed class Noop : IDisposable { internal static Noop Instance { get; } = new(); public void Dispose() { } }
}
