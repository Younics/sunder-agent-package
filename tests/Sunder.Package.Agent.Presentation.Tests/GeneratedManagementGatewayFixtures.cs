extern alias McpApp;
extern alias McpWorker;
extern alias SkillsApp;
extern alias SkillsWorker;

using Microsoft.Extensions.Logging;
using McpWorker::Sunder.Package.Agent.Mcp;
using McpWorker::Sunder.Package.Agent.Mcp.Runtime;
using McpWorker::Sunder.Package.Agent.Mcp.Services;
using SkillsWorker::Sunder.Package.Agent.Skills.Runtime;
using SkillsWorker::Sunder.Package.Agent.Skills.Services;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using McpAppManagementService = McpApp::Sunder.Generated.Packages.Sunder_Package_Agent_Mcp_Services.IManagementService;
using McpAppRuntimeGateway = McpApp::Sunder.Package.Agent.Mcp.Runtime.McpAppRuntimeGateway;
using SkillsAppManagementService = SkillsApp::Sunder.Generated.Packages.Sunder_Package_Agent_Skills_Services.ISkillsManagementService;
using SkillsAppRuntimeGateway = SkillsApp::Sunder.Package.Agent.Skills.Runtime.SkillAppRuntimeGateway;

namespace Sunder.Package.Agent.Presentation.Tests;

internal sealed class McpManagementGatewayFixture : IAsyncDisposable
{
    private readonly RegressionTestExtensionCatalog _catalog;
    private readonly GeneratedServiceTestScope _receiver;
    private readonly McpConfigurationCoordinator _configuration;

    private McpManagementGatewayFixture(
        RegressionTestExtensionCatalog catalog,
        GeneratedServiceTestScope receiver,
        McpConfigurationCoordinator configuration,
        McpAppRuntimeGateway gateway)
    {
        _catalog = catalog;
        _receiver = receiver;
        _configuration = configuration;
        Gateway = gateway;
    }

    internal McpAppRuntimeGateway Gateway { get; }

    internal static McpManagementGatewayFixture Create(
        IPackageContext context,
        McpServerCatalogService catalog,
        McpClientConnectionManager connections,
        McpOAuthService? oauth = null)
    {
        McpFixturePhaseLog.Mark("CreateRuntime:start");
        var configuration = new McpConfigurationCoordinator(
            new McpEcosystemConfigurationImporter(catalog),
            syncService: null);
        McpFixturePhaseLog.Mark("CreateRuntime:configuration-created");
        var service = new McpManagementService(catalog, new McpSettingsEditorService(catalog), configuration,
            new McpServerConnectionService(catalog, connections, oauth),
            new McpOAuthCoordinator(oauth, connections), context);
        McpFixturePhaseLog.Mark("CreateRuntime:service-created");
        var extensions = new RegressionTestExtensionCatalog();
        var receiver = new GeneratedServiceTestScope(extensions.RpcForCaller("sunder.package.agent.mcp"));
        try
        {
            McpFixturePhaseLog.Mark("CreateRuntime:before-provider-registration");
            extensions.AddGeneratedProvider("sunder.package.agent.mcp", service, service.CreateSunderRpcProviderRegistration());
            McpFixturePhaseLog.Mark("CreateRuntime:after-provider-registration");
            McpFixturePhaseLog.Mark("CreateRuntime:before-client-discovery");
            var client = receiver.Providers<McpAppManagementService>().ListAsync().AsTask().GetAwaiter().GetResult()
                .Providers.Single().Service;
            McpFixturePhaseLog.Mark("CreateRuntime:after-client-discovery");
            McpFixturePhaseLog.Mark("CreateRuntime:before-gateway-construction");
            var gateway = new McpAppRuntimeGateway(client, context.Callbacks, context.Logging.LoggerFactory);
            McpFixturePhaseLog.Mark("CreateRuntime:after-gateway-construction");
            return new(extensions, receiver, configuration, gateway);
        }
        catch
        {
            McpFixturePhaseLog.Mark("CreateRuntime:failure-cleanup");
            receiver.DisposeAsync().AsTask().GetAwaiter().GetResult();
            configuration.DisposeAsync().AsTask().GetAwaiter().GetResult();
            extensions.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        McpFixturePhaseLog.Mark("Dispose:start");
        Gateway.Dispose();
        McpFixturePhaseLog.Mark("Dispose:gateway-stopped");
        await _receiver.DisposeAsync();
        McpFixturePhaseLog.Mark("Dispose:receiver-disposed");
        await Gateway.DisposeAsync();
        McpFixturePhaseLog.Mark("Dispose:gateway-drained");
        await _configuration.DisposeAsync();
        McpFixturePhaseLog.Mark("Dispose:configuration-disposed");
        _catalog.Dispose();
        McpFixturePhaseLog.Mark("Dispose:catalog-disposed");
    }
}

internal static class McpFixturePhaseLog
{
    private static readonly object Gate = new();

    internal static void Mark(string phase)
    {
        var path = Environment.GetEnvironmentVariable("SUNDER_TEST_PHASE_LOG");
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (Gate)
        {
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {phase}{Environment.NewLine}");
        }
    }
}

internal sealed class SkillManagementGatewayFixture : IAsyncDisposable
{
    private readonly RegressionTestRpcState _state;
    private readonly GeneratedServiceTestScope _receiver;
    private readonly SkillsAppManagementService _client;

    private SkillManagementGatewayFixture(
        RegressionTestRpcState state,
        GeneratedServiceTestScope receiver,
        SkillsAppManagementService client,
        SkillsAppRuntimeGateway gateway)
    {
        _state = state;
        _receiver = receiver;
        _client = client;
        Gateway = gateway;
    }

    internal SkillsAppRuntimeGateway Gateway { get; }
    internal async Task<SunderRpcException> QueryFailureAsync()
    {
        try
        {
            await _client.QueryAsync();
        }
        catch (SunderRpcException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected the malformed Skills index to fault.");
    }

    internal static SkillManagementGatewayFixture Create(
        IPackageContext context,
        SkillStore store,
        SkillImportService importer)
    {
        var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var receiver = new GeneratedServiceTestScope(new RegressionTestRpcClient(state, "sunder.package.agent.skills"));
        try
        {
            var handler = new SkillRuntimeHandler(store, importer, context);
            state.AddGenerated("sunder.package.agent.skills", handler, handler.CreateSunderRpcProviderRegistration(), EmptyLifetime.Instance);
            var client = receiver.Providers<SkillsAppManagementService>().ListAsync().AsTask().GetAwaiter().GetResult()
                .Providers.Single().Service;
            return new(state, receiver, client, new SkillsAppRuntimeGateway(client, context));
        }
        catch
        {
            receiver.DisposeAsync().AsTask().GetAwaiter().GetResult();
            state.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Gateway.Dispose();
        await _receiver.DisposeAsync();
        await Gateway.DisposeAsync();
        _state.Dispose();
    }

    private sealed class EmptyLifetime : IDisposable
    {
        internal static EmptyLifetime Instance { get; } = new();
        public void Dispose() { }
    }
}
