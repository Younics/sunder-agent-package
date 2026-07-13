using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Sunder.Package.Agent.Shared.Threading;

namespace Sunder.Package.Agent.Mcp.Services;

public sealed class McpClientConnectionManager : IAsyncDisposable
{
    private const int MaxStandardErrorLines = 10;
    private const int MaxStatusCacheEntries = 256;
    private static readonly TimeSpan ShutdownWaitTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LeaseDrainTimeout = TimeSpan.FromSeconds(2);
    private readonly ILogger<McpClientConnectionManager> _logger;
    private readonly IMcpClientConnectionFactory _connectionFactory;
    private readonly ConcurrentDictionary<string, CachedMcpConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedMcpToolMetadata> _toolMetadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly ReferenceCountedKeyedLock<string> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _backgroundRefreshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeConnectionCancellations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, McpConnectionStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _standardErrorLines = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private volatile bool _disposed;

    internal int KeyedLockCount => _locks.Count;
    internal int CachedStatusCount => _statuses.Count;

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
                    acquireInvocationLease: false,
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
            acquireInvocationLease: false,
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
            acquireInvocationLease: false,
            cancellationToken);

    private async Task<IReadOnlyList<McpClientTool>> GetToolsCoreAsync(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        int? discoveryTimeoutMilliseconds,
        McpConnectionScope scope,
        bool acquireInvocationLease,
        CancellationToken cancellationToken)
        => (await GetConnectionAccessAsync(
            server,
            headers,
            environmentVariables,
            discoveryTimeoutMilliseconds,
            scope,
            acquireInvocationLease,
            cancellationToken)).Tools;

    private async Task<McpConnectionAccess> GetConnectionAccessAsync(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        int? discoveryTimeoutMilliseconds,
        McpConnectionScope scope,
        bool acquireInvocationLease,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!acquireInvocationLease
            && _toolMetadata.TryGetValue(server.ServerId, out var metadata)
            && IsReusable(metadata, server))
        {
            return new McpConnectionAccess(metadata.Tools, null);
        }

        var key = BuildConnectionKey(server.ServerId, scope);

        using (await _locks.EnterAsync(key, cancellationToken).ConfigureAwait(false))
        {
            if (_connections.TryGetValue(key, out var existing) && IsReusable(existing, server))
            {
                if (!acquireInvocationLease)
                {
                    return new McpConnectionAccess(existing.Tools, null);
                }

                if (existing.TryAcquireLease(out var existingLease))
                {
                    return new McpConnectionAccess(existing.Tools, existingLease);
                }
            }

            if (!acquireInvocationLease
                && _toolMetadata.TryGetValue(server.ServerId, out metadata)
                && IsReusable(metadata, server))
            {
                return new McpConnectionAccess(metadata.Tools, null);
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
                var cached = new CachedMcpConnection(server.ServerId, scope, connection, tools, server.PersistenceVersion, server.UpdatedAtUtc);
                McpClientInvocationLease? lease = null;
                if (acquireInvocationLease && !cached.TryAcquireLease(out lease))
                {
                    throw new InvalidOperationException($"MCP connection for server '{server.DisplayName}' completed before it could be leased.");
                }

                _connections[key] = cached;
                _toolMetadata[server.ServerId] = new CachedMcpToolMetadata(tools, server.PersistenceVersion, server.UpdatedAtUtc);
                connection = null;
                SetStatus(server, McpConnectionStatusKind.Connected, $"Connected to MCP server '{server.DisplayName}'.", toolCount: tools.Length, toolNames: tools.Select(tool => tool.Name).ToArray());
                return new McpConnectionAccess(tools, lease);
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
                return new McpConnectionAccess([], null);
            }
            catch (Exception ex)
            {
                await DisposeFailedConnectionAsync(connection);
                SetStatus(server, McpConnectionStatusKind.Error, $"MCP server '{server.DisplayName}' is unavailable: {ex.Message}", ex.Message);
                _logger.LogWarning(ex, "Failed to connect to MCP server '{ServerId}'.", server.ServerId);
                return new McpConnectionAccess([], null);
            }
            finally
            {
                _activeConnectionCancellations.TryRemove(key, out _);
            }
        }
    }

    internal Task<McpClientInvocationLease?> AcquireClientLeaseAsync(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        int? discoveryTimeoutMilliseconds,
        CancellationToken cancellationToken = default)
        => AcquireClientLeaseCoreAsync(
            server,
            headers,
            environmentVariables,
            discoveryTimeoutMilliseconds,
            McpConnectionScope.Shared,
            cancellationToken);

    internal Task<McpClientInvocationLease?> AcquireClientLeaseAsync(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        int? discoveryTimeoutMilliseconds,
        McpConnectionScope scope,
        CancellationToken cancellationToken = default)
        => AcquireClientLeaseCoreAsync(server, headers, environmentVariables, discoveryTimeoutMilliseconds, scope, cancellationToken);

    private async Task<McpClientInvocationLease?> AcquireClientLeaseCoreAsync(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        int? discoveryTimeoutMilliseconds,
        McpConnectionScope scope,
        CancellationToken cancellationToken)
    {
        var access = await GetConnectionAccessAsync(
            server,
            headers,
            environmentVariables,
            discoveryTimeoutMilliseconds,
            scope,
            acquireInvocationLease: true,
            cancellationToken);
        return access.Lease;
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

    public async Task ReconnectServerAsync(string serverId)
    {
        _toolMetadata.TryRemove(serverId, out _);
        await DisconnectServerAsync(serverId).ConfigureAwait(false);
        _toolMetadata.TryRemove(serverId, out _);
        _statuses.TryRemove(serverId, out _);
        _standardErrorLines.TryRemove(serverId, out _);
        StatusChanged?.Invoke();
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
            await Task.WhenAll(_backgroundRefreshes.Values.ToArray()).WaitAsync(ShutdownWaitTimeout);
        }
        catch (Exception ex) when (ex is OperationCanceledException or AggregateException or TimeoutException)
        {
            _logger.LogDebug(ex, "MCP background refreshes stopped during disposal.");
        }

        var keys = _connections.Keys
            .Concat(_activeConnectionCancellations.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var key in keys)
        {
            try
            {
                using var timeout = new CancellationTokenSource(ShutdownWaitTimeout);
                using (await _locks.EnterAsync(key, timeout.Token).ConfigureAwait(false))
                {
                    await DisconnectCoreAsync(key).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Timed out waiting to shut down MCP connection '{ConnectionKey}'.", key);
            }
        }

        _toolMetadata.Clear();
        _statuses.Clear();
        _standardErrorLines.Clear();

        _disposeCancellation.Dispose();
    }

    private async Task DisconnectWithGateAsync(string connectionKey)
    {
        if (_activeConnectionCancellations.TryGetValue(connectionKey, out var cancellation))
        {
            await TryCancelAsync(cancellation);
        }

        using var timeout = new CancellationTokenSource(ShutdownWaitTimeout);
        try
        {
            using (await _locks.EnterAsync(connectionKey, timeout.Token).ConfigureAwait(false))
            {
                await DisconnectCoreAsync(connectionKey).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            var separator = connectionKey.IndexOf('\0');
            var serverId = separator < 0 ? connectionKey : connectionKey[..separator];
            SetStatus(serverId, McpConnectionStatusKind.Error, "Timed out while disconnecting from MCP server.");
            _logger.LogWarning("Timed out waiting for MCP connection gate '{ConnectionKey}' during disconnect.", connectionKey);
        }
    }

    private async Task DisconnectCoreAsync(string connectionKey)
    {
        if (_connections.TryRemove(connectionKey, out var connection))
        {
            connection.Retire();
            try
            {
                await connection.WaitForLeasesAsync().WaitAsync(LeaseDrainTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "Timed out waiting for invocation leases before disconnecting MCP server '{ServerId}'.",
                    connection.ServerId);
            }

            try
            {
                await connection.Connection.DisposeAsync().AsTask().WaitAsync(ShutdownWaitTimeout).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
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
        TrimStatusCaches();
        StatusChanged?.Invoke();
    }

    private void TrimStatusCaches()
    {
        if (_statuses.Count <= MaxStatusCacheEntries)
        {
            return;
        }

        foreach (var candidate in _statuses.Values
                     .Where(status => CountActiveConnections(status.ServerId) == 0)
                     .OrderBy(status => status.LastChangedAtUtc ?? DateTimeOffset.MinValue)
                     .Take(_statuses.Count - MaxStatusCacheEntries))
        {
            _statuses.TryRemove(candidate.ServerId, out _);
            _standardErrorLines.TryRemove(candidate.ServerId, out _);
        }
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

    private sealed class CachedMcpConnection(
        string serverId,
        McpConnectionScope scope,
        IMcpClientConnection connection,
        IReadOnlyList<McpClientTool> tools,
        int persistenceVersion,
        DateTimeOffset serverUpdatedAtUtc)
    {
        private readonly object _sync = new();
        private TaskCompletionSource? _leasesDrained;
        private int _activeLeaseCount;
        private bool _retired;

        public string ServerId { get; } = serverId;
        public McpConnectionScope Scope { get; } = scope;
        public IMcpClientConnection Connection { get; } = connection;
        public IReadOnlyList<McpClientTool> Tools { get; } = tools;
        public int PersistenceVersion { get; } = persistenceVersion;
        public DateTimeOffset ServerUpdatedAtUtc { get; } = serverUpdatedAtUtc;

        public bool TryAcquireLease(out McpClientInvocationLease? lease)
        {
            lock (_sync)
            {
                if (_retired || Connection.Completion.IsCompleted)
                {
                    lease = null;
                    return false;
                }

                _activeLeaseCount++;
                lease = new McpClientInvocationLease(Connection.Client, ReleaseLease);
                return true;
            }
        }

        public void Retire()
        {
            lock (_sync)
            {
                _retired = true;
                if (_activeLeaseCount > 0)
                {
                    _leasesDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }

        public Task WaitForLeasesAsync()
        {
            lock (_sync)
            {
                return _leasesDrained?.Task ?? Task.CompletedTask;
            }
        }

        private void ReleaseLease()
        {
            TaskCompletionSource? drained = null;
            lock (_sync)
            {
                if (--_activeLeaseCount == 0 && _retired)
                {
                    drained = _leasesDrained;
                    _leasesDrained = null;
                }
            }

            drained?.TrySetResult();
        }
    }

    private sealed record CachedMcpToolMetadata(
        IReadOnlyList<McpClientTool> Tools,
        int PersistenceVersion,
        DateTimeOffset ServerUpdatedAtUtc);

    private sealed record McpConnectionAccess(
        IReadOnlyList<McpClientTool> Tools,
        McpClientInvocationLease? Lease);

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

internal sealed class McpClientInvocationLease(McpClient? client, Action release) : IAsyncDisposable
{
    private Action? _release = release;

    public McpClient? Client { get; } = client;

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _release, null)?.Invoke();
        return ValueTask.CompletedTask;
    }
}
