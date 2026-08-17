using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Agent.Mcp.Protocol.Generated;
using Sunder.Package.Agent.Mcp.Runtime;
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
        state.Add(
            "sunder.package.agent.mcp",
            McpManagementProtocol.Descriptor,
            provider,
            new SunderPackageAgentMcpManagementRpcHandler(provider),
            NoOpDisposable.Instance,
            McpManagementProtocol.ProviderId);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent.mcp");
        var scope = await rpc.CreateCallScopeAsync();
        var catalog = await scope.DiscoverAsync(McpManagementProtocol.Descriptor.ContractId);
        var endpoint = Assert.Single(catalog.Providers).Endpoint;
        var document = new string('x', (1024 * 1024) + 32 * 1024);
        var command = new McpCommand(
            McpCommandKind.ImportDocument,
            Document: document,
            FileName: "large.json");

        var request = await McpManagementPayload.RegisterRequestAsync(
            scope,
            endpoint,
            command,
            "mcp-command.json",
            CancellationToken.None);
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(command, ContentJson);

        Assert.True(request.Content.Length > 1024 * 1024);
        Assert.Equal(requestBytes.LongLength, request.Content.Length);
        Assert.Equal(Digest(requestBytes), request.Content.Sha256);
        Assert.Equal(SunderRpcContentRepeatability.SingleUse, request.Content.Repeatability);
        Assert.InRange(request.Content.ExpiresAtUtc, DateTimeOffset.UtcNow, scope.DeadlineUtc);

        var envelopeOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new SunderRpcV2JsonConverterFactory() },
        };
        var envelope = JsonSerializer.SerializeToUtf8Bytes(request, envelopeOptions);
        Assert.True(envelope.Length < 2048);
        Assert.DoesNotContain(document[..256], Encoding.UTF8.GetString(envelope), StringComparison.Ordinal);

        var response = await new ManagementClient(scope, endpoint).CommandAsync(request);
        Assert.True(response.Content.Length > 1024 * 1024);
        Assert.Equal(SunderRpcContentRepeatability.SingleUse, response.Content.Repeatability);
        Assert.InRange(response.Content.ExpiresAtUtc, DateTimeOffset.UtcNow, scope.DeadlineUtc);

        var projection = await McpManagementPayload.ReadResponseAsync<McpProjection>(
            scope,
            response,
            CancellationToken.None);
        Assert.Equal(document, projection.Document);
        Assert.Equal(Digest(JsonSerializer.SerializeToUtf8Bytes(projection, ContentJson)), response.Content.Sha256);
        await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await McpManagementPayload.ReadResponseAsync<McpProjection>(
                scope,
                response,
                CancellationToken.None));

        var unused = await McpManagementPayload.RegisterRequestAsync(
            scope,
            endpoint,
            command,
            "unused-command.json",
            CancellationToken.None);
        await scope.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await scope.OpenContentAsync(unused.Content));
    }

    private static string Digest(byte[] value)
        => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private sealed class EchoProvider : IManagementProvider
    {
        public ValueTask<ManagementContent> QueryAsync(
            SunderRpcInvocationContext context,
            ManagementContent request,
            CancellationToken cancellationToken = default)
            => CommandAsync(context, request, cancellationToken);

        public async ValueTask<ManagementContent> CommandAsync(
            SunderRpcInvocationContext context,
            ManagementContent request,
            CancellationToken cancellationToken = default)
        {
            var command = await McpManagementPayload.ReadRequestAsync<McpCommand>(
                context,
                request,
                cancellationToken);
            return await McpManagementPayload.RegisterResponseAsync(
                context,
                new McpProjection(Document: command.Document),
                "mcp-projection.json",
                cancellationToken);
        }

        public async IAsyncEnumerable<ManagementChange> ChangesAsync(
            SunderRpcInvocationContext context,
            ChangeSubscription request,
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
