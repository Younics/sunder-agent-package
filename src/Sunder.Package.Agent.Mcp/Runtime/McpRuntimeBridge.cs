using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Mcp.Runtime;

internal interface IMcpManagementGateway
{
    event Action? ServersChanged;
    event Action? StatusChanged;
    IReadOnlyList<McpCatalogDiagnostic> Diagnostics { get; }
    Task<IReadOnlyList<ConfiguredMcpServerRecord>> ListAsync(CancellationToken cancellationToken = default);
    Task<string?> LoadDocumentAsync(string serverId, CancellationToken cancellationToken = default);
    string NormalizeName(string? name);
    ParsedMcpServerConfiguration Parse(string serverId, string name, string document, ConfiguredMcpServerRecord? existing);
    string Format(string serverId, string name, string document, ConfiguredMcpServerRecord? existing);
    Task SaveAsync(ParsedMcpServerConfiguration parsed, CancellationToken cancellationToken = default);
    Task DeleteAsync(string serverId, CancellationToken cancellationToken = default);
    Task<McpConfigurationImportResult> InitializeAsync(CancellationToken cancellationToken = default);
    Task<McpConfigurationImportResult> ImportCommonAsync(CancellationToken cancellationToken = default);
    Task<McpConfigurationImportResult> ImportFileAsync(string filePath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> DiscoverAsync(ConfiguredMcpServerRecord server, bool reconnect, CancellationToken cancellationToken);
    Task DisconnectAsync(string serverId, CancellationToken cancellationToken = default);
    Task<McpConnectionPresentation> GetPresentationAsync(ConfiguredMcpServerRecord server, CancellationToken cancellationToken = default);
    Task AuthorizeAsync(ConfiguredMcpServerRecord server, CancellationToken cancellationToken);
    Task ClearAuthorizationAsync(ConfiguredMcpServerRecord server, CancellationToken cancellationToken);
}

internal static class McpRuntimeOperations
{
    internal static readonly PackageRuntimeOperation<McpQuery, McpProjection> Query = new("mcp.query.v1");
    internal static readonly PackageRuntimeOperation<McpCommand, McpProjection> Command = new("mcp.command.v1");
    internal static readonly PackageRuntimeStream<McpChangeSubscription, McpChanged> Changes = new("mcp.changes.v1");
}

internal enum McpQueryKind { Catalog, Document, Presentation }
internal sealed record McpQuery(McpQueryKind Kind, string? ServerId = null);
internal enum McpCommandKind { Initialize, Save, Delete, ImportCommon, ImportDocument, Discover, Reconnect, Disconnect, ClearAuthorization }
internal sealed record McpCommand(
    McpCommandKind Kind,
    string? ServerId = null,
    ParsedMcpServerConfiguration? Configuration = null,
    string? Document = null,
    string? FileName = null);
internal sealed record McpProjection(
    IReadOnlyList<ConfiguredMcpServerRecord>? Servers = null,
    IReadOnlyList<McpCatalogDiagnostic>? Diagnostics = null,
    string? Document = null,
    McpConnectionPresentation? Presentation = null,
    McpConfigurationImportResult? ImportResult = null,
    IReadOnlyList<string>? ToolNames = null);
internal sealed record McpChangeSubscription;
internal enum McpChangeKind { Servers, Status }
internal sealed record McpChanged(McpChangeKind Kind);

internal sealed class McpLocalManagementGateway(
    McpSettingsEditorService editor,
    McpConfigurationCoordinator configuration,
    McpServerConnectionService connections,
    McpOAuthCoordinator oauth) : IMcpManagementGateway
{
    public event Action? ServersChanged { add => editor.ServersChanged += value; remove => editor.ServersChanged -= value; }
    public event Action? StatusChanged { add => connections.StatusChanged += value; remove => connections.StatusChanged -= value; }
    public IReadOnlyList<McpCatalogDiagnostic> Diagnostics => editor.Diagnostics;
    public Task<IReadOnlyList<ConfiguredMcpServerRecord>> ListAsync(CancellationToken cancellationToken = default) => editor.ListAsync(cancellationToken);
    public Task<string?> LoadDocumentAsync(string serverId, CancellationToken cancellationToken = default) => editor.LoadDocumentAsync(serverId, cancellationToken);
    public string NormalizeName(string? name) => editor.NormalizeName(name);
    public ParsedMcpServerConfiguration Parse(string serverId, string name, string document, ConfiguredMcpServerRecord? existing) => editor.Parse(serverId, name, document, existing);
    public string Format(string serverId, string name, string document, ConfiguredMcpServerRecord? existing) => editor.Format(serverId, name, document, existing);
    public Task SaveAsync(ParsedMcpServerConfiguration parsed, CancellationToken cancellationToken = default) => editor.SaveAsync(parsed, cancellationToken);
    public Task DeleteAsync(string serverId, CancellationToken cancellationToken = default) => editor.DeleteAsync(serverId, cancellationToken);
    public Task<McpConfigurationImportResult> InitializeAsync(CancellationToken cancellationToken = default) => configuration.InitializeAsync(cancellationToken);
    public Task<McpConfigurationImportResult> ImportCommonAsync(CancellationToken cancellationToken = default) => configuration.ImportCommonAsync(cancellationToken);
    public Task<McpConfigurationImportResult> ImportFileAsync(string filePath, CancellationToken cancellationToken = default) => configuration.ImportFileAsync(filePath, cancellationToken);
    public async Task<IReadOnlyList<string>> DiscoverAsync(ConfiguredMcpServerRecord server, bool reconnect, CancellationToken cancellationToken)
        => (await connections.DiscoverAsync(server, reconnect, cancellationToken)).Select(tool => tool.Name).ToArray();
    public Task DisconnectAsync(string serverId, CancellationToken cancellationToken = default) => connections.DisconnectAsync(serverId);
    public Task<McpConnectionPresentation> GetPresentationAsync(ConfiguredMcpServerRecord server, CancellationToken cancellationToken = default) => connections.GetPresentationAsync(server, cancellationToken);
    public Task AuthorizeAsync(ConfiguredMcpServerRecord server, CancellationToken cancellationToken)
        => Task.FromException(new NotSupportedException("Interactive MCP OAuth is available only through the App callback client."));
    public Task ClearAuthorizationAsync(ConfiguredMcpServerRecord server, CancellationToken cancellationToken) => oauth.ClearAsync(server, cancellationToken);
}

internal sealed class McpAppRuntimeGateway : IMcpManagementGateway, IDisposable
{
    private const int MaxImportBytes = 1024 * 1024;
    private readonly IPackageRuntimeClient _client;
    private readonly PackageCallbackFlowRunner _callbackFlow;
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<McpCatalogDiagnostic> _diagnostics = [];
    private int _disposed;

