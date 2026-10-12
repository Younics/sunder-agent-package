extern alias McpWorker;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using McpWorker::Sunder.Package.Agent.Mcp.Runtime;
using IManagementService = McpWorker::Sunder.Generated.Packages.Sunder_Package_Agent_Mcp_Services.IManagementService;
using Sunder.Sdk.Services;
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
    public async Task GeneratedService_TransfersMultiMiBValuesThroughContentAndEnforcesDigestAndLifetime()
    {
        var provider = new EchoProvider();
        var document = new string('x', (1024 * 1024) + 32 * 1024);
        var requestBytes = Encoding.UTF8.GetBytes(document);
        await using var request = SunderContent.FromBytes(requestBytes, "application/json", "large.json");

        Assert.True(request.Length > 1024 * 1024);
        Assert.Equal(requestBytes.LongLength, request.Length);
        Assert.Equal(Digest(requestBytes), request.Sha256);
        Assert.True(request.IsRepeatable);
        Assert.Null(request.ExpiresAtUtc);

        var response = await provider.ImportDocumentAsync(request);
        Assert.True(response.Length > 1024 * 1024);
        Assert.True(response.IsRepeatable);
        Assert.Null(response.ExpiresAtUtc);

        var projection = await McpManagementContent.ReadResponseAsync<McpProjection>(
            ValueTask.FromResult(response),
            CancellationToken.None);
        Assert.Equal(document, projection.Document);
        Assert.Equal(Digest(JsonSerializer.SerializeToUtf8Bytes(projection, ContentJson)), response.Sha256);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await McpManagementContent.ReadResponseAsync<McpProjection>(
                ValueTask.FromResult(response),
                CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await response.OpenReadAsync());
    }

    private static string Digest(byte[] value)
        => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private sealed class EchoProvider : IManagementService
    {
        public ValueTask<SunderContent> CatalogAsync(
            CancellationToken cancellationToken = default)
            => Unavailable();

        public ValueTask<SunderContent> DocumentAsync(
            string serverId,
            CancellationToken cancellationToken = default)
            => Unavailable();

        public ValueTask<SunderContent> PresentationAsync(
            string serverId,
            CancellationToken cancellationToken = default)
            => Unavailable();

        public ValueTask<SunderContent> InitializeAsync(
            CancellationToken cancellationToken = default)
            => Unavailable();

        public ValueTask<SunderContent> SaveAsync(
            SunderContent configurationDocument,
            CancellationToken cancellationToken = default)
            => Unavailable();

        public ValueTask<SunderContent> DeleteAsync(
            string serverId,
            CancellationToken cancellationToken = default)
            => Unavailable();

        public ValueTask<SunderContent> ImportCommonAsync(
            CancellationToken cancellationToken = default)
            => Unavailable();

        public async ValueTask<SunderContent> ImportDocumentAsync(
            SunderContent document,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return McpManagementContent.Create(
                new McpProjection(Document: await McpManagementContent.ReadDocumentAsync(document, cancellationToken)),
                "mcp-projection.json");
        }

        public ValueTask<SunderContent> DiscoverAsync(
            string serverId,
            bool reconnect,
            CancellationToken cancellationToken = default)
            => Unavailable();

        public ValueTask<SunderContent> DisconnectAsync(
            string serverId,
            CancellationToken cancellationToken = default)
            => Unavailable();

        public ValueTask<SunderContent> ClearAuthorizationAsync(
            string serverId,
            CancellationToken cancellationToken = default)
            => Unavailable();

        public async IAsyncEnumerable<McpChanged> ChangesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        private static ValueTask<SunderContent> Unavailable()
            => ValueTask.FromException<SunderContent>(new NotSupportedException());
    }
}
