using Microsoft.Extensions.Logging;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Mcp.Runtime;

internal sealed partial class McpAppRuntimeGateway :
    IMcpManagementGateway,
    IOwnerStreamAvailability,
    IDisposable,
    IAsyncDisposable
{
    private const int MaxImportBytes = 1024 * 1024;
    private readonly IMcpOwnerManagementClient _client;
    private readonly PackageCallbackFlowRunner _callbackFlow;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ILogger<McpAppRuntimeGateway>? _logger;
    private readonly OwnerStreamObserver _observer;
    private readonly Task _observationTask;
    private IReadOnlyList<McpCatalogDiagnostic> _diagnostics = [];
    private int _disposed;
    private int _lifetimeDisposed;

    internal McpAppRuntimeGateway(
        IMcpOwnerManagementClient client,
        IPackageCallbackClient callbacks,
        ILoggerFactory? loggerFactory = null)
    {
        _client = client;
        _callbackFlow = new PackageCallbackFlowRunner(callbacks);
        _logger = loggerFactory?.CreateLogger<McpAppRuntimeGateway>();
        _observer = new OwnerStreamObserver(LogObservationRetry);
        _observationTask = _observer.RunAsync(ObserveChangesOnceAsync, _lifetime.Token);
    }

    public event Action? ServersChanged;
    public event Action? StatusChanged;
    public IReadOnlyList<McpCatalogDiagnostic> Diagnostics => _diagnostics;
    public bool IsOwnerStreamAvailable => _observer.IsAvailable;
    public event Action<bool>? OwnerStreamAvailabilityChanged
    {
        add => _observer.AvailabilityChanged += value;
        remove => _observer.AvailabilityChanged -= value;
    }

    internal Task ObservationTask => _observationTask;

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
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException("Select an existing MCP configuration file.");
        }
        var document = await McpConfigurationSourceReader.ReadTextAsync(
            filePath,
            MaxImportBytes,
            cancellationToken).ConfigureAwait(false);
        return (await CommandAsync(
            new McpCommand(
                McpCommandKind.ImportDocument,
                Document: document,
                FileName: Path.GetFileName(filePath)),
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
            McpOAuthConstants.HandlerId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [McpOAuthConstants.ServerIdParameter] = server.ServerId,
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

    private async Task ObserveChangesOnceAsync(
        Action markAvailable,
        CancellationToken cancellationToken)
    {
        var connected = false;
        await foreach (var change in _client.WatchChangesAsync(cancellationToken))
        {
            if (!connected)
            {
                connected = true;
                markAvailable();
            }
            switch (change.Kind)
            {
                case McpChangeKind.Servers:
                    ServersChanged?.Invoke();
                    break;
                case McpChangeKind.Status:
                    StatusChanged?.Invoke();
                    break;
                default:
                    throw new InvalidDataException("The MCP Runtime returned a malformed change event.");
            }
        }
    }

    private void LogObservationRetry(Exception exception, TimeSpan retryDelay)
        => _logger?.LogWarning(
            "MCP owner change observation failed and will retry in {RetryDelayMilliseconds} ms ({ExceptionType}).",
            retryDelay.TotalMilliseconds,
            exception.GetType().Name);

    public void Dispose()
    {
        StopObservation();
        DisposeLifetime();
    }

    public async ValueTask DisposeAsync()
    {
        StopObservation();
        try
        {
            await _observationTask.ConfigureAwait(false);
        }
        finally
        {
            DisposeLifetime();
        }
    }

    private void StopObservation()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _lifetime.Cancel();
        }
    }

    private void DisposeLifetime()
    {
        if (Interlocked.Exchange(ref _lifetimeDisposed, 1) == 0)
        {
            _lifetime.Dispose();
        }
    }
}
