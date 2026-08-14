using Microsoft.Extensions.Logging;

namespace Sunder.Package.Agent.Mcp.Services;

public sealed class McpConfigurationCoordinator(
    McpEcosystemConfigurationImporter importer,
    McpSunderConfigurationSyncService? syncService,
    ILoggerFactory? loggerFactory = null) : IAsyncDisposable
{
    private readonly object _initializationLock = new();
    private readonly ILogger<McpConfigurationCoordinator>? _logger = loggerFactory?.CreateLogger<McpConfigurationCoordinator>();
    private readonly CancellationTokenSource _lifetime = new();
    private Task<McpConfigurationImportResult>? _initialization;
    private Task? _observation;
    private bool _disposed;

    public void Start()
    {
        lock (_initializationLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _observation ??= ObserveInitializationAsync();
        }
    }

    public Task<McpConfigurationImportResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        Task<McpConfigurationImportResult> initialization;
        lock (_initializationLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            initialization = _initialization ??= InitializeCoreAsync(_lifetime.Token);
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
                if (!_disposed)
                {
                    _initialization = null;
                }
            }

            throw;
        }
    }

    private async Task ObserveInitializationAsync()
    {
        try
        {
            await Task.Yield();
            _lifetime.Token.ThrowIfCancellationRequested();
            await InitializeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Initial Sunder MCP configuration synchronization failed.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? initialization;
        Task? observation;
        lock (_initializationLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            initialization = _initialization;
            observation = _observation;
        }

        _lifetime.Cancel();
        var tasks = new[] { initialization, observation }
            .Where(static task => task is not null)
            .Cast<Task>()
            .Distinct()
            .ToArray();
        if (tasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch
            {
                foreach (var task in tasks)
                {
                    _ = task.Exception;
                }
            }
        }

        _lifetime.Dispose();
    }
}
