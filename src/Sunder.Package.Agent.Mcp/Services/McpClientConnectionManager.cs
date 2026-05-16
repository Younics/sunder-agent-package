using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Sunder.Package.Agent.Mcp.Services;

public sealed class McpClientConnectionManager(ILoggerFactory loggerFactory) : IAsyncDisposable
{
    private const int MaxStandardErrorLines = 10;

    private readonly ILogger<McpClientConnectionManager> _logger = loggerFactory.CreateLogger<McpClientConnectionManager>();
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly ConcurrentDictionary<string, CachedMcpSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _backgroundRefreshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeConnectionCancellations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, McpConnectionStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _standardErrorLines = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private volatile bool _disposed;

    public event Action? StatusChanged;

    public IReadOnlyList<McpClientTool>? GetCachedTools(Guid sessionId, ConfiguredMcpServerRecord server)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _sessions.TryGetValue(server.ServerId, out var existing)
               && existing.ServerUpdatedAtUtc == server.UpdatedAtUtc
               && IsSessionAlive(existing)
            ? existing.Tools
            : null;
    }

    public McpConnectionStatus GetStatus(ConfiguredMcpServerRecord server)
    {
        if (!server.IsEnabled)
        {
            return new McpConnectionStatus(
                server.ServerId,
                McpConnectionStatusKind.Disabled,
                "Disabled in MCP settings.",
                ActiveConnectionCount: 0,
                StandardErrorTail: GetStandardErrorTail(server.ServerId));
        }

        var activeConnectionCount = CountActiveConnections(server.ServerId);
        var standardErrorTail = GetStandardErrorTail(server.ServerId);
        if (_statuses.TryGetValue(server.ServerId, out var status))
        {
            if (status.Kind == McpConnectionStatusKind.Connected && activeConnectionCount == 0)
            {
                return status with
                {
                    Kind = McpConnectionStatusKind.Disconnected,
                    Message = "Disconnected from MCP server.",
                    ActiveConnectionCount = 0,
                    StandardErrorTail = standardErrorTail,
                };
            }

            return status with
            {
                ActiveConnectionCount = activeConnectionCount,
                StandardErrorTail = standardErrorTail,
            };
        }

        if (_sessions.TryGetValue(server.ServerId, out var session) && IsSessionAlive(session))
        {
            return new McpConnectionStatus(
                server.ServerId,
                McpConnectionStatusKind.Connected,
                "Connected to MCP server.",
                activeConnectionCount,
                ToolCount: session.Tools.Count,
                ToolNames: session.Tools.Select(tool => tool.Name).ToArray(),
                StandardErrorTail: GetStandardErrorTail(server.ServerId));
        }

        return activeConnectionCount > 0
            ? new McpConnectionStatus(
                server.ServerId,
                McpConnectionStatusKind.Connected,
                "Connected to MCP server.",
                activeConnectionCount,
                StandardErrorTail: GetStandardErrorTail(server.ServerId))
            : new McpConnectionStatus(
                server.ServerId,
                McpConnectionStatusKind.Idle,
                "Configured but not connected yet.",
                ActiveConnectionCount: 0,
                StandardErrorTail: GetStandardErrorTail(server.ServerId));
    }

    public void RefreshToolsInBackground(
        Guid sessionId,
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        int? discoveryTimeoutMilliseconds)
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
                await GetToolsAsync(sessionId, server, headers, environmentVariables, discoveryTimeoutMilliseconds, _disposeCancellation.Token);
            }
            catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background MCP tool refresh failed for server '{ServerId}' and session {SessionId}.", server.ServerId, sessionId);
            }
            finally
            {
                _backgroundRefreshes.TryRemove(key, out _);
            }
        });
    }

    public async Task<IReadOnlyList<McpClientTool>> GetToolsAsync(
        Guid sessionId,
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        int? discoveryTimeoutMilliseconds,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = server.ServerId;
        if (_sessions.TryGetValue(key, out var existing) && IsSessionReusable(existing, server))
        {
            return existing.Tools;
        }

        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_sessions.TryGetValue(key, out existing) && IsSessionReusable(existing, server))
            {
                return existing.Tools;
            }

            await DisconnectCoreAsync(key);

            ConnectedMcpSession? connectedSession = null;
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCancellation.Token);
            _activeConnectionCancellations[key] = operationCancellation;
            using var timeoutScope = CreateTimeoutScope(discoveryTimeoutMilliseconds, operationCancellation.Token);
            try
            {
                SetStatus(server, McpConnectionStatusKind.Connecting, $"Connecting to MCP server '{server.DisplayName}'.");
                connectedSession = await ConnectAsync(server, headers, environmentVariables, discoveryTimeoutMilliseconds, timeoutScope.Token);
                SetStatus(server, McpConnectionStatusKind.DiscoveringTools, $"Discovering tools from MCP server '{server.DisplayName}'.");
                var tools = (await connectedSession.Client.ListToolsAsync(cancellationToken: timeoutScope.Token)).ToArray();
                _sessions[key] = new CachedMcpSession(
                    connectedSession.Client,
                    tools,
                    server.UpdatedAtUtc,
                    connectedSession.OwnedHttpClient);
                connectedSession = null;
                SetStatus(
                    server,
                    McpConnectionStatusKind.Connected,
                    $"Connected to MCP server '{server.DisplayName}'.",
                    toolCount: tools.Length,
                    toolNames: tools.Select(tool => tool.Name).ToArray());
                return tools;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _disposeCancellation.IsCancellationRequested || operationCancellation.IsCancellationRequested)
            {
                await DisposeConnectedSessionAsync(connectedSession);
                SetStatus(server, McpConnectionStatusKind.Disconnected, $"Connection to MCP server '{server.DisplayName}' was canceled.");
                throw;
            }
            catch (OperationCanceledException ex)
            {
                await DisposeConnectedSessionAsync(connectedSession);
                SetStatus(server, McpConnectionStatusKind.Error, $"MCP server '{server.DisplayName}' timed out during discovery.", ex.Message);
                _logger.LogWarning(ex, "Timed out connecting to MCP server '{ServerId}' for session {SessionId}.", server.ServerId, sessionId);
                return [];
            }
            catch (Exception ex)
            {
                await DisposeConnectedSessionAsync(connectedSession);
                SetStatus(server, McpConnectionStatusKind.Error, $"MCP server '{server.DisplayName}' is unavailable: {ex.Message}", ex.Message);
                _logger.LogWarning(ex, "Failed to connect to MCP server '{ServerId}' for session {SessionId}.", server.ServerId, sessionId);
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
        Guid sessionId,
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        int? discoveryTimeoutMilliseconds,
        CancellationToken cancellationToken = default)
    {
        var tools = await GetToolsAsync(sessionId, server, headers, environmentVariables, discoveryTimeoutMilliseconds, cancellationToken);
        return tools.Count == 0 && !_sessions.TryGetValue(server.ServerId, out var cached)
            ? null
            : _sessions.TryGetValue(server.ServerId, out cached)
                ? cached.Client
                : null;
    }

    public async Task DisconnectSessionAsync(Guid sessionId)
    {
        await Task.CompletedTask;
    }

    public async Task DisconnectServerAsync(string serverId)
    {
        await DisconnectWithGateAsync(serverId);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _disposeCancellation.CancelAsync();

        var backgroundRefreshes = _backgroundRefreshes.Values.ToArray();
        if (backgroundRefreshes.Length > 0)
        {
            try
            {
                await Task.WhenAll(backgroundRefreshes);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "One or more background MCP refreshes failed during disposal.");
            }
        }

        foreach (var key in _sessions.Keys.ToArray())
        {
            await DisconnectCoreAsync(key);
        }

        _disposeCancellation.Dispose();
    }

    private async Task<ConnectedMcpSession> ConnectAsync(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        int? discoveryTimeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        return server.TransportType switch
        {
            ConfiguredMcpTransportType.Stdio => await ConnectStdioAsync(server, environmentVariables, discoveryTimeoutMilliseconds, cancellationToken),
            ConfiguredMcpTransportType.HttpSse => await ConnectHttpAsync(server, headers, discoveryTimeoutMilliseconds, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported MCP transport '{server.TransportType}'."),
        };
    }

    private async Task<ConnectedMcpSession> ConnectStdioAsync(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> environmentVariables,
        int? discoveryTimeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        if (server.CommandParts.Length == 0)
        {
            throw new InvalidOperationException($"MCP server '{server.DisplayName}' is missing a command.");
        }

        var transport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = server.DisplayName,
                Command = server.CommandParts[0],
                Arguments = server.CommandParts.Skip(1).ToArray(),
                WorkingDirectory = string.IsNullOrWhiteSpace(server.WorkingDirectory) ? null : server.WorkingDirectory,
                EnvironmentVariables = environmentVariables.Count == 0
                    ? null
                    : environmentVariables.ToDictionary(item => item.Key, item => (string?)item.Value, StringComparer.OrdinalIgnoreCase),
                StandardErrorLines = line => RecordStandardErrorLine(server.ServerId, line),
            },
            _loggerFactory);

        var client = await McpClient.CreateAsync(transport, CreateClientOptions(discoveryTimeoutMilliseconds), _loggerFactory, cancellationToken);
        return new ConnectedMcpSession(client, OwnedHttpClient: null);
    }

    private async Task<ConnectedMcpSession> ConnectHttpAsync(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        int? discoveryTimeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(server.EndpointUrl))
        {
            throw new InvalidOperationException($"MCP server '{server.DisplayName}' is missing an endpoint URL.");
        }

        var options = new HttpClientTransportOptions
        {
            Name = server.DisplayName,
            Endpoint = new Uri(server.EndpointUrl),
            TransportMode = HttpTransportMode.AutoDetect,
            ConnectionTimeout = ToSdkTimeout(discoveryTimeoutMilliseconds),
            AdditionalHeaders = headers.Count == 0 ? null : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase),
        };

        var httpClient = new HttpClient
        {
            Timeout = ToSdkTimeout(discoveryTimeoutMilliseconds),
        };

        var transport = new HttpClientTransport(options, httpClient, _loggerFactory);

        var client = await McpClient.CreateAsync(transport, CreateClientOptions(discoveryTimeoutMilliseconds), _loggerFactory, cancellationToken);
        return new ConnectedMcpSession(client, httpClient);
    }

    private async Task DisconnectWithGateAsync(string serverId)
    {
        if (_activeConnectionCancellations.TryGetValue(serverId, out var cancellation))
        {
            try
            {
                await cancellation.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        var gate = _locks.GetOrAdd(serverId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await DisconnectCoreAsync(serverId);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DisconnectCoreAsync(string serverId)
    {
        if (_sessions.TryRemove(serverId, out var session))
        {
            try
            {
                await session.Client.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dispose MCP client for server '{ServerId}'.", serverId);
            }

            try
            {
                session.OwnedHttpClient?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dispose MCP HTTP client for server '{ServerId}'.", serverId);
            }
        }

        if (CountActiveConnections(serverId) == 0)
        {
            SetStatus(serverId, McpConnectionStatusKind.Disconnected, "Disconnected from MCP server.");
        }
    }

    private static bool IsSessionReusable(CachedMcpSession existing, ConfiguredMcpServerRecord server)
        => existing.ServerUpdatedAtUtc == server.UpdatedAtUtc
           && IsSessionAlive(existing);

    private static bool IsSessionAlive(CachedMcpSession existing)
        => !existing.Client.Completion.IsCompleted;

    private TimeoutScope CreateTimeoutScope(int? effectiveTimeoutMilliseconds, CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCancellation.Token);
        if (effectiveTimeoutMilliseconds is > 0)
        {
            source.CancelAfter(effectiveTimeoutMilliseconds.Value);
        }

        return new TimeoutScope(source.Token, source);
    }

    private static McpClientOptions CreateClientOptions(int? discoveryTimeoutMilliseconds)
        => new()
        {
            InitializationTimeout = ToSdkTimeout(discoveryTimeoutMilliseconds),
        };

    private static TimeSpan ToSdkTimeout(int? timeoutMilliseconds)
        => timeoutMilliseconds is > 0
            ? TimeSpan.FromMilliseconds(timeoutMilliseconds.Value)
            : Timeout.InfiniteTimeSpan;

    private async ValueTask DisposeConnectedSessionAsync(ConnectedMcpSession? session)
    {
        if (session is null)
        {
            return;
        }

        try
        {
            await session.Client.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispose failed MCP client connection.");
        }

        try
        {
            session.OwnedHttpClient?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispose failed MCP HTTP client connection.");
        }
    }

    private void RecordStandardErrorLine(string serverId, string line)
    {
        var queue = _standardErrorLines.GetOrAdd(serverId, _ => new ConcurrentQueue<string>());
        queue.Enqueue(line);
        while (queue.Count > MaxStandardErrorLines && queue.TryDequeue(out _))
        {
        }

        if (_statuses.TryGetValue(serverId, out var existing))
        {
            _statuses[serverId] = existing with
            {
                StandardErrorTail = GetStandardErrorTail(serverId),
                LastChangedAtUtc = DateTimeOffset.UtcNow,
            };
            StatusChanged?.Invoke();
        }
    }

    private int CountActiveConnections(string serverId)
        => _sessions.TryGetValue(serverId, out var session) && IsSessionAlive(session) ? 1 : 0;

    private IReadOnlyList<string> GetStandardErrorTail(string serverId)
        => _standardErrorLines.TryGetValue(serverId, out var queue) ? queue.ToArray() : [];

    private void SetStatus(
        ConfiguredMcpServerRecord server,
        McpConnectionStatusKind kind,
        string message,
        string? error = null,
        int? toolCount = null,
        IReadOnlyList<string>? toolNames = null)
        => SetStatus(server.ServerId, kind, message, error, toolCount, toolNames);

    private void SetStatus(
        string serverId,
        McpConnectionStatusKind kind,
        string message,
        string? error = null,
        int? toolCount = null,
        IReadOnlyList<string>? toolNames = null)
    {
        _statuses[serverId] = new McpConnectionStatus(
            serverId,
            kind,
            message,
            CountActiveConnections(serverId),
            toolCount,
            toolNames,
            DateTimeOffset.UtcNow,
            error,
            GetStandardErrorTail(serverId));
        StatusChanged?.Invoke();
    }

    private sealed record CachedMcpSession(
        McpClient Client,
        IReadOnlyList<McpClientTool> Tools,
        DateTimeOffset ServerUpdatedAtUtc,
        HttpClient? OwnedHttpClient);

    private sealed record ConnectedMcpSession(McpClient Client, HttpClient? OwnedHttpClient);

    private sealed class TimeoutScope(CancellationToken token, CancellationTokenSource? source) : IDisposable
    {
        public CancellationToken Token { get; } = token;

        public void Dispose() => source?.Dispose();
    }
}
