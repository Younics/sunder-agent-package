using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Sunder.Package.Agent.Mcp.Services;

public sealed class McpClientConnectionManager : IAsyncDisposable
{
    private const int MaxStandardErrorLines = 10;
    private readonly ILogger<McpClientConnectionManager> _logger;
    private readonly IMcpClientConnectionFactory _connectionFactory;
    private readonly ConcurrentDictionary<string, CachedMcpConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedMcpToolMetadata> _toolMetadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _backgroundRefreshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeConnectionCancellations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, McpConnectionStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _standardErrorLines = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private volatile bool _disposed;

    public McpClientConnectionManager(ILoggerFactory loggerFactory, McpOAuthService? oauthService = null)
        : this(loggerFactory, new McpClientConnectionFactory(loggerFactory, oauthService))
    {
    }

    internal McpClientConnectionManager(ILoggerFactory loggerFactory, IMcpClientConnectionFactory connectionFactory)
    {
        _logger = loggerFactory.CreateLogger<McpClientConnectionManager>();
        _connectionFactory = connectionFactory;
    }

    public event Action? StatusChanged;

    public IReadOnlyList<McpClientTool>? GetCachedTools(ConfiguredMcpServerRecord server)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _toolMetadata.TryGetValue(server.ServerId, out var existing) && IsReusable(existing, server)
            ? existing.Tools
            : null;
    }

    public McpConnectionStatus GetStatus(ConfiguredMcpServerRecord server)
    {
        if (!server.IsEnabled)
        {
            return new McpConnectionStatus(server.ServerId, McpConnectionStatusKind.Disabled, "Disabled in MCP settings.", 0, StandardErrorTail: GetStandardErrorTail(server.ServerId));
        }

        var activeCount = CountActiveConnections(server.ServerId);
        var stderr = GetStandardErrorTail(server.ServerId);
        if (_statuses.TryGetValue(server.ServerId, out var status))
        {
            return status.Kind == McpConnectionStatusKind.Connected && activeCount == 0
                ? status with { Kind = McpConnectionStatusKind.Disconnected, Message = "Disconnected from MCP server.", ActiveConnectionCount = 0, StandardErrorTail = stderr }
                : status with { ActiveConnectionCount = activeCount, StandardErrorTail = stderr };
        }

        if (_toolMetadata.TryGetValue(server.ServerId, out var metadata))
        {
            return new McpConnectionStatus(server.ServerId, McpConnectionStatusKind.Idle, "MCP tool metadata is available.", activeCount, metadata.Tools.Count, metadata.Tools.Select(tool => tool.Name).ToArray(), StandardErrorTail: stderr);
        }

        return new McpConnectionStatus(server.ServerId, McpConnectionStatusKind.Idle, "Configured but not connected yet.", 0, StandardErrorTail: stderr);
    }

    public void RefreshToolsInBackground(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        int? discoveryTimeoutMilliseconds)
        => RefreshToolsInBackground(server, headers, environmentVariables, discoveryTimeoutMilliseconds, McpConnectionScope.Shared);

    internal void RefreshToolsInBackground(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        int? discoveryTimeoutMilliseconds,
        McpConnectionScope scope)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = server.ServerId;
        if (_backgroundRefreshes.TryGetValue(key, out var existing) && !existing.IsCompleted)
        {
            return;
        }

        _backgroundRefreshes[key] = Task.Run(async () =>
        {
            try
            {
                await GetToolsCoreAsync(
                    server,
                    headers,
                    environmentVariables,
                    discoveryTimeoutMilliseconds,
                    scope,
                    requireConnection: false,
                    _disposeCancellation.Token);
            }
            catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background MCP tool refresh failed for server '{ServerId}'.", server.ServerId);
            }
            finally
            {
                _backgroundRefreshes.TryRemove(key, out _);
            }
        });
    }

    public Task<IReadOnlyList<McpClientTool>> GetToolsAsync(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        int? discoveryTimeoutMilliseconds,
        CancellationToken cancellationToken = default)
        => GetToolsCoreAsync(
            server,
            headers,
            environmentVariables,
            discoveryTimeoutMilliseconds,
            McpConnectionScope.Shared,
            requireConnection: false,
            cancellationToken);

    internal Task<IReadOnlyList<McpClientTool>> GetToolsAsync(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        int? discoveryTimeoutMilliseconds,
        McpConnectionScope scope,
        CancellationToken cancellationToken = default)
        => GetToolsCoreAsync(
            server,
            headers,
            environmentVariables,
            discoveryTimeoutMilliseconds,
            scope,
            requireConnection: false,
            cancellationToken);

    private async Task<IReadOnlyList<McpClientTool>> GetToolsCoreAsync(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        int? discoveryTimeoutMilliseconds,
        McpConnectionScope scope,
        bool requireConnection,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!requireConnection
            && _toolMetadata.TryGetValue(server.ServerId, out var metadata)
            && IsReusable(metadata, server))
        {
            return metadata.Tools;
        }

        var key = BuildConnectionKey(server.ServerId, scope);

        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_connections.TryGetValue(key, out var existing) && IsReusable(existing, server))
            {
                return existing.Tools;
            }

            if (!requireConnection
                && _toolMetadata.TryGetValue(server.ServerId, out metadata)
                && IsReusable(metadata, server))
            {
                return metadata.Tools;
            }

            await DisconnectCoreAsync(key);
            IMcpClientConnection? connection = null;
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCancellation.Token);
            _activeConnectionCancellations[key] = operationCancellation;
            using var timeout = CreateTimeoutScope(discoveryTimeoutMilliseconds, operationCancellation.Token);
            try
            {
                SetStatus(server, McpConnectionStatusKind.Connecting, $"Connecting to MCP server '{server.DisplayName}'.");
                connection = await _connectionFactory.ConnectAsync(
                    server,
                    headers,
                    environmentVariables,
                    discoveryTimeoutMilliseconds,
                    line => RecordStandardErrorLine(server.ServerId, line),
                    timeout.Token);
                SetStatus(server, McpConnectionStatusKind.DiscoveringTools, $"Discovering tools from MCP server '{server.DisplayName}'.");
                var tools = (await connection.ListToolsAsync(timeout.Token)).ToArray();
                _connections[key] = new CachedMcpConnection(server.ServerId, scope, connection, tools, server.PersistenceVersion, server.UpdatedAtUtc);
                _toolMetadata[server.ServerId] = new CachedMcpToolMetadata(tools, server.PersistenceVersion, server.UpdatedAtUtc);
                connection = null;
                SetStatus(server, McpConnectionStatusKind.Connected, $"Connected to MCP server '{server.DisplayName}'.", toolCount: tools.Length, toolNames: tools.Select(tool => tool.Name).ToArray());
                return tools;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _disposeCancellation.IsCancellationRequested || operationCancellation.IsCancellationRequested)
            {
                await DisposeFailedConnectionAsync(connection);
                SetStatus(server, McpConnectionStatusKind.Disconnected, $"Connection to MCP server '{server.DisplayName}' was canceled.");
                throw;
            }
            catch (OperationCanceledException ex)
            {
                await DisposeFailedConnectionAsync(connection);
                SetStatus(server, McpConnectionStatusKind.Error, $"MCP server '{server.DisplayName}' timed out during discovery.", ex.Message);
                return [];
            }
            catch (Exception ex)
            {
                await DisposeFailedConnectionAsync(connection);
                SetStatus(server, McpConnectionStatusKind.Error, $"MCP server '{server.DisplayName}' is unavailable: {ex.Message}", ex.Message);
                _logger.LogWarning(ex, "Failed to connect to MCP server '{ServerId}'.", server.ServerId);
                return [];
            }
            finally
            {
                _activeConnectionCancellations.TryRemove(key, out _);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<McpClient?> GetClientAsync(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        int? discoveryTimeoutMilliseconds,
        CancellationToken cancellationToken = default)
        => await GetClientCoreAsync(
            server,
            headers,
            environmentVariables,
            discoveryTimeoutMilliseconds,
            McpConnectionScope.Shared,
            cancellationToken);

    internal Task<McpClient?> GetClientAsync(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        int? discoveryTimeoutMilliseconds,
        McpConnectionScope scope,
        CancellationToken cancellationToken = default)
        => GetClientCoreAsync(server, headers, environmentVariables, discoveryTimeoutMilliseconds, scope, cancellationToken);

    private async Task<McpClient?> GetClientCoreAsync(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        int? discoveryTimeoutMilliseconds,
        McpConnectionScope scope,
        CancellationToken cancellationToken)
    {
        await GetToolsCoreAsync(server, headers, environmentVariables, discoveryTimeoutMilliseconds, scope, requireConnection: true, cancellationToken);
        return _connections.TryGetValue(BuildConnectionKey(server.ServerId, scope), out var cached) ? cached.Connection.Client : null;
    }

    public async Task DisconnectServerAsync(string serverId)
    {
        var connectionKeys = _connections
            .Where(entry => string.Equals(entry.Value.ServerId, serverId, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Key)
            .Concat(_activeConnectionCancellations.Keys.Where(key => IsConnectionKeyForServer(key, serverId)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var key in connectionKeys)
        {
            await DisconnectWithGateAsync(key).ConfigureAwait(false);
        }

        if (connectionKeys.Length == 0)
        {
            SetStatus(serverId, McpConnectionStatusKind.Disconnected, "Disconnected from MCP server.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _disposeCancellation.CancelAsync();
        foreach (var cancellation in _activeConnectionCancellations.Values)
        {
            await TryCancelAsync(cancellation);
        }

        try
        {
            await Task.WhenAll(_backgroundRefreshes.Values.ToArray());
        }
        catch (Exception ex) when (ex is OperationCanceledException or AggregateException)
        {
            _logger.LogDebug(ex, "MCP background refreshes stopped during disposal.");
        }

        foreach (var key in _locks.Keys.ToArray())
        {
            var gate = _locks[key];
            await gate.WaitAsync();
            try
            {
                await DisconnectCoreAsync(key);
            }
            finally
            {
                gate.Release();
            }
        }

        _disposeCancellation.Dispose();
    }

    private async Task DisconnectWithGateAsync(string connectionKey)
    {
        if (_activeConnectionCancellations.TryGetValue(connectionKey, out var cancellation))
        {
            await TryCancelAsync(cancellation);
        }

        var gate = _locks.GetOrAdd(connectionKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await DisconnectCoreAsync(connectionKey);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DisconnectCoreAsync(string connectionKey)
    {
        if (_connections.TryRemove(connectionKey, out var connection))
        {
            try
            {
                await connection.Connection.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dispose MCP client for server '{ServerId}'.", connection.ServerId);
            }

            SetStatus(connection.ServerId, McpConnectionStatusKind.Disconnected, "Disconnected from MCP server.");
        }
    }

    private async ValueTask DisposeFailedConnectionAsync(IMcpClientConnection? connection)
    {
        if (connection is null)
        {
            return;
        }

        try
        {
            await connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispose failed MCP client connection.");
        }
    }

    private void RecordStandardErrorLine(string serverId, string line)
    {
        var queue = _standardErrorLines.GetOrAdd(serverId, _ => new ConcurrentQueue<string>());
        queue.Enqueue(line);
        while (queue.Count > MaxStandardErrorLines && queue.TryDequeue(out _))
        {
        }

        StatusChanged?.Invoke();
    }

    private int CountActiveConnections(string serverId)
        => _connections.Values.Count(connection => string.Equals(connection.ServerId, serverId, StringComparison.OrdinalIgnoreCase) && IsAlive(connection));

    private IReadOnlyList<string> GetStandardErrorTail(string serverId)
        => _standardErrorLines.TryGetValue(serverId, out var queue) ? queue.ToArray() : [];

    private void SetStatus(ConfiguredMcpServerRecord server, McpConnectionStatusKind kind, string message, string? error = null, int? toolCount = null, IReadOnlyList<string>? toolNames = null)
        => SetStatus(server.ServerId, kind, message, error, toolCount, toolNames);

    private void SetStatus(string serverId, McpConnectionStatusKind kind, string message, string? error = null, int? toolCount = null, IReadOnlyList<string>? toolNames = null)
    {
        _statuses[serverId] = new McpConnectionStatus(serverId, kind, message, CountActiveConnections(serverId), toolCount, toolNames, DateTimeOffset.UtcNow, error, GetStandardErrorTail(serverId));
        StatusChanged?.Invoke();
    }

    private TimeoutScope CreateTimeoutScope(int? milliseconds, CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCancellation.Token);
        if (milliseconds is > 0)
        {
            source.CancelAfter(milliseconds.Value);
        }

        return new TimeoutScope(source.Token, source);
    }

    private static bool IsReusable(CachedMcpConnection existing, ConfiguredMcpServerRecord server)
        => existing.PersistenceVersion == server.PersistenceVersion && existing.ServerUpdatedAtUtc == server.UpdatedAtUtc && IsAlive(existing);

    private static bool IsReusable(CachedMcpToolMetadata existing, ConfiguredMcpServerRecord server)
        => existing.PersistenceVersion == server.PersistenceVersion && existing.ServerUpdatedAtUtc == server.UpdatedAtUtc;

    private static bool IsAlive(CachedMcpConnection existing) => !existing.Connection.Completion.IsCompleted;

    private static async Task TryCancelAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await cancellation.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string BuildConnectionKey(string serverId, McpConnectionScope scope)
        => $"{serverId}\0{scope.ScopeId}";

    private static bool IsConnectionKeyForServer(string connectionKey, string serverId)
        => connectionKey.StartsWith(serverId + "\0", StringComparison.OrdinalIgnoreCase);

    private sealed record CachedMcpConnection(
        string ServerId,
        McpConnectionScope Scope,
        IMcpClientConnection Connection,
        IReadOnlyList<McpClientTool> Tools,
        int PersistenceVersion,
        DateTimeOffset ServerUpdatedAtUtc);

    private sealed record CachedMcpToolMetadata(
        IReadOnlyList<McpClientTool> Tools,
        int PersistenceVersion,
        DateTimeOffset ServerUpdatedAtUtc);

    private sealed class TimeoutScope(CancellationToken token, CancellationTokenSource source) : IDisposable
    {
        public CancellationToken Token { get; } = token;

        public void Dispose() => source.Dispose();
    }
}

internal readonly record struct McpConnectionScope(string ScopeId)
{
    public static McpConnectionScope Shared { get; } = new("metadata");

    public static McpConnectionScope For(Guid? sessionId, string? workspaceId)
        => sessionId is null
            ? Shared
            : new($"session:{sessionId.Value:N}:workspace:{workspaceId?.Trim().ToLowerInvariant() ?? "none"}");
}
