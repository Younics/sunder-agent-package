extern alias McpWorker;
extern alias McpApp;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using McpWorker::Sunder.Package.Agent.Mcp;
using McpWorker::Sunder.Package.Agent.Mcp.Runtime;
using McpWorker::Sunder.Package.Agent.Mcp.Services;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;
using McpAppManagementGateway = McpApp::Sunder.Package.Agent.Mcp.Runtime.IMcpManagementGateway;
using McpAppRuntimeGateway = McpApp::Sunder.Package.Agent.Mcp.Runtime.McpAppRuntimeGateway;
using McpAppManagementService = McpApp::Sunder.Generated.Packages.Sunder_Package_Agent_Mcp_Services.IManagementService;

namespace Sunder.Package.Agent.Tests;

public sealed class McpManagementSourceMigrationTests
{
    internal static readonly string[] SourcePaths =
    [
        "src/Sunder.Package.Agent.Mcp/Runtime/McpManagementService.cs",
        "src/Sunder.Package.Agent.Mcp/Rpc/AppComposition.cs",
        "src/Sunder.Package.Agent.Mcp/Runtime/McpAppRpcGateway.cs",
    ];

    [AgentPublicSourceMigrationFact]
    public async Task ManagementSource_PreservesContractAndContentOperationsAcrossIsolatedApp()
    {
        var descriptor = CurrentPrivateRpcDescriptors.Get("sunder.package.agent.mcp.management");
        Assert.Equal("sunder.package.agent.mcp.management", descriptor.ContractId);
        await Verify();
    }

    private static async Task Verify()
    {
        using var package = RegressionTestPackageScope.Create();
        var catalog = new McpServerCatalogService(package.Context);
        var editor = new McpSettingsEditorService(catalog);
        await using var connections = new McpClientConnectionManager(NullLoggerFactory.Instance);
        var connectionService = new McpServerConnectionService(catalog, connections, oauthService: null);
        await using var configuration = new McpConfigurationCoordinator(
            new McpEcosystemConfigurationImporter(catalog),
            syncService: null);
        var oauth = new McpOAuthCoordinator(oauthService: null, connections);
        var service = new McpManagementService(catalog, editor, configuration, connectionService, oauth, package.Context);
        var registration = service.CreateSunderRpcProviderRegistration();
        Assert.Equal("mcp.management", registration.ProviderId);
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent.mcp");
        await using var receiver = new GeneratedServiceTestScope(rpc);
        state.AddGenerated("sunder.package.agent.mcp", service, registration, Noop.Instance, registration.ProviderId);
        var selected = Assert.Single((await receiver.Providers<McpAppManagementService>().ListAsync()).Providers);
        var appClient = selected.Service;
        await using var gatewayLifetime = new McpAppRuntimeGateway(
            appClient,
            package.Context.Callbacks,
            package.Context.Logging.LoggerFactory);
        McpAppManagementGateway gateway = gatewayLifetime;
        Assert.Empty(await gateway.ListAsync());
        var oversizedImport = Path.Combine(package.RootPath, "oversized-mcp.json");
        await File.WriteAllTextAsync(oversizedImport, new string('x', McpManagementContent.MaximumPayloadBytes + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => gateway.ImportFileAsync(oversizedImport));
        state.FailNextResponseContentRegistration(new IOException("private MCP content failure"));
        var contentFailure = await Assert.ThrowsAsync<SunderRpcException>(() => gateway.ListAsync());
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, contentFailure.Error.Kind);
        Assert.DoesNotContain("private MCP content failure", contentFailure.Message, StringComparison.Ordinal);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.ServersChanged += () => changed.TrySetResult();
        var parsed = gatewayLifetime.Parse("source-server", "source_server", """{"type":"remote","url":"https://example.com/mcp","headers":{"X-Secret":"test-only-secret"}}""", null);
        var saved = await gateway.SaveAsync(parsed);
        Assert.DoesNotContain("test-only-secret", saved, StringComparison.Ordinal);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var document = await gateway.LoadDocumentAsync("source-server");
        Assert.DoesNotContain("test-only-secret", document!, StringComparison.Ordinal);
        var missing = await Assert.ThrowsAsync<SunderRpcException>(() => gateway.LoadDocumentAsync("missing"));
        Assert.Equal("mcp.management.failed", missing.Error.Code);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.DeleteAsync("source-server", cancelled.Token));
            Assert.NotNull(await catalog.GetServerAsync("source-server"));
        }
        await gateway.DeleteAsync("source-server");
        Assert.Empty(await gateway.ListAsync());
        await state.RetireAsync(service);
        Assert.True(selected.RetirementToken.IsCancellationRequested);
        var stale = await Assert.ThrowsAsync<SunderRpcException>(() => selected.EnsureCurrentAsync().AsTask());
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, stale.Error.Kind);
        var retired = await Assert.ThrowsAsync<SunderRpcException>(() => gateway.ListAsync());
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, retired.Error.Kind);
        Assert.Equal("rpc.stale-endpoint", retired.Error.Code);
    }

    private sealed class Noop : IDisposable { internal static Noop Instance { get; } = new(); public void Dispose() { } }
}