    public McpAppRuntimeGateway(IPackageRuntimeClient client, IPackageCallbackClient callbacks)
    {
        _client = client;
        _callbackFlow = new PackageCallbackFlowRunner(callbacks);
        _ = ObserveChangesAsync(_lifetime.Token);
    }

    public event Action? ServersChanged;
    public event Action? StatusChanged;
    public IReadOnlyList<McpCatalogDiagnostic> Diagnostics => _diagnostics;

    public async Task<IReadOnlyList<ConfiguredMcpServerRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = await QueryAsync(new(McpQueryKind.Catalog), cancellationToken);
        _diagnostics = result.Diagnostics ?? [];
        return result.Servers ?? [];
    }

    public async Task<string?> LoadDocumentAsync(string serverId, CancellationToken cancellationToken = default)
        => (await QueryAsync(new(McpQueryKind.Document, serverId), cancellationToken)).Document;
    public string NormalizeName(string? name) => McpServerCatalogService.NormalizeName(name);
    public ParsedMcpServerConfiguration Parse(string serverId, string name, string document, ConfiguredMcpServerRecord? existing)
        => McpConfigurationDocument.Parse(serverId, NormalizeName(name), document, existing);
    public string Format(string serverId, string name, string document, ConfiguredMcpServerRecord? existing)
    {
        var parsed = Parse(serverId, name, document, existing);
        return McpConfigurationDocument.BuildEditorText(parsed.Server, parsed.Headers, parsed.EnvironmentVariables);
    }
    public async Task SaveAsync(ParsedMcpServerConfiguration parsed, CancellationToken cancellationToken = default)
        => _ = await CommandAsync(new(McpCommandKind.Save, Configuration: parsed), cancellationToken);
    public async Task DeleteAsync(string serverId, CancellationToken cancellationToken = default)
        => _ = await CommandAsync(new(McpCommandKind.Delete, serverId), cancellationToken);
    public async Task<McpConfigurationImportResult> InitializeAsync(CancellationToken cancellationToken = default)
        => (await CommandAsync(new(McpCommandKind.Initialize), cancellationToken)).ImportResult!;
    public async Task<McpConfigurationImportResult> ImportCommonAsync(CancellationToken cancellationToken = default)
        => (await CommandAsync(new(McpCommandKind.ImportCommon), cancellationToken)).ImportResult!;
    public async Task<McpConfigurationImportResult> ImportFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists) throw new InvalidOperationException("Select an existing MCP configuration file.");
        if (info.Length > MaxImportBytes) throw new InvalidOperationException("The MCP configuration file exceeds the 1 MB import limit.");
        var document = await File.ReadAllTextAsync(filePath, cancellationToken);
        return (await CommandAsync(new(McpCommandKind.ImportDocument, Document: document, FileName: info.Name), cancellationToken)).ImportResult!;
    }
    public async Task<IReadOnlyList<string>> DiscoverAsync(ConfiguredMcpServerRecord server, bool reconnect, CancellationToken cancellationToken)
        => (await CommandAsync(new(reconnect ? McpCommandKind.Reconnect : McpCommandKind.Discover, server.ServerId), cancellationToken)).ToolNames ?? [];
    public async Task DisconnectAsync(string serverId, CancellationToken cancellationToken = default)
        => _ = await CommandAsync(new(McpCommandKind.Disconnect, serverId), cancellationToken);
    public async Task<McpConnectionPresentation> GetPresentationAsync(ConfiguredMcpServerRecord server, CancellationToken cancellationToken = default)
        => (await QueryAsync(new(McpQueryKind.Presentation, server.ServerId), cancellationToken)).Presentation!;
    public async Task AuthorizeAsync(ConfiguredMcpServerRecord server, CancellationToken cancellationToken)
    {
        await _callbackFlow.RunAsync(
            McpOAuthCallbackHandler.HandlerId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [McpOAuthCallbackHandler.ServerIdParameter] = server.ServerId,
            },
            "MCP OAuth authorization",
            cancellationToken).ConfigureAwait(false);
        StatusChanged?.Invoke();
    }
    public async Task ClearAuthorizationAsync(ConfiguredMcpServerRecord server, CancellationToken cancellationToken)
        => _ = await CommandAsync(new(McpCommandKind.ClearAuthorization, server.ServerId), cancellationToken);

    private async Task<McpProjection> QueryAsync(McpQuery query, CancellationToken cancellationToken)
        => await _client.InvokeAsync(McpRuntimeOperations.Query, query, cancellationToken).ConfigureAwait(false);
    private async Task<McpProjection> CommandAsync(McpCommand command, CancellationToken cancellationToken)
        => await _client.InvokeAsync(McpRuntimeOperations.Command, command, cancellationToken).ConfigureAwait(false);
    private async Task ObserveChangesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var change in _client.SubscribeAsync(McpRuntimeOperations.Changes, new McpChangeSubscription(), cancellationToken))
                {
                    if (change.Kind == McpChangeKind.Servers) ServersChanged?.Invoke(); else StatusChanged?.Invoke();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch { }
            try { await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
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

internal sealed class McpRuntimeHandler(
    McpServerCatalogService catalog,
    McpSettingsEditorService editor,
    McpConfigurationCoordinator configuration,
    McpServerConnectionService connections,
    McpOAuthCoordinator oauth,
    IPackageContext context)
    : IPackageRuntimeOperationHandler<McpQuery, McpProjection>,
      IPackageRuntimeOperationHandler<McpCommand, McpProjection>
{
    public async ValueTask<McpProjection> HandleAsync(McpQuery request, CancellationToken cancellationToken = default)
    {
        var server = request.Kind is McpQueryKind.Document or McpQueryKind.Presentation
            ? await GetServerAsync(request.ServerId, cancellationToken) : null;
        return request.Kind switch
        {
            McpQueryKind.Catalog => await CatalogAsync(cancellationToken),
            McpQueryKind.Document => new(Document: await editor.LoadDocumentAsync(server!.ServerId, cancellationToken)),
            McpQueryKind.Presentation => new(Presentation: await connections.GetPresentationAsync(server!, cancellationToken)),
            _ => throw new InvalidOperationException("Unknown MCP query."),
        };
    }

    public async ValueTask<McpProjection> HandleAsync(McpCommand request, CancellationToken cancellationToken = default)
    {
        switch (request.Kind)
        {
            case McpCommandKind.Initialize:
                return new(ImportResult: await configuration.InitializeAsync(cancellationToken));
            case McpCommandKind.Save:
                await editor.SaveAsync(request.Configuration ?? throw new InvalidOperationException("MCP configuration is required."), cancellationToken);
                await connections.DisconnectAsync(request.Configuration.Server.ServerId);
                return await CatalogAsync(cancellationToken);
            case McpCommandKind.Delete:
                await editor.DeleteAsync(Require(request.ServerId), cancellationToken);
                await connections.DisconnectAsync(Require(request.ServerId));
                return await CatalogAsync(cancellationToken);
            case McpCommandKind.ImportCommon:
                return new(ImportResult: await configuration.ImportCommonAsync(cancellationToken));
            case McpCommandKind.ImportDocument:
                return new(ImportResult: await ImportDocumentAsync(request, cancellationToken));
        }

        var server = await GetServerAsync(request.ServerId, cancellationToken);
        switch (request.Kind)
        {
            case McpCommandKind.Discover:
            case McpCommandKind.Reconnect:
                var tools = await connections.DiscoverAsync(server, request.Kind == McpCommandKind.Reconnect, cancellationToken);
                return new(ToolNames: tools.Select(tool => tool.Name).ToArray());
            case McpCommandKind.Disconnect:
                await connections.DisconnectAsync(server.ServerId);
                break;
            case McpCommandKind.ClearAuthorization:
                await oauth.ClearAsync(server, cancellationToken);
                break;
            default:
                throw new InvalidOperationException("Unknown MCP command.");
        }
        return new(Presentation: await connections.GetPresentationAsync(server, cancellationToken));
    }

    private async Task<McpProjection> CatalogAsync(CancellationToken cancellationToken)
    {
        var servers = await editor.ListAsync(cancellationToken);
        return new(Servers: servers, Diagnostics: editor.Diagnostics);
    }

    private async Task<McpConfigurationImportResult> ImportDocumentAsync(McpCommand request, CancellationToken cancellationToken)
    {
        if (request.Document is null || request.Document.Length > 1024 * 1024)
            throw new InvalidOperationException("The MCP configuration document exceeds the 1 MB import limit.");
        var safeName = Path.GetFileName(request.FileName ?? "mcp-import.json");
        var path = context.Storage.RoleLocalWorkspace.GetLocalPath($"mcp-import/{Guid.NewGuid():N}/{safeName}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await File.WriteAllTextAsync(path, request.Document, cancellationToken);
            return await configuration.ImportFileAsync(path, cancellationToken);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { }
        }
    }

    private async Task<ConfiguredMcpServerRecord> GetServerAsync(string? serverId, CancellationToken cancellationToken)
        => await catalog.GetServerAsync(Require(serverId), cancellationToken)
           ?? throw new InvalidOperationException("The selected MCP server was not found.");
    private static string Require(string? value) => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException("An MCP server id is required.") : value;
}

internal sealed class McpRuntimeChangeStream(McpSettingsEditorService editor, McpServerConnectionService connections)
    : IPackageRuntimeStreamHandler<McpChangeSubscription, McpChanged>
{
    public async IAsyncEnumerable<McpChanged> SubscribeAsync(McpChangeSubscription request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<McpChanged>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        void Servers() => channel.Writer.TryWrite(new(McpChangeKind.Servers));
        void Status() => channel.Writer.TryWrite(new(McpChangeKind.Status));
        editor.ServersChanged += Servers;
        connections.StatusChanged += Status;
        try { await foreach (var change in channel.Reader.ReadAllAsync(cancellationToken)) yield return change; }
        finally { editor.ServersChanged -= Servers; connections.StatusChanged -= Status; channel.Writer.TryComplete(); }
    }
}
