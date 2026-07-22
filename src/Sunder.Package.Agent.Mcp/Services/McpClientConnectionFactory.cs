using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Sunder.Package.Agent.Mcp.Services;

internal interface IMcpClientConnectionFactory
{
    Task<IMcpClientConnection> ConnectAsync(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        int? discoveryTimeoutMilliseconds,
        Action<string> standardError,
        CancellationToken cancellationToken);
}

internal interface IMcpClientConnection : IAsyncDisposable
{
    McpClient? Client { get; }

    Task Completion { get; }

    Task<IReadOnlyList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken);
}

internal sealed class McpClientConnectionFactory(
    ILoggerFactory loggerFactory,
    McpOAuthService? oauthService) : IMcpClientConnectionFactory
{
    private readonly ILogger<McpClientConnectionManager> _logger = loggerFactory.CreateLogger<McpClientConnectionManager>();

    public async Task<IMcpClientConnection> ConnectAsync(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> environmentVariables,
        int? discoveryTimeoutMilliseconds,
        Action<string> standardError,
        CancellationToken cancellationToken)
    {
        return server.TransportType switch
        {
            ConfiguredMcpTransportType.Stdio => await ConnectStdioAsync(server, environmentVariables, discoveryTimeoutMilliseconds, standardError, cancellationToken),
            ConfiguredMcpTransportType.HttpSse => await ConnectHttpAsync(server, headers, discoveryTimeoutMilliseconds, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported MCP transport '{server.TransportType}'."),
        };
    }

    private async Task<IMcpClientConnection> ConnectStdioAsync(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> environmentVariables,
        int? timeoutMilliseconds,
        Action<string> standardError,
        CancellationToken cancellationToken)
    {
        if (server.CommandParts.Length == 0)
        {
            throw new InvalidOperationException($"MCP server '{server.DisplayName}' is missing a command.");
        }

        var launch = await McpCommandResolver.ResolveAsync(
            server.CommandParts[0],
            server.WorkingDirectory,
            environmentVariables,
            _logger,
            cancellationToken).ConfigureAwait(false);
        var transport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = server.DisplayName,
                Command = launch.Command,
                Arguments = server.CommandParts.Skip(1).ToArray(),
                WorkingDirectory = launch.WorkingDirectory,
                EnvironmentVariables = launch.EnvironmentVariables?.ToDictionary(item => item.Key, item => (string?)item.Value, StringComparer.OrdinalIgnoreCase),
                StandardErrorLines = standardError,
            },
            loggerFactory);
        var client = await McpClient.CreateAsync(transport, CreateClientOptions(timeoutMilliseconds), loggerFactory, cancellationToken);
        return new SdkMcpClientConnection(client, ownedHttpClient: null);
    }

    private async Task<IMcpClientConnection> ConnectHttpAsync(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string> headers,
        int? timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        McpTransportSecurity.ValidateRemoteEndpoint(server, headers);
        if (string.IsNullOrWhiteSpace(server.EndpointUrl))
        {
            throw new InvalidOperationException($"MCP server '{server.DisplayName}' is missing an endpoint URL.");
        }

        var options = new HttpClientTransportOptions
        {
            Name = server.DisplayName,
            Endpoint = new Uri(server.EndpointUrl),
            TransportMode = HttpTransportMode.AutoDetect,
            ConnectionTimeout = ToSdkTimeout(timeoutMilliseconds),
            AdditionalHeaders = headers.Count == 0 ? null : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase),
            OAuth = oauthService is null
                ? null
                : await oauthService.CreateClientOptionsAsync(server, allowInteractive: false, cancellationToken),
        };
        var httpClient = new HttpClient { Timeout = ToSdkTimeout(timeoutMilliseconds) };
        try
        {
            var transport = new HttpClientTransport(options, httpClient, loggerFactory);
            var client = await McpClient.CreateAsync(transport, CreateClientOptions(timeoutMilliseconds), loggerFactory, cancellationToken);
            return new SdkMcpClientConnection(client, httpClient);
        }
        catch
        {
            httpClient.Dispose();
            throw;
        }
    }

    private static McpClientOptions CreateClientOptions(int? timeoutMilliseconds)
        => new() { InitializationTimeout = ToSdkTimeout(timeoutMilliseconds) };

    private static TimeSpan ToSdkTimeout(int? timeoutMilliseconds)
        => TimeSpan.FromMilliseconds(timeoutMilliseconds is > 0
            ? Math.Min(timeoutMilliseconds.Value, McpTimeoutResolver.MaximumToolTimeoutMilliseconds)
            : McpTimeoutResolver.DefaultDiscoveryTimeoutMilliseconds);

    private sealed class SdkMcpClientConnection(McpClient client, HttpClient? ownedHttpClient) : IMcpClientConnection
    {
        public McpClient? Client => client;

        public Task Completion => client.Completion;

        public async Task<IReadOnlyList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken)
            => (await client.ListToolsAsync(cancellationToken: cancellationToken)).ToArray();

        public async ValueTask DisposeAsync()
        {
            try
            {
                await client.DisposeAsync();
            }
            finally
            {
                ownedHttpClient?.Dispose();
            }
        }
    }
}
