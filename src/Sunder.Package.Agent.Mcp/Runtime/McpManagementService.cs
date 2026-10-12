using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Mcp.Runtime;

[SunderService(Key = "management", ContractId = "sunder.package.agent.mcp.management", ServiceId = "management", IncludeSchemaDialect = true)]
[SunderServiceProvider(ProviderId = "mcp.management")]
[SunderServiceError("mcp.management.failed", Description = "The MCP management request failed.")]
internal sealed partial class McpManagementService(
    McpServerCatalogService catalog,
    McpSettingsEditorService editor,
    McpConfigurationCoordinator configuration,
    McpServerConnectionService connections,
    McpOAuthCoordinator oauth,
    IPackageContext context)
{
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["mcp.management.failed"])]
    public ValueTask<SunderContent> CatalogAsync(CancellationToken token = default)
        => ProjectAsync(() => CatalogCoreAsync(token));

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["mcp.management.failed"])]
    public ValueTask<SunderContent> DocumentAsync(string serverId, CancellationToken token = default)
        => ProjectAsync(async () => new McpProjection(Document: await editor.LoadDocumentAsync((await GetServerAsync(serverId, token)).ServerId, token)));

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["mcp.management.failed"])]
    public ValueTask<SunderContent> PresentationAsync(string serverId, CancellationToken token = default)
        => ProjectAsync(async () => new McpProjection(Presentation: await connections.GetPresentationAsync(await GetServerAsync(serverId, token), token)));

    [SunderMethod(Errors = ["mcp.management.failed"])]
    public ValueTask<SunderContent> InitializeAsync(CancellationToken token = default)
        => ProjectAsync(async () => new McpProjection(ImportResult: await configuration.InitializeAsync(token)));

    [SunderMethod(Errors = ["mcp.management.failed"])]
    public ValueTask<SunderContent> SaveAsync(SunderContent configurationDocument, CancellationToken token = default)
        => ProjectAsync(async () =>
        {
            var parsed = await McpManagementContent.ReadRequestAsync<ParsedMcpServerConfiguration>(configurationDocument, token).ConfigureAwait(false);
            var document = await editor.SaveAsync(parsed, token).ConfigureAwait(false);
            await connections.DisconnectAsync(parsed.Server.ServerId).ConfigureAwait(false);
            return (await CatalogCoreAsync(token).ConfigureAwait(false)) with { Document = document };
        });

    [SunderMethod(Errors = ["mcp.management.failed"])]
    public ValueTask<SunderContent> DeleteAsync(string serverId, CancellationToken token = default)
        => ProjectAsync(async () =>
        {
            await editor.DeleteAsync(Require(serverId), token).ConfigureAwait(false);
            await connections.DisconnectAsync(serverId).ConfigureAwait(false);
            return await CatalogCoreAsync(token).ConfigureAwait(false);
        });

    [SunderMethod(Errors = ["mcp.management.failed"])]
    public ValueTask<SunderContent> ImportCommonAsync(CancellationToken token = default)
        => ProjectAsync(async () => new McpProjection(ImportResult: await configuration.ImportCommonAsync(token)));

    [SunderMethod(Errors = ["mcp.management.failed"])]
    public ValueTask<SunderContent> ImportDocumentAsync(SunderContent document, CancellationToken token = default)
        => ProjectAsync(async () =>
        {
            var text = await McpManagementContent.ReadDocumentAsync(document, token).ConfigureAwait(false);
            if (text.Length > 1024 * 1024)
                throw new McpManagementOperationException("The MCP configuration document exceeds the 1 MB import limit.");
            var safeName = Path.GetFileName(string.IsNullOrWhiteSpace(document.FileName) ? "mcp-import.json" : document.FileName);
            var path = context.Storage.RoleLocalWorkspace.GetLocalPath($"mcp-import/{Guid.NewGuid():N}/{safeName}");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                await File.WriteAllTextAsync(path, text, token).ConfigureAwait(false);
                return new McpProjection(ImportResult: await configuration.ImportFileAsync(path, token).ConfigureAwait(false));
            }
            finally
            {
                try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
                catch { /* Temporary import cleanup must not replace the operation outcome. */ }
            }
        });

    [SunderMethod(Errors = ["mcp.management.failed"])]
    public ValueTask<SunderContent> DiscoverAsync(string serverId, bool reconnect, CancellationToken token = default)
        => ProjectAsync(async () => new McpProjection(ToolNames:
            (await connections.DiscoverAsync(await GetServerAsync(serverId, token), reconnect, token)).Select(tool => tool.Name).ToArray()));

    [SunderMethod(Errors = ["mcp.management.failed"])]
    public ValueTask<SunderContent> DisconnectAsync(string serverId, CancellationToken token = default)
        => ProjectAsync(async () =>
        {
            var server = await GetServerAsync(serverId, token).ConfigureAwait(false);
            await connections.DisconnectAsync(server.ServerId).ConfigureAwait(false);
            return new McpProjection(Presentation: await connections.GetPresentationAsync(server, token).ConfigureAwait(false));
        });

    [SunderMethod(Errors = ["mcp.management.failed"])]
    public ValueTask<SunderContent> ClearAuthorizationAsync(string serverId, CancellationToken token = default)
        => ProjectAsync(async () =>
        {
            var server = await GetServerAsync(serverId, token).ConfigureAwait(false);
            await oauth.ClearAsync(server, token).ConfigureAwait(false);
            return new McpProjection(Presentation: await connections.GetPresentationAsync(server, token).ConfigureAwait(false));
        });

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public async IAsyncEnumerable<McpChanged> ChangesAsync([EnumeratorCancellation] CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        yield return new(McpChangeKind.Servers);
        var invalidations = new McpChangeQueue();
        void Invalidate() => invalidations.Publish();
        invalidations.Publish();
        editor.ServersChanged += Invalidate;
        connections.StatusChanged += Invalidate;
        try
        {
            await foreach (var change in invalidations.ReadAllAsync(token).ConfigureAwait(false)) yield return change;
        }
        finally
        {
            editor.ServersChanged -= Invalidate;
            connections.StatusChanged -= Invalidate;
            invalidations.Complete();
        }
    }

    private static async ValueTask<SunderContent> ProjectAsync(Func<Task<McpProjection>> operation)
    {
        try { return McpManagementContent.Create(await operation().ConfigureAwait(false), "mcp-projection.json"); }
        catch (McpManagementOperationException error)
        { throw new SunderRpcException(new(SunderRpcErrorKind.Domain, "mcp.management.failed", error.Message)); }
    }

    private async Task<McpProjection> CatalogCoreAsync(CancellationToken token)
        => new(Servers: await editor.ListAsync(token).ConfigureAwait(false), Diagnostics: editor.Diagnostics);

    private async Task<ConfiguredMcpServerRecord> GetServerAsync(string? serverId, CancellationToken token)
        => await catalog.GetServerAsync(Require(serverId), token).ConfigureAwait(false)
            ?? throw new McpManagementOperationException("The selected MCP server was not found.");

    private static string Require(string? value)
        => string.IsNullOrWhiteSpace(value) ? throw new McpManagementOperationException("An MCP server id is required.") : value;
}

internal sealed class McpChangeQueue
{
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
    public void Publish() => _signals.Writer.TryWrite(true);
    public async IAsyncEnumerable<McpChanged> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var _ in _signals.Reader.ReadAllAsync(cancellationToken))
        {
            yield return new(McpChangeKind.Servers);
            yield return new(McpChangeKind.Status);
        }
    }
    public void Complete() => _signals.Writer.TryComplete();
}
