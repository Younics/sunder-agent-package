using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Agent.Mcp.Protocol.Generated;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Mcp.Runtime;

internal sealed class McpOwnerManagementRpcClient(ISunderRpcClient rpc) : IMcpOwnerManagementClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public async ValueTask<McpProjection> QueryAsync(
        McpQuery query,
        CancellationToken cancellationToken = default)
    {
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: cancellationToken);
        var client = new ManagementClient(scope, await ResolveProviderAsync(scope, cancellationToken));
        var response = await client.QueryAsync(
            new Payload { Chunks = McpManagementPayload.Split(JsonSerializer.Serialize(query, JsonOptions)) },
            cancellationToken);
        return Deserialize<McpProjection>(McpManagementPayload.Join(response.Chunks));
    }

    public async ValueTask<McpProjection> CommandAsync(
        McpCommand command,
        CancellationToken cancellationToken = default)
    {
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: cancellationToken);
        var client = new ManagementClient(scope, await ResolveProviderAsync(scope, cancellationToken));
        var response = await client.CommandAsync(
            new Payload { Chunks = McpManagementPayload.Split(JsonSerializer.Serialize(command, JsonOptions)) },
            cancellationToken);
        return Deserialize<McpProjection>(McpManagementPayload.Join(response.Chunks));
    }

    public async IAsyncEnumerable<McpChanged> WatchChangesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: cancellationToken);
        var client = new ManagementClient(scope, await ResolveProviderAsync(scope, cancellationToken));
        await foreach (var change in client.ChangesAsync(
                           new EmptyRequest(),
                           cancellationToken).WithCancellation(cancellationToken))
        {
            yield return new McpChanged(change.Kind switch
            {
                "servers" => McpChangeKind.Servers,
                "status" => McpChangeKind.Status,
                _ => throw new InvalidDataException("The MCP Runtime returned an unknown change kind."),
            });
        }
    }

    private static async ValueTask<SunderRpcEndpointReference> ResolveProviderAsync(
        ISunderRpcClient client,
        CancellationToken cancellationToken)
    {
        var descriptor = McpManagementProtocol.Descriptor;
        var catalog = await client.DiscoverAsync(descriptor.ContractId, cancellationToken);
        var provider = catalog.Providers.SingleOrDefault(candidate =>
            candidate.ProviderId == McpManagementProtocol.ProviderId
            && candidate.State == SunderRpcProviderState.Active
            && candidate.ContractVersion == descriptor.Version
            && candidate.DescriptorSha256 == descriptor.Sha256
            && candidate.AbiSha256 == descriptor.AbiSha256);
        return provider?.Endpoint
            ?? throw new InvalidOperationException("The MCP Runtime management provider is unavailable.");
    }

    private static T Deserialize<T>(string payload)
        => JsonSerializer.Deserialize<T>(payload, JsonOptions)
           ?? throw new InvalidDataException("The MCP Runtime returned an empty management response.");
}

internal sealed class McpAppRuntimeGateway : IMcpManagementGateway, IDisposable
{
    private const int MaxImportBytes = 1024 * 1024;
    private readonly IMcpOwnerManagementClient _client;
    private readonly PackageCallbackFlowRunner _callbackFlow;
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<McpCatalogDiagnostic> _diagnostics = [];
    private int _disposed;

    internal McpAppRuntimeGateway(
        IMcpOwnerManagementClient client,
        IPackageCallbackClient callbacks)
    {
        _client = client;
        _callbackFlow = new PackageCallbackFlowRunner(callbacks);
        _ = ObserveChangesAsync(_lifetime.Token);
    }

    public event Action? ServersChanged;
    public event Action? StatusChanged;
    public IReadOnlyList<McpCatalogDiagnostic> Diagnostics => _diagnostics;

