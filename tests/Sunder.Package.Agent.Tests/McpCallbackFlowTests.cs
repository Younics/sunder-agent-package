using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Mcp;
using Sunder.Package.Agent.Mcp.Runtime;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Runtime;
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
        var services = new ServiceCollection();
        services.AddSingleton<IPackageRuntimeClient>(new IdleRuntimeClient());

        new Sunder.Package.Agent.Mcp.AppPackageModule().ConfigureAppServices(services, context);

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IPackageCallbackClient));
        await using var provider = services.BuildServiceProvider();
        var gateway = Assert.IsType<McpAppRuntimeGateway>(
            provider.GetRequiredService<IMcpManagementGateway>());
        await gateway.AuthorizeAsync(CreateServer(), CancellationToken.None);
        Assert.Equal(McpOAuthCallbackHandler.HandlerId, callbacks.HandlerId);
    }

    [Fact]
    public async Task AppGateway_AuthorizeUsesSharedCallbackFlowAndPreservesServerParameter()
    {
        var callbacks = new FakeCallbackClient();
        using var gateway = new McpAppRuntimeGateway(new IdleRuntimeClient(), callbacks);
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

    private sealed class IdleRuntimeClient : IPackageRuntimeClient
    {
        public bool IsAvailable => true;

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
            => ValueTask.FromException<TResponse>(new NotSupportedException());

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }
}
