using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Mcp;
using Sunder.Package.Agent.Mcp.Runtime;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Package.Agent.Mcp.Worker;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class McpWorkerCompositionTests
{
    [Fact]
    public async Task WorkerAndAppComposition_KeepAuthorityInTheWorker()
    {
        using var scope = RegressionTestPackageScope.Create();
        var options = McpWorkerConfiguration.CreateCore(scope.Context, new IdleRpcClient());
        await using var composition = Assert.IsType<SunderWorkerComposition>(options.Composition);

        Assert.Equal(
            [
                "mcp.tools",
                "mcp.tools.observed",
                "mcp.selectable.capabilities",
                "mcp.session.cleaner",
                "mcp.stack",
                McpManagementProtocol.ProviderId,
            ],
            options.Providers.Select(provider => provider.ProviderId));
        Assert.All(options.Providers, provider => Assert.IsAssignableFrom<ISunderRpcServiceHandler>(provider.Handler));
        Assert.IsType<McpOAuthCallbackHandler>(Assert.Single(options.CallbackHandlers));
        Assert.Empty(options.StateMigrations);
        Assert.Empty(options.SecretMigrations);
        Assert.Null(options.OnCandidateStarted);
        Assert.Null(options.OnBeforeStorageMigrations);
        Assert.NotNull(options.OnActivated);
        Assert.NotNull(composition.Services.GetService<McpServerCatalogService>());
        Assert.NotNull(composition.Services.GetService<McpClientConnectionManager>());
        Assert.NotNull(composition.Services.GetService<McpToolSource>());
        Assert.NotNull(composition.Services.GetService<McpServerStackContributor>());
        Assert.NotNull(composition.Services.GetService<McpManagementRpcProvider>());
        Assert.Null(composition.Services.GetService<AgentMcpSettingsViewModel>());
        await options.OnActivated!(null!, CancellationToken.None);

        var appServices = new ServiceCollection();
        new Sunder.Package.Agent.Mcp.AppPackageModule().ConfigureAppServices(appServices, scope.Context);
        Assert.Contains(appServices, descriptor => descriptor.ServiceType == typeof(IMcpOwnerManagementClient));
        Assert.Contains(appServices, descriptor => descriptor.ServiceType == typeof(IMcpManagementGateway));
        Assert.Contains(appServices, descriptor => descriptor.ServiceType == typeof(AgentMcpSettingsViewModel));
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
        using var rpcCatalog = AgentRpcCatalog.CreateDormant(rpc);
        var importer = new McpEcosystemConfigurationImporter(new McpServerCatalogService(scope.Context));
        var sync = new McpSunderConfigurationSyncService(
            importer,
            rpcCatalog,
            userProfilePath: scope.RootPath);
        var coordinator = new McpConfigurationCoordinator(importer, sync);

        await Task.Run(coordinator.Start).WaitAsync(TimeSpan.FromSeconds(2));
        await rpc.DiscoverStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        await rpc.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
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

    private sealed class BlockingDiscoverRpcClient : ISunderRpcClient
    {
        private readonly TaskCompletionSource<SunderRpcCatalogSnapshot> _discover = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DiscoverStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

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
