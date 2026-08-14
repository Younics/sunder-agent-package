using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Sunder.Package.Agent.Mcp.Protocol.Generated;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Mcp.Runtime;

internal sealed class McpManagementRpcProvider(
    McpRuntimeHandler operations,
    McpRuntimeChangeStream changes) : IManagementProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public async ValueTask<Payload> QueryAsync(
        SunderRpcInvocationContext context,
        Payload request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = Deserialize<McpQuery>(McpManagementPayload.Join(request.Chunks));
            var result = await operations.HandleAsync(query, cancellationToken);
            return new Payload
            {
                Chunks = McpManagementPayload.Split(JsonSerializer.Serialize(result, JsonOptions)),
            };
        }
        catch (Exception exception) when (ShouldMap(exception))
        {
            throw Failure((McpManagementOperationException)exception);
        }
    }

    public async ValueTask<Payload> CommandAsync(
        SunderRpcInvocationContext context,
        Payload request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var command = Deserialize<McpCommand>(McpManagementPayload.Join(request.Chunks));
            var result = await operations.HandleAsync(command, cancellationToken);
            return new Payload
            {
                Chunks = McpManagementPayload.Split(JsonSerializer.Serialize(result, JsonOptions)),
            };
        }
        catch (Exception exception) when (ShouldMap(exception))
        {
            throw Failure((McpManagementOperationException)exception);
        }
    }

    public async IAsyncEnumerable<Change> ChangesAsync(
        SunderRpcInvocationContext context,
        EmptyRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var change in changes.WatchAsync(cancellationToken))
        {
            yield return new Change
            {
                Kind = change.Kind == McpChangeKind.Servers ? "servers" : "status",
            };
        }
    }

    private static T Deserialize<T>(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload, JsonOptions)
                   ?? throw new McpManagementOperationException(
                       "The MCP management request payload is invalid.");
        }
        catch (JsonException exception)
        {
            throw new McpManagementOperationException(
                "The MCP management request payload is invalid.",
                exception);
        }
    }

    internal static bool ShouldMap(Exception exception)
        => exception is McpManagementOperationException;

    private static SunderRpcException Failure(McpManagementOperationException exception)
        => new(new SunderRpcError(
            SunderRpcErrorKind.Domain,
            "mcp.management.failed",
            exception.Message));
}

internal sealed class McpLocalManagementGateway(
    McpSettingsEditorService editor,
    McpConfigurationCoordinator configuration,
    McpServerConnectionService connections,
    McpOAuthCoordinator oauth) : IMcpManagementGateway
{
    public event Action? ServersChanged
    {
        add => editor.ServersChanged += value;
        remove => editor.ServersChanged -= value;
    }

    public event Action? StatusChanged
    {
        add => connections.StatusChanged += value;
        remove => connections.StatusChanged -= value;
    }

    public IReadOnlyList<McpCatalogDiagnostic> Diagnostics => editor.Diagnostics;
    public Task<IReadOnlyList<ConfiguredMcpServerRecord>> ListAsync(CancellationToken cancellationToken = default) => editor.ListAsync(cancellationToken);
    public Task<string?> LoadDocumentAsync(string serverId, CancellationToken cancellationToken = default) => editor.LoadDocumentAsync(serverId, cancellationToken);
    public string NormalizeName(string? name) => editor.NormalizeName(name);
    public ParsedMcpServerConfiguration Parse(string serverId, string name, string document, ConfiguredMcpServerRecord? existing) => editor.Parse(serverId, name, document, existing);
    public string Format(string serverId, string name, string document, ConfiguredMcpServerRecord? existing) => editor.Format(serverId, name, document, existing);
    public Task<string> SaveAsync(ParsedMcpServerConfiguration parsed, CancellationToken cancellationToken = default) => editor.SaveAsync(parsed, cancellationToken);
    public Task DeleteAsync(string serverId, CancellationToken cancellationToken = default) => editor.DeleteAsync(serverId, cancellationToken);
    public Task<McpConfigurationImportResult> InitializeAsync(CancellationToken cancellationToken = default) => configuration.InitializeAsync(cancellationToken);
    public Task<McpConfigurationImportResult> ImportCommonAsync(CancellationToken cancellationToken = default) => configuration.ImportCommonAsync(cancellationToken);
    public Task<McpConfigurationImportResult> ImportFileAsync(string filePath, CancellationToken cancellationToken = default) => configuration.ImportFileAsync(filePath, cancellationToken);

    public async Task<IReadOnlyList<string>> DiscoverAsync(
        ConfiguredMcpServerRecord server,
        bool reconnect,
        CancellationToken cancellationToken)
        => (await connections.DiscoverAsync(server, reconnect, cancellationToken))
            .Select(tool => tool.Name)
            .ToArray();

    public Task DisconnectAsync(string serverId, CancellationToken cancellationToken = default)
        => connections.DisconnectAsync(serverId);

    public Task<McpConnectionPresentation> GetPresentationAsync(
        ConfiguredMcpServerRecord server,
        CancellationToken cancellationToken = default)
        => connections.GetPresentationAsync(server, cancellationToken);

    public Task AuthorizeAsync(ConfiguredMcpServerRecord server, CancellationToken cancellationToken)
        => Task.FromException(new NotSupportedException(
            "Interactive MCP OAuth is available only through the App callback client."));

    public Task ClearAuthorizationAsync(
        ConfiguredMcpServerRecord server,
        CancellationToken cancellationToken)
        => oauth.ClearAsync(server, cancellationToken);
}

