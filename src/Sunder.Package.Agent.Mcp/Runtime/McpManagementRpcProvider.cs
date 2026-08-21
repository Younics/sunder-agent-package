using System.Runtime.CompilerServices;
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
    public async ValueTask<ManagementContent> QueryAsync(
        SunderRpcInvocationContext context,
        ManagementContent request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = await McpManagementPayload.ReadRequestAsync<McpQuery>(
                context,
                request,
                cancellationToken);
            var result = await operations.HandleAsync(query, cancellationToken);
            return await McpManagementPayload.RegisterResponseAsync(
                context,
                result,
                "mcp-projection.json",
                cancellationToken);
        }
        catch (Exception exception) when (ShouldMap(exception))
        {
            throw Failure((McpManagementOperationException)exception);
        }
    }

    public async ValueTask<ManagementContent> CommandAsync(
        SunderRpcInvocationContext context,
        ManagementContent request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var command = await McpManagementPayload.ReadRequestAsync<McpCommand>(
                context,
                request,
                cancellationToken);
            var result = await operations.HandleAsync(command, cancellationToken);
            return await McpManagementPayload.RegisterResponseAsync(
                context,
                result,
                "mcp-projection.json",
                cancellationToken);
        }
        catch (Exception exception) when (ShouldMap(exception))
        {
            throw Failure((McpManagementOperationException)exception);
        }
    }

    public async IAsyncEnumerable<ManagementChange> ChangesAsync(
        SunderRpcInvocationContext context,
        ChangeSubscription request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var change in changes.WatchAsync(cancellationToken))
        {
            yield return new ManagementChange
            {
                Kind = change.Kind == McpChangeKind.Servers
                    ? ManagementChangeKind.Servers
                    : ManagementChangeKind.Status,
            };
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
        var invalidations = new McpChangeQueue();
        void Invalidate() => invalidations.Publish();
        invalidations.Publish();
        editor.ServersChanged += Invalidate;
        connections.StatusChanged += Invalidate;
        try
        {
            await foreach (var change in invalidations.ReadAllAsync(cancellationToken))
            {
                yield return change;
            }
        }
        finally
        {
            editor.ServersChanged -= Invalidate;
            connections.StatusChanged -= Invalidate;
            invalidations.Complete();
        }
    }
}

internal sealed class McpChangeQueue
{
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
    });

    public void Publish() => _signals.Writer.TryWrite(true);

    public async IAsyncEnumerable<McpChanged> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var _ in _signals.Reader.ReadAllAsync(cancellationToken))
        {
            yield return new McpChanged(McpChangeKind.Servers);
            yield return new McpChanged(McpChangeKind.Status);
        }
    }

    public void Complete() => _signals.Writer.TryComplete();
}
