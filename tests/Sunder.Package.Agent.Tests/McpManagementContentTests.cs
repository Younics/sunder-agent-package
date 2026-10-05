extern alias McpWorker;

using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using McpWorker::Sunder.Generated.Rpc.Management;
using McpWorker::Sunder.Package.Agent.Mcp.Runtime;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class McpManagementContentTests
{
    private static readonly JsonSerializerOptions ContentJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    [Fact]
    public async Task GeneratedCallScope_TransfersMultiMiBValuesOutsideJsonAndEnforcesDigestAndLifetime()
    {
        using var state = new RegressionTestRpcState();
        var provider = new EchoProvider();
        var registration = new McpWorker::Sunder.Package.Agent.Mcp.Rpc.ManagementService(provider).CreateSunderRpcProviderRegistration();
        state.AddGenerated(
            "sunder.package.agent.mcp",
            provider,
            registration,
            NoOpDisposable.Instance,
            registration.ProviderId);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent.mcp");
        var scope = await rpc.CreateCallScopeAsync();
        var catalog = await scope.DiscoverAsync(registration.ContractId);
        var endpoint = Assert.Single(catalog.Providers).Endpoint;
        var document = new string('x', (1024 * 1024) + 32 * 1024);
        var command = new McpCommand(
            McpCommandKind.ImportDocument,
            Document: document,
            FileName: "large.json");

        var request = new ManagementContent
        {
            Content = await McpManagementContent.RegisterAsync(command, "mcp-command.json", scope.DeadlineUtc,
                (stream, options, token) => scope.RegisterContentAsync(endpoint, stream, options, token), CancellationToken.None),
        };
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(command, ContentJson);

        Assert.True(request.Content.Length > 1024 * 1024);
        Assert.Equal(requestBytes.LongLength, request.Content.Length);
        Assert.Equal(Digest(requestBytes), request.Content.Sha256);
        Assert.Equal(SunderRpcContentRepeatability.SingleUse, request.Content.Repeatability);
        Assert.InRange(request.Content.ExpiresAtUtc, DateTimeOffset.UtcNow, scope.DeadlineUtc);

        var envelopeOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new SunderRpcJsonConverterFactory() },
        };
        var envelope = JsonSerializer.SerializeToUtf8Bytes(request, envelopeOptions);
        Assert.True(envelope.Length < 2048);
        Assert.DoesNotContain(document[..256], Encoding.UTF8.GetString(envelope), StringComparison.Ordinal);

        var response = await new ManagementClient(scope, endpoint).CommandAsync(request);
        Assert.True(response.Content.Length > 1024 * 1024);
        Assert.Equal(SunderRpcContentRepeatability.SingleUse, response.Content.Repeatability);
        Assert.InRange(response.Content.ExpiresAtUtc, DateTimeOffset.UtcNow, scope.DeadlineUtc);

        var projection = await McpManagementContent.ReadResponseAsync<McpProjection>(
            scope,
            response.Content,
            CancellationToken.None);
        Assert.Equal(document, projection.Document);
        Assert.Equal(Digest(JsonSerializer.SerializeToUtf8Bytes(projection, ContentJson)), response.Content.Sha256);
        await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await McpManagementContent.ReadResponseAsync<McpProjection>(
                scope,
                response.Content,
                CancellationToken.None));

        var unused = await McpManagementContent.RegisterAsync(command, "unused-command.json", scope.DeadlineUtc,
            (stream, options, token) => scope.RegisterContentAsync(endpoint, stream, options, token), CancellationToken.None);
        await scope.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await scope.OpenContentAsync(unused));
    }

    private static string Digest(byte[] value)
        => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private sealed class EchoProvider : IMcpOwnerManagementClient
    {
        public ValueTask<McpProjection> QueryAsync(
            McpQuery request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new McpProjection());

        public ValueTask<McpProjection> CommandAsync(
            McpCommand request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new McpProjection(Document: request.Document));
        }

        public async IAsyncEnumerable<McpChanged> WatchChangesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class NoOpDisposable : IDisposable
    {
        internal static NoOpDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