    public async Task<IReadOnlyList<ConfiguredMcpServerRecord>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await QueryAsync(new McpQuery(McpQueryKind.Catalog), cancellationToken);
        _diagnostics = result.Diagnostics ?? [];
        return result.Servers ?? [];
    }

    public async Task<string?> LoadDocumentAsync(
        string serverId,
        CancellationToken cancellationToken = default)
        => (await QueryAsync(new McpQuery(McpQueryKind.Document, serverId), cancellationToken)).Document;

    public string NormalizeName(string? name) => McpServerName.Normalize(name);

    public ParsedMcpServerConfiguration Parse(
        string serverId,
        string name,
        string document,
        ConfiguredMcpServerRecord? existing)
        => McpConfigurationDocument.Parse(serverId, NormalizeName(name), document, existing);

    public string Format(
        string serverId,
        string name,
        string document,
        ConfiguredMcpServerRecord? existing)
    {
        var parsed = Parse(serverId, name, document, existing);
        return McpConfigurationDocument.BuildEditorText(
            parsed.Server,
            parsed.Headers,
            parsed.EnvironmentVariables);
    }

    public async Task<string> SaveAsync(
        ParsedMcpServerConfiguration parsed,
        CancellationToken cancellationToken = default)
        => (await CommandAsync(
                new McpCommand(McpCommandKind.Save, Configuration: parsed),
                cancellationToken)).Document
           ?? throw new InvalidOperationException("The Runtime did not return the saved MCP editor document.");

    public async Task DeleteAsync(string serverId, CancellationToken cancellationToken = default)
        => _ = await CommandAsync(new McpCommand(McpCommandKind.Delete, serverId), cancellationToken);

    public async Task<McpConfigurationImportResult> InitializeAsync(
        CancellationToken cancellationToken = default)
        => (await CommandAsync(new McpCommand(McpCommandKind.Initialize), cancellationToken)).ImportResult!;

    public async Task<McpConfigurationImportResult> ImportCommonAsync(
        CancellationToken cancellationToken = default)
        => (await CommandAsync(new McpCommand(McpCommandKind.ImportCommon), cancellationToken)).ImportResult!;

    public async Task<McpConfigurationImportResult> ImportFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists)
        {
            throw new InvalidOperationException("Select an existing MCP configuration file.");
        }
        if (info.Length > MaxImportBytes)
        {
            throw new InvalidOperationException("The MCP configuration file exceeds the 1 MB import limit.");
        }
        var document = await File.ReadAllTextAsync(filePath, cancellationToken);
        return (await CommandAsync(
            new McpCommand(
                McpCommandKind.ImportDocument,
                Document: document,
                FileName: info.Name),
            cancellationToken)).ImportResult!;
    }

    public async Task<IReadOnlyList<string>> DiscoverAsync(
        ConfiguredMcpServerRecord server,
        bool reconnect,
        CancellationToken cancellationToken)
        => (await CommandAsync(
                new McpCommand(
                    reconnect ? McpCommandKind.Reconnect : McpCommandKind.Discover,
                    server.ServerId),
                cancellationToken)).ToolNames
           ?? [];

    public async Task DisconnectAsync(
        string serverId,
        CancellationToken cancellationToken = default)
        => _ = await CommandAsync(new McpCommand(McpCommandKind.Disconnect, serverId), cancellationToken);

    public async Task<McpConnectionPresentation> GetPresentationAsync(
        ConfiguredMcpServerRecord server,
        CancellationToken cancellationToken = default)
        => (await QueryAsync(
                new McpQuery(McpQueryKind.Presentation, server.ServerId),
                cancellationToken)).Presentation!;

    public async Task AuthorizeAsync(
        ConfiguredMcpServerRecord server,
        CancellationToken cancellationToken)
    {
        await _callbackFlow.RunAsync(
            McpManagementProtocol.OAuthCallbackHandlerId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [McpManagementProtocol.OAuthServerIdParameter] = server.ServerId,
            },
            "MCP OAuth authorization",
            cancellationToken).ConfigureAwait(false);
        StatusChanged?.Invoke();
    }

    public async Task ClearAuthorizationAsync(
        ConfiguredMcpServerRecord server,
        CancellationToken cancellationToken)
        => _ = await CommandAsync(
            new McpCommand(McpCommandKind.ClearAuthorization, server.ServerId),
            cancellationToken);

    private async Task<McpProjection> QueryAsync(McpQuery query, CancellationToken cancellationToken)
        => await _client.QueryAsync(query, cancellationToken).ConfigureAwait(false);

    private async Task<McpProjection> CommandAsync(McpCommand command, CancellationToken cancellationToken)
        => await _client.CommandAsync(command, cancellationToken).ConfigureAwait(false);

    private async Task ObserveChangesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var change in _client.WatchChangesAsync(cancellationToken))
                {
                    if (change.Kind == McpChangeKind.Servers)
                    {
                        ServersChanged?.Invoke();
                    }
                    else
                    {
                        StatusChanged?.Invoke();
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
