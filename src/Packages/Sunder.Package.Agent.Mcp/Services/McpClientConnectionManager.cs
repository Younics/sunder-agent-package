using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Sunder.Package.Agent.Mcp.Services;

public sealed class McpClientConnectionManager(ILoggerFactory loggerFactory) : IAsyncDisposable
{
    private readonly ILogger<McpClientConnectionManager> _logger = loggerFactory.CreateLogger<McpClientConnectionManager>();
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly ConcurrentDictionary<(Guid SessionId, string ServerId), CachedMcpSession> _sessions = new();
    private readonly ConcurrentDictionary<(Guid SessionId, string ServerId), SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<(Guid SessionId, string ServerId), Task> _backgroundRefreshes = new();
    private volatile bool _disposed;

    public IReadOnlyList<McpClientTool>? GetCachedTools(Guid sessionId, ConfiguredMcpServerRecord server)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _sessions.TryGetValue((sessionId, server.ServerId), out var existing)
               && existing.ServerUpdatedAtUtc == server.UpdatedAtUtc
            ? existing.Tools
            : null;
    }

    public void RefreshToolsInBackground(
        Guid sessionId,
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        int effectiveTimeoutMilliseconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = (sessionId, server.ServerId);
        if (_backgroundRefreshes.TryGetValue(key, out var existing) && !existing.IsCompleted)
        {
            return;
        }

        _backgroundRefreshes[key] = Task.Run(async () =>
        {
            try
            {
                await GetToolsAsync(sessionId, server, headers, environmentVariables, effectiveTimeoutMilliseconds, CancellationToken.None);
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
        int? effectiveTimeoutMilliseconds,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = (sessionId, server.ServerId);
        if (_sessions.TryGetValue(key, out var existing) && IsSessionReusable(existing, server, effectiveTimeoutMilliseconds))
        {
            return existing.Tools;
        }

        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_sessions.TryGetValue(key, out existing) && IsSessionReusable(existing, server, effectiveTimeoutMilliseconds))
            {
                return existing.Tools;
            }

            await DisconnectCoreAsync(key, removeLock: false);

            using var timeoutScope = CreateTimeoutScope(effectiveTimeoutMilliseconds, cancellationToken);
            var connectedSession = await ConnectAsync(server, headers, environmentVariables, effectiveTimeoutMilliseconds, timeoutScope.Token);
            var tools = (await connectedSession.Client.ListToolsAsync(cancellationToken: timeoutScope.Token)).ToArray();
            _sessions[key] = new CachedMcpSession(
                connectedSession.Client,
                tools,
                effectiveTimeoutMilliseconds,
                server.UpdatedAtUtc,
                connectedSession.OwnedHttpClient);
            return tools;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect to MCP server '{ServerId}' for session {SessionId}.", server.ServerId, sessionId);
            return [];
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
        int? effectiveTimeoutMilliseconds,
        CancellationToken cancellationToken = default)
    {
        var tools = await GetToolsAsync(sessionId, server, headers, environmentVariables, effectiveTimeoutMilliseconds, cancellationToken);
        return tools.Count == 0 && !_sessions.TryGetValue((sessionId, server.ServerId), out var cached)
            ? null
            : _sessions.TryGetValue((sessionId, server.ServerId), out cached)
                ? cached.Client
                : null;
    }

    public async Task DisconnectSessionAsync(Guid sessionId)
    {
        var keys = _sessions.Keys.Where(key => key.SessionId == sessionId).ToArray();
        foreach (var key in keys)
        {
            await DisconnectCoreAsync(key);
        }
    }

    public async Task DisconnectServerAsync(string serverId)
    {
        var keys = _sessions.Keys.Where(key => string.Equals(key.ServerId, serverId, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var key in keys)
        {
            await DisconnectCoreAsync(key);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var key in _sessions.Keys.ToArray())
        {
            await DisconnectCoreAsync(key);
        }
    }

    private async Task<ConnectedMcpSession> ConnectAsync(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        int? effectiveTimeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        return server.TransportType switch
        {
            ConfiguredMcpTransportType.Stdio => await ConnectStdioAsync(server, environmentVariables, cancellationToken),
            ConfiguredMcpTransportType.HttpSse => await ConnectHttpAsync(server, headers, effectiveTimeoutMilliseconds, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported MCP transport '{server.TransportType}'."),
        };
    }

    private async Task<ConnectedMcpSession> ConnectStdioAsync(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> environmentVariables,
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
            },
            _loggerFactory);

        var client = await McpClient.CreateAsync(transport, new McpClientOptions(), _loggerFactory, cancellationToken);
        return new ConnectedMcpSession(client, OwnedHttpClient: null);
    }

    private async Task<ConnectedMcpSession> ConnectHttpAsync(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        int? effectiveTimeoutMilliseconds,
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
            AdditionalHeaders = headers.Count == 0 ? null : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase),
        };

        if (effectiveTimeoutMilliseconds is > 0)
        {
            options.ConnectionTimeout = TimeSpan.FromMilliseconds(effectiveTimeoutMilliseconds.Value);
        }

        var httpClient = new HttpClient();
        if (effectiveTimeoutMilliseconds is > 0)
        {
            httpClient.Timeout = TimeSpan.FromMilliseconds(effectiveTimeoutMilliseconds.Value);
        }

        var transport = new HttpClientTransport(options, httpClient, _loggerFactory);

        var client = await McpClient.CreateAsync(transport, new McpClientOptions(), _loggerFactory, cancellationToken);
        return new ConnectedMcpSession(client, httpClient);
    }

    private async Task DisconnectCoreAsync((Guid SessionId, string ServerId) key, bool removeLock = true)
    {
        if (_sessions.TryRemove(key, out var session))
        {
            try
            {
                await session.Client.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dispose MCP client for server '{ServerId}'.", key.ServerId);
            }

            try
            {
                session.OwnedHttpClient?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dispose MCP HTTP client for server '{ServerId}'.", key.ServerId);
            }
        }

        if (removeLock)
        {
            _locks.TryRemove(key, out _);
        }
    }

    private static bool IsSessionReusable(CachedMcpSession existing, ConfiguredMcpServerRecord server, int? effectiveTimeoutMilliseconds)
        => existing.ServerUpdatedAtUtc == server.UpdatedAtUtc
           && existing.EffectiveTimeoutMilliseconds == effectiveTimeoutMilliseconds;

    private TimeoutScope CreateTimeoutScope(int? effectiveTimeoutMilliseconds, CancellationToken cancellationToken)
    {
        if (effectiveTimeoutMilliseconds is not > 0)
        {
            return new TimeoutScope(cancellationToken, null);
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(effectiveTimeoutMilliseconds.Value);
        return new TimeoutScope(source.Token, source);
    }

    private sealed record CachedMcpSession(
        McpClient Client,
        IReadOnlyList<McpClientTool> Tools,
        int? EffectiveTimeoutMilliseconds,
        DateTimeOffset ServerUpdatedAtUtc,
        HttpClient? OwnedHttpClient);

    private sealed record ConnectedMcpSession(McpClient Client, HttpClient? OwnedHttpClient);

    private sealed class TimeoutScope(CancellationToken token, CancellationTokenSource? source) : IDisposable
    {
        public CancellationToken Token { get; } = token;

        public void Dispose() => source?.Dispose();
    }
}
