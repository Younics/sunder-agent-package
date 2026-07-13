using System.Runtime.CompilerServices;
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
    public async Task AppGateway_AuthorizeUsesSharedCallbackFlowAndPreservesServerParameter()
    {
        var callbacks = new FakeCallbackClient();
        using var gateway = new McpAppRuntimeGateway(new IdleRuntimeClient(), callbacks);
        var statusChanges = 0;
        gateway.StatusChanged += () => statusChanges++;
        var server = new ConfiguredMcpServerRecord
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

        await gateway.AuthorizeAsync(server, CancellationToken.None);

        Assert.Equal(McpOAuthCallbackHandler.HandlerId, callbacks.HandlerId);
        Assert.Equal("remote-one", callbacks.Parameters?[McpOAuthCallbackHandler.ServerIdParameter]);
        Assert.Equal(1, callbacks.OpenCount);
        Assert.Equal(1, callbacks.StatusCount);
        Assert.Equal(1, statusChanges);
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
