extern alias McpWorker;
extern alias McpApp;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using McpWorker::Sunder.Package.Agent.Mcp;
using McpWorker::Sunder.Package.Agent.Mcp.Runtime;
using McpWorker::Sunder.Package.Agent.Mcp.Services;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;
using McpAppManagementService = McpApp::Sunder.Generated.Packages.Sunder_Package_Agent_Mcp_Services.IManagementService;
using McpServices = McpWorker::Sunder.Generated.Packages.Sunder_Package_Agent_Services;

namespace Sunder.Package.Agent.Tests;

public sealed class McpWorkerCompositionTests
{
    [Fact]
    [Trait("Lane", "PackageCompatibility")]
    public async Task WorkerAndAppComposition_KeepAuthorityInTheWorker()
    {
        using var scope = RegressionTestPackageScope.Create();
        var options = AgentSourceArtifactMigrationTests.CreateGeneratedWorker(typeof(McpToolSource).Assembly, scope.Context, new IdleRpcClient()).Options;
        await using var composition = Assert.IsType<SunderWorkerComposition>(options.Composition);

        Assert.Equal(
            [
                "mcp.management",
                "mcp.selectable.capabilities",
                "mcp.session.cleaner",
                "mcp.stack",
                "mcp.tools",
                "mcp.tools.observed",
            ],
            options.Providers.Select(provider => provider.ProviderId).Order(StringComparer.Ordinal));
        Assert.All(options.Providers, provider => Assert.IsAssignableFrom<ISunderRpcServiceHandler>(provider.Handler));
        Assert.IsType<McpOAuthCallbackHandler>(Assert.Single(options.CallbackHandlers));
        Assert.Empty(options.StateMigrations);
        Assert.Empty(options.SecretMigrations);
        Assert.Null(options.OnCandidateStarted);
        Assert.Null(options.OnBeforeStorageMigrations);
        Assert.Null(options.OnActivated);
        Assert.NotNull(composition.Services.GetService<McpServerCatalogService>());
        Assert.NotNull(composition.Services.GetService<McpClientConnectionManager>());
        Assert.NotNull(composition.Services.GetService<McpToolSource>());
        Assert.NotNull(composition.Services.GetService<McpServerStackContributor>());
        Assert.NotNull(composition.Services.GetService<McpManagementService>());
        Assert.Null(composition.Services.GetService<McpApp::Sunder.Package.Agent.Mcp.AgentMcpSettingsViewModel>());
        await composition.Services.GetRequiredService<McpWorker::Sunder.Package.Agent.Mcp.Worker.McpActivation>()
            .ActivateAsync(null!, CancellationToken.None);

        var appServices = new ServiceCollection();
        new McpApp::Sunder.Generated.Package.AppModule().ConfigureAppServices(appServices, scope.Context);
        Assert.Contains(appServices, descriptor => descriptor.ServiceType == typeof(McpAppManagementService));
        Assert.Contains(appServices, descriptor => descriptor.ServiceType == typeof(McpApp::Sunder.Package.Agent.Mcp.Runtime.McpAppRuntimeGateway));
        Assert.Contains(appServices, descriptor => descriptor.ServiceType == typeof(McpApp::Sunder.Package.Agent.Mcp.AgentMcpSettingsViewModel));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(McpServerCatalogService));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(McpClientConnectionManager));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(McpToolSource));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(McpServerStackContributor));
    }

    [Fact]
    public async Task Startup_DoesNotWaitForInitialCrossPackageSync()
    {
        using var scope = RegressionTestPackageScope.Create();
        var rpc = new BlockingDiscoverRpcClient();
        await using var receiver = new GeneratedServiceTestScope(rpc);
        var importer = new McpEcosystemConfigurationImporter(new McpServerCatalogService(scope.Context));
        var sync = new McpSunderConfigurationSyncService(
            importer,
            receiver.Providers<McpServices.IRuntimeCatalogService>(),
            userProfilePath: scope.RootPath);
        await using var coordinator = new McpConfigurationCoordinator(importer, sync);

        await Task.Run(coordinator.Start).WaitAsync(TimeSpan.FromSeconds(2));
        await rpc.DiscoverStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(RegressionTestBindings.RuntimeCatalog, rpc.DiscoveredContractId);
        Assert.False(rpc.CancellationObserved.Task.IsCompleted);

        await receiver.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await rpc.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class IdleRpcClient : ISunderRpcClient
    {
        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
            SunderRpcEndpointReference endpoint,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<SunderRpcProviderSnapshot?>(null);

        public ValueTask<bool> TryReportInvariantViolationAsync(
            SunderRpcEndpointReference endpoint,
            Exception exception,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
            string contractId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SunderRpcCatalogSnapshot>(new NotSupportedException());

        public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
            long afterRevision,
            long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask<JsonElement> InvokeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<JsonElement>(new NotSupportedException());

        public async IAsyncEnumerable<JsonElement> SubscribeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class BlockingDiscoverRpcClient : ISunderRpcCallScope
    {
        public DateTimeOffset DeadlineUtc { get; } = DateTimeOffset.UtcNow.AddMinutes(1);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ISunderRpcCallScope>(this);
        public ValueTask<SunderRpcContentReference> RegisterContentAsync(SunderRpcEndpointReference endpoint, Stream source,
            SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(SunderRpcEndpointReference endpoint, string filePath,
            SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<Stream> OpenContentAsync(SunderRpcContentReference reference, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        private readonly TaskCompletionSource<SunderRpcCatalogSnapshot> _discover = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DiscoverStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string? DiscoveredContractId { get; private set; }

        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
            SunderRpcEndpointReference endpoint,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<SunderRpcProviderSnapshot?>(null);

        public ValueTask<bool> TryReportInvariantViolationAsync(
            SunderRpcEndpointReference endpoint,
            Exception exception,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
            string contractId,
            CancellationToken cancellationToken = default)
        {
            DiscoveredContractId = contractId;
            DiscoverStarted.TrySetResult();
            cancellationToken.Register(() =>
            {
                CancellationObserved.TrySetResult();
                _discover.TrySetCanceled(cancellationToken);
            });
            return new ValueTask<SunderRpcCatalogSnapshot>(_discover.Task);
        }

        public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
            long afterRevision,
            long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask<JsonElement> InvokeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<JsonElement>(new NotSupportedException());

        public async IAsyncEnumerable<JsonElement> SubscribeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }
}