internal sealed class McpRuntimeHandler(
    McpServerCatalogService catalog,
    McpSettingsEditorService editor,
    McpConfigurationCoordinator configuration,
    McpServerConnectionService connections,
    McpOAuthCoordinator oauth,
    IPackageContext context)
{
    public async ValueTask<McpProjection> HandleAsync(
        McpQuery request,
        CancellationToken cancellationToken = default)
    {
        var server = request.Kind is McpQueryKind.Document or McpQueryKind.Presentation
            ? await GetServerAsync(request.ServerId, cancellationToken)
            : null;
        return request.Kind switch
        {
            McpQueryKind.Catalog => await CatalogAsync(cancellationToken),
            McpQueryKind.Document => new McpProjection(
                Document: await editor.LoadDocumentAsync(server!.ServerId, cancellationToken)),
            McpQueryKind.Presentation => new McpProjection(
                Presentation: await connections.GetPresentationAsync(server!, cancellationToken)),
            _ => throw new McpManagementOperationException("Unknown MCP query."),
        };
    }

    public async ValueTask<McpProjection> HandleAsync(
        McpCommand request,
        CancellationToken cancellationToken = default)
    {
        switch (request.Kind)
        {
            case McpCommandKind.Initialize:
                return new McpProjection(
                    ImportResult: await configuration.InitializeAsync(cancellationToken));
            case McpCommandKind.Save:
                var configurationToSave = request.Configuration
                    ?? throw new McpManagementOperationException("MCP configuration is required.");
                var editorDocument = await editor.SaveAsync(configurationToSave, cancellationToken);
                await connections.DisconnectAsync(configurationToSave.Server.ServerId);
                return (await CatalogAsync(cancellationToken)) with { Document = editorDocument };
            case McpCommandKind.Delete:
                await editor.DeleteAsync(Require(request.ServerId), cancellationToken);
                await connections.DisconnectAsync(Require(request.ServerId));
                return await CatalogAsync(cancellationToken);
            case McpCommandKind.ImportCommon:
                return new McpProjection(
                    ImportResult: await configuration.ImportCommonAsync(cancellationToken));
            case McpCommandKind.ImportDocument:
                return new McpProjection(
                    ImportResult: await ImportDocumentAsync(request, cancellationToken));
        }

        var server = await GetServerAsync(request.ServerId, cancellationToken);
        switch (request.Kind)
        {
            case McpCommandKind.Discover:
            case McpCommandKind.Reconnect:
                var tools = await connections.DiscoverAsync(
                    server,
                    request.Kind == McpCommandKind.Reconnect,
                    cancellationToken);
                return new McpProjection(ToolNames: tools.Select(tool => tool.Name).ToArray());
            case McpCommandKind.Disconnect:
                await connections.DisconnectAsync(server.ServerId);
                break;
            case McpCommandKind.ClearAuthorization:
                await oauth.ClearAsync(server, cancellationToken);
                break;
            default:
                throw new McpManagementOperationException("Unknown MCP command.");
        }

        return new McpProjection(
            Presentation: await connections.GetPresentationAsync(server, cancellationToken));
    }

    private async Task<McpProjection> CatalogAsync(CancellationToken cancellationToken)
    {
        var servers = await editor.ListAsync(cancellationToken);
        return new McpProjection(Servers: servers, Diagnostics: editor.Diagnostics);
    }

    private async Task<McpConfigurationImportResult> ImportDocumentAsync(
        McpCommand request,
        CancellationToken cancellationToken)
    {
        if (request.Document is null || request.Document.Length > 1024 * 1024)
        {
            throw new McpManagementOperationException(
                "The MCP configuration document exceeds the 1 MB import limit.");
        }

        var safeName = Path.GetFileName(request.FileName ?? "mcp-import.json");
        var path = context.Storage.RoleLocalWorkspace.GetLocalPath(
            $"mcp-import/{Guid.NewGuid():N}/{safeName}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await File.WriteAllTextAsync(path, request.Document, cancellationToken);
            return await configuration.ImportFileAsync(path, cancellationToken);
        }
        finally
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
            }
            catch
            {
            }
        }
    }

    private async Task<ConfiguredMcpServerRecord> GetServerAsync(
        string? serverId,
        CancellationToken cancellationToken)
        => await catalog.GetServerAsync(Require(serverId), cancellationToken)
           ?? throw new McpManagementOperationException("The selected MCP server was not found.");

    private static string Require(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? throw new McpManagementOperationException("An MCP server id is required.")
            : value;
}

internal sealed class McpRuntimeChangeStream(
    McpSettingsEditorService editor,
    McpServerConnectionService connections)
{
    public async IAsyncEnumerable<McpChanged> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<McpChanged>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        void Servers() => channel.Writer.TryWrite(new McpChanged(McpChangeKind.Servers));
        void Status() => channel.Writer.TryWrite(new McpChanged(McpChangeKind.Status));
        editor.ServersChanged += Servers;
        connections.StatusChanged += Status;
        try
        {
            await foreach (var change in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return change;
            }
        }
        finally
        {
            editor.ServersChanged -= Servers;
            connections.StatusChanged -= Status;
            channel.Writer.TryComplete();
        }
    }
}
