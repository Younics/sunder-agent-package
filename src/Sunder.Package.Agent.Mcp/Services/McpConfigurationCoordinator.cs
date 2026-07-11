using Microsoft.Extensions.Logging;

namespace Sunder.Package.Agent.Mcp.Services;

public sealed class McpConfigurationCoordinator(
    McpEcosystemConfigurationImporter importer,
    McpSunderConfigurationSyncService? syncService,
    ILoggerFactory? loggerFactory = null)
{
    private readonly object _initializationLock = new();
    private readonly ILogger<McpConfigurationCoordinator>? _logger = loggerFactory?.CreateLogger<McpConfigurationCoordinator>();
    private Task<McpConfigurationImportResult>? _initialization;

    public void Start() => _ = ObserveInitializationAsync();

    public Task<McpConfigurationImportResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        Task<McpConfigurationImportResult> initialization;
        lock (_initializationLock)
        {
            initialization = _initialization ??= InitializeCoreAsync(CancellationToken.None);
        }

        return cancellationToken.CanBeCanceled ? initialization.WaitAsync(cancellationToken) : initialization;
    }

    public Task<McpConfigurationImportResult> ReloadAsync(CancellationToken cancellationToken = default)
        => syncService?.SyncAsync(cancellationToken) ?? Task.FromResult(new McpConfigurationImportResult(0, 0, []));

    public Task<McpConfigurationImportResult> ImportCommonAsync(CancellationToken cancellationToken = default)
        => importer.ImportCommonConfigurationsAsync(cancellationToken);

    public Task<McpConfigurationImportResult> ImportFileAsync(string filePath, CancellationToken cancellationToken = default)
        => importer.ImportFileAsync(filePath, cancellationToken);

    private async Task<McpConfigurationImportResult> InitializeCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            return syncService is null
                ? new McpConfigurationImportResult(0, 0, [])
                : await syncService.SyncAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_initializationLock)
            {
                _initialization = null;
            }

            throw;
        }
    }

    private async Task ObserveInitializationAsync()
    {
        try
        {
            await InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Initial Sunder MCP configuration synchronization failed.");
        }
    }
}
