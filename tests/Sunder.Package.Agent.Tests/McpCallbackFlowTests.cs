extern alias McpApp;
extern alias McpWorker;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using McpApp::Sunder.Package.Agent.Mcp;
using McpApp::Sunder.Package.Agent.Mcp.Runtime;
using McpApp::Sunder.Package.Agent.Mcp.Services;
using McpOAuthCallbackHandler = McpWorker::Sunder.Package.Agent.Mcp.Services.McpOAuthCallbackHandler;
using McpAppManagementService = McpApp::Sunder.Generated.Packages.Sunder_Package_Agent_Mcp_Services.IManagementService;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class McpCallbackFlowTests
{
    [Fact]
    public async Task AppModule_UsesContextCallbacksWithoutRegisteringReservedCallbackService()
    {
        using var scope = RegressionTestPackageScope.Create();
        var callbacks = new FakeCallbackClient();
        var context = new CallbackPackageContext(scope.Context, callbacks);
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent.mcp");
        var generated = AgentSourceArtifactMigrationTests.CreateGeneratedWorker(
            typeof(McpWorker::Sunder.Package.Agent.Mcp.McpToolSource).Assembly,
            context,
            rpc);
        await using var worker = generated.Lifetime;
        var registration = Assert.Single(generated.Options.Providers,
            provider => provider.ProviderId == "mcp.management");
        var descriptor = CurrentPrivateRpcDescriptors.Get("sunder.package.agent.mcp.management");
        state.Add(
            context.PackageId,
            descriptor,
            registration.Handler,
            registration.Handler,
            Noop.Instance,
            registration.ProviderId,
            context.Version);
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(context);
        services.AddSingleton<Sunder.Sdk.Rpc.ISunderRpcClient>(rpc);

        new McpApp::Sunder.Generated.Package.AppModule().ConfigureAppServices(services, context);

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IPackageCallbackClient));
        await using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<McpAppRuntimeGateway>();
        await gateway.AuthorizeAsync(CreateServer(), CancellationToken.None);
        Assert.Equal(McpOAuthCallbackHandler.HandlerId, callbacks.HandlerId);
    }

    [Fact]
    public async Task AppGateway_AuthorizeUsesSharedCallbackFlowAndPreservesServerParameter()
    {
        var callbacks = new FakeCallbackClient();
        using var gateway = new McpAppRuntimeGateway(new IdleManagementClient(), callbacks);
        var statusChanges = 0;
        gateway.StatusChanged += () => statusChanges++;
        var server = CreateServer();

        await gateway.AuthorizeAsync(server, CancellationToken.None);

        Assert.Equal(McpOAuthCallbackHandler.HandlerId, callbacks.HandlerId);
        Assert.Equal("remote-one", callbacks.Parameters?[McpOAuthCallbackHandler.ServerIdParameter]);
        Assert.Equal(1, callbacks.OpenCount);
        Assert.Equal(1, callbacks.StatusCount);
        Assert.Equal(1, statusChanges);
    }

    [FileSystemFact(
        TestFileSystemCapability.FileSymbolicLinks,
        "MCP import-link rejection requires file symbolic links.")]
    public async Task AppGateway_ImportFileRejectsSymbolicLinkBeforeOwnerCommand()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-mcp-app-import-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "target.json");
            var link = Path.Combine(root, "linked.json");
            await File.WriteAllTextAsync(target, "{\"mcp\":{}}");
            File.CreateSymbolicLink(link, target);
            using var gateway = new McpAppRuntimeGateway(
                new IdleManagementClient(),
                new FakeCallbackClient());

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                gateway.ImportFileAsync(link, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AppGateway_MalformedChangeReconnectsAndShutsDownObservation()
    {
        var client = new ReconnectingManagementClient();
        var gateway = new McpAppRuntimeGateway(client, new FakeCallbackClient());
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.ServersChanged += () => changed.TrySetResult();
        try
        {
            await client.SecondSubscriptionStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.True(gateway.IsOwnerStreamAvailable);
            Assert.Equal(2, client.SubscriptionCount);

            await gateway.DisposeAsync();
            Assert.True(gateway.ObservationTask.IsCompletedSuccessfully);
            Assert.False(gateway.IsOwnerStreamAvailable);
        }
        finally
        {
            gateway.Dispose();
        }
    }

    private static ConfiguredMcpServerRecord CreateServer() => new()
    {
        ServerId = "remote-one",
        Name = "remote-one",
        DisplayName = "Remote one",
        IsEnabled = true,
        TransportType = ConfiguredMcpTransportType.HttpSse,
        EndpointUrl = "https://mcp.example/api",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };

    private sealed class CallbackPackageContext(
        IPackageContext inner,
        IPackageCallbackClient callbacks) : IPackageContext
    {
        public string PackageId => inner.PackageId;
        public string Version => inner.Version;
        public string ContentRootPath => inner.ContentRootPath;
        public IPackageStorageContext Storage => inner.Storage;
        public IPackageSettings Settings => inner.Settings;
        public IPackageSecrets Secrets => inner.Secrets;
        public IPackageCallbackClient Callbacks => callbacks;
        public Sunder.Sdk.Logging.IPackageLogging Logging => inner.Logging;
    }

    private sealed class FakeCallbackClient : IPackageCallbackClient
    {
        public bool IsAvailable => true;
        public string? HandlerId { get; private set; }
        public IReadOnlyDictionary<string, string>? Parameters { get; private set; }
        public int OpenCount { get; private set; }
        public int StatusCount { get; private set; }

        public ValueTask<PackageCallbackSessionStatus> StartAsync(
            string callbackHandlerId,
            IReadOnlyDictionary<string, string>? parameters = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HandlerId = callbackHandlerId;
            Parameters = parameters;
            return ValueTask.FromResult(Status(PackageCallbackSessionState.Pending));
        }

        public ValueTask<PackageCallbackSessionStatus> GetStatusAsync(
            string callbackSessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StatusCount++;
            return ValueTask.FromResult(Status(PackageCallbackSessionState.Completed));
        }

        public ValueTask<bool> CancelAsync(
            string callbackSessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(false);
        }

        public ValueTask<PackageCallbackSessionStatus> WaitForCompletionAsync(
            string callbackSessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
            => GetStatusAsync(callbackSessionId, cancellationToken);

        public ValueTask OpenLaunchUriAsync(Uri launchUri, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
            return ValueTask.CompletedTask;
        }

        private static PackageCallbackSessionStatus Status(PackageCallbackSessionState state)
            => new(
                "sunder.package.agent.mcp",
                McpOAuthCallbackHandler.HandlerId,
                "mcp-session",
                state,
                "MCP OAuth status.",
                new Uri("https://mcp.example/authorize"),
                DateTimeOffset.UtcNow.AddMinutes(5));
    }

    private class ManagementClientStub : McpAppManagementService
    {
        public virtual ValueTask<SunderContent> CatalogAsync(
            CancellationToken cancellationToken = default)
            => Unavailable();

        public virtual ValueTask<SunderContent> DocumentAsync(
            string serverId,
            CancellationToken cancellationToken = default)
            => Unavailable();

        public virtual ValueTask<SunderContent> PresentationAsync(
            string serverId,
            CancellationToken cancellationToken = default)
            => Unavailable();

        public virtual ValueTask<SunderContent> InitializeAsync(
            CancellationToken cancellationToken = default)
            => Unavailable();

        public virtual ValueTask<SunderContent> SaveAsync(
            SunderContent configurationDocument,
            CancellationToken cancellationToken = default)
            => Unavailable();

        public virtual ValueTask<SunderContent> DeleteAsync(
            string serverId,
            CancellationToken cancellationToken = default)
            => Unavailable();

        public virtual ValueTask<SunderContent> ImportCommonAsync(
            CancellationToken cancellationToken = default)
            => Unavailable();

        public virtual ValueTask<SunderContent> ImportDocumentAsync(
            SunderContent document,
            CancellationToken cancellationToken = default)
            => Unavailable();

        public virtual ValueTask<SunderContent> DiscoverAsync(
            string serverId,
            bool reconnect,
            CancellationToken cancellationToken = default)
            => Unavailable();

        public virtual ValueTask<SunderContent> DisconnectAsync(
            string serverId,
            CancellationToken cancellationToken = default)
            => Unavailable();

        public virtual ValueTask<SunderContent> ClearAuthorizationAsync(
            string serverId,
            CancellationToken cancellationToken = default)
            => Unavailable();

        public virtual async IAsyncEnumerable<McpChanged> ChangesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        private static ValueTask<SunderContent> Unavailable()
            => ValueTask.FromException<SunderContent>(new NotSupportedException());
    }

    private sealed class IdleManagementClient : ManagementClientStub;

    private sealed class ReconnectingManagementClient : ManagementClientStub
    {
        private int _subscriptionCount;

        internal int SubscriptionCount => Volatile.Read(ref _subscriptionCount);
        internal TaskCompletionSource SecondSubscriptionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async IAsyncEnumerable<McpChanged> ChangesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var subscription = Interlocked.Increment(ref _subscriptionCount);
            yield return new McpChanged(McpChangeKind.Servers);
            if (subscription == 1)
            {
                yield return new McpChanged((McpChangeKind)int.MaxValue);
                yield break;
            }

            SecondSubscriptionStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class Noop : IDisposable
    {
        internal static Noop Instance { get; } = new();
        public void Dispose() { }
    }
}
