using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Mcp.Services;

public sealed class McpOAuthService : IAsyncDisposable
{
    private static readonly Uri NonInteractiveRedirectUri = new("https://sunder.invalid/mcp/oauth/callback");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IPackageContext _packageContext;
    private readonly ILoggerFactory _loggerFactory;
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, McpOAuthFlow> _flowsBySession = new(StringComparer.Ordinal);
    private readonly Dictionary<string, McpOAuthFlow> _flowsByServer = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    public McpOAuthService(IPackageContext packageContext)
    {
        _packageContext = packageContext;
        _loggerFactory = packageContext.Logging.LoggerFactory;
    }

    public async Task<ClientOAuthOptions?> CreateClientOptionsAsync(
        ConfiguredMcpServerRecord server,
        bool allowInteractive,
        CancellationToken cancellationToken = default)
    {
        if (allowInteractive)
        {
            throw new InvalidOperationException("Interactive MCP OAuth must be started by the registered host callback handler.");
        }
        if (!server.OAuthEnabled || string.IsNullOrWhiteSpace(server.EndpointUrl)) return null;
        var registration = await ReadClientRegistrationAsync(server.ServerId, cancellationToken);
        var redirectUri = TryReadRedirectUri(registration) ?? NonInteractiveRedirectUri;
        return await CreateClientOptionsCoreAsync(
            server,
            redirectUri,
            static (_, _, _) => Task.FromResult<string?>(null),
            cancellationToken);
    }

    internal async Task<Uri> StartAuthorizationAsync(
        ConfiguredMcpServerRecord server,
        string callbackSessionId,
        Uri redirectUri,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!server.OAuthEnabled) throw new InvalidOperationException($"MCP server '{server.DisplayName}' is not configured for OAuth.");
        if (string.IsNullOrWhiteSpace(server.EndpointUrl)) throw new InvalidOperationException($"MCP server '{server.DisplayName}' is missing an endpoint URL.");

        McpOAuthFlow flow;
        lock (_syncRoot)
        {
            if (_flowsByServer.ContainsKey(server.ServerId))
            {
                throw new InvalidOperationException($"OAuth authorization is already in progress for MCP server '{server.DisplayName}'.");
            }
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            flow = new McpOAuthFlow(callbackSessionId, server.ServerId, redirectUri, lifetime);
            _flowsBySession.Add(callbackSessionId, flow);
            _flowsByServer.Add(server.ServerId, flow);
        }

        try
        {
            await _packageContext.Secrets.DeleteSecretAsync(McpOAuthSecretKeys.TokenCache(server.ServerId), cancellationToken);
            flow.RunTask = RunAuthorizationAsync(server, flow);
            var completed = await Task.WhenAny(flow.AuthorizationUri.Task, flow.RunTask).WaitAsync(cancellationToken);
            if (ReferenceEquals(completed, flow.RunTask))
            {
                await flow.RunTask;
                throw new InvalidOperationException("The MCP server did not request interactive authorization.");
            }
            return await flow.AuthorizationUri.Task;
        }
        catch
        {
            await CancelAsync(callbackSessionId);
            throw;
        }
    }

    internal async Task<McpOAuthCompletion> CompleteAsync(
        string callbackSessionId,
        IReadOnlyDictionary<string, string?> queryValues,
        CancellationToken cancellationToken)
    {
        var flow = GetFlow(callbackSessionId);
        var error = GetValue(queryValues, "error_description") ?? GetValue(queryValues, "error");
        var code = GetValue(queryValues, "code");
        if (!string.IsNullOrWhiteSpace(error))
        {
            flow.AuthorizationCode.TrySetException(new InvalidOperationException(error));
        }
        else if (string.IsNullOrWhiteSpace(code))
        {
            flow.AuthorizationCode.TrySetException(new InvalidOperationException("The MCP OAuth callback did not include an authorization code."));
        }
        else if (!flow.AuthorizationCode.TrySetResult(code))
        {
            return new McpOAuthCompletion(false, "The MCP OAuth callback was already completed.");
        }

        try
        {
            await flow.RunTask.WaitAsync(cancellationToken);
            return new McpOAuthCompletion(true, "MCP OAuth authorization completed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await flow.Lifetime.CancelAsync();
            try { await flow.RunTask; } catch { }
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new McpOAuthCompletion(false, exception.Message);
        }
        finally
        {
            if (RemoveFlow(flow)) flow.Lifetime.Dispose();
        }
    }

    internal async Task CancelAsync(string callbackSessionId)
    {
        McpOAuthFlow? flow;
        lock (_syncRoot)
        {
            _flowsBySession.TryGetValue(callbackSessionId, out flow);
        }
        if (flow is null) return;
        if (!RemoveFlow(flow)) return;
        try
        {
            await flow.Lifetime.CancelAsync();
            try { await flow.RunTask; } catch (OperationCanceledException) { } catch { }
        }
        finally
        {
            flow.Lifetime.Dispose();
        }
    }

    public async Task<bool> HasCachedAuthorizationAsync(string serverId, CancellationToken cancellationToken = default)
        => !string.IsNullOrWhiteSpace(await _packageContext.Secrets.GetSecretAsync(
            McpOAuthSecretKeys.TokenCache(serverId), cancellationToken));

    public async Task ClearAuthorizationAsync(string serverId, CancellationToken cancellationToken = default)
    {
        await _packageContext.Secrets.DeleteSecretAsync(McpOAuthSecretKeys.TokenCache(serverId), cancellationToken);
        await _packageContext.Secrets.DeleteSecretAsync(McpOAuthSecretKeys.ClientRegistration(serverId), cancellationToken);
        await _packageContext.Secrets.DeleteSecretAsync(McpOAuthSecretKeys.ClientSecret(serverId), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _shutdown.CancelAsync();
        McpOAuthFlow[] flows;
        lock (_syncRoot) flows = _flowsBySession.Values.ToArray();
        foreach (var flow in flows) await CancelAsync(flow.CallbackSessionId);
        _shutdown.Dispose();
    }

    private async Task RunAuthorizationAsync(ConfiguredMcpServerRecord server, McpOAuthFlow flow)
    {
        var options = new HttpClientTransportOptions
        {
            Name = server.DisplayName,
            Endpoint = new Uri(server.EndpointUrl!),
            TransportMode = HttpTransportMode.AutoDetect,
            ConnectionTimeout = Timeout.InfiniteTimeSpan,
            OAuth = await CreateClientOptionsCoreAsync(
                server,
                flow.RedirectUri,
                (authorizationUri, actualRedirectUri, cancellationToken) =>
                    HandleAuthorizationRedirectAsync(flow, authorizationUri, actualRedirectUri, cancellationToken),
                flow.Lifetime.Token),
        };
        using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var transport = new HttpClientTransport(options, httpClient, _loggerFactory);
        await using var client = await McpClient.CreateAsync(
            transport,
            new McpClientOptions { InitializationTimeout = Timeout.InfiniteTimeSpan },
            _loggerFactory,
            flow.Lifetime.Token).ConfigureAwait(false);
        await client.ListToolsAsync(cancellationToken: flow.Lifetime.Token).ConfigureAwait(false);
    }

    private static async Task<string?> HandleAuthorizationRedirectAsync(
        McpOAuthFlow flow,
        Uri authorizationUri,
        Uri actualRedirectUri,
        CancellationToken cancellationToken)
    {
        if (!Uri.Equals(flow.RedirectUri, actualRedirectUri))
        {
            throw new InvalidOperationException("The MCP SDK returned a callback redirect URI that did not match the host-owned URI.");
        }
        if (!flow.AuthorizationUri.TrySetResult(authorizationUri))
        {
            throw new InvalidOperationException("The MCP OAuth flow requested more than one authorization redirect.");
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, flow.Lifetime.Token);
        return await flow.AuthorizationCode.Task.WaitAsync(linked.Token).ConfigureAwait(false);
    }

    private async Task<ClientOAuthOptions> CreateClientOptionsCoreAsync(
        ConfiguredMcpServerRecord server,
        Uri redirectUri,
        AuthorizationRedirectDelegate redirect,
        CancellationToken cancellationToken)
    {
        var registration = await ReadClientRegistrationAsync(server.ServerId, redirectUri, cancellationToken);
        var explicitClientSecret = await _packageContext.Secrets.GetSecretAsync(
            McpOAuthSecretKeys.ClientSecret(server.ServerId), cancellationToken);
        return new ClientOAuthOptions
        {
            RedirectUri = redirectUri,
            ClientId = string.IsNullOrWhiteSpace(server.OAuthClientId) ? registration?.ClientId : server.OAuthClientId,
            ClientSecret = string.IsNullOrWhiteSpace(explicitClientSecret) ? registration?.ClientSecret : explicitClientSecret,
            Scopes = server.OAuthScopes.Length == 0 ? null : server.OAuthScopes,
            TokenCache = new SecretTokenCache(_packageContext, server.ServerId),
            AuthorizationRedirectDelegate = redirect,
            DynamicClientRegistration = string.IsNullOrWhiteSpace(server.OAuthClientId)
                ? new DynamicClientRegistrationOptions
                {
                    ClientName = "Sunder",
                    ResponseDelegate = (response, token) => SaveClientRegistrationAsync(response, server.ServerId, redirectUri, token),
                }
                : null,
        };
    }

    private Task SaveClientRegistrationAsync(
        DynamicClientRegistrationResponse response,
        string serverId,
        Uri redirectUri,
        CancellationToken cancellationToken)
        => _packageContext.Secrets.SetSecretAsync(
            McpOAuthSecretKeys.ClientRegistration(serverId),
            JsonSerializer.Serialize(new StoredOAuthClientRegistration(response.ClientId, response.ClientSecret, redirectUri.ToString()), JsonOptions),
            cancellationToken);

    private async Task<StoredOAuthClientRegistration?> ReadClientRegistrationAsync(string serverId, CancellationToken cancellationToken)
    {
        var payload = await _packageContext.Secrets.GetSecretAsync(McpOAuthSecretKeys.ClientRegistration(serverId), cancellationToken);
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try { return JsonSerializer.Deserialize<StoredOAuthClientRegistration>(payload, JsonOptions); }
        catch { return null; }
    }

    private async Task<StoredOAuthClientRegistration?> ReadClientRegistrationAsync(
        string serverId,
        Uri redirectUri,
        CancellationToken cancellationToken)
    {
        var registration = await ReadClientRegistrationAsync(serverId, cancellationToken);
        return registration is not null
               && string.Equals(registration.RedirectUri, redirectUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase)
            ? registration
            : null;
    }

    private McpOAuthFlow GetFlow(string callbackSessionId)
    {
        lock (_syncRoot)
        {
            return _flowsBySession.TryGetValue(callbackSessionId, out var flow)
                ? flow
                : throw new InvalidOperationException("The MCP OAuth callback session is no longer active.");
        }
    }

    private bool RemoveFlow(McpOAuthFlow flow)
    {
        lock (_syncRoot)
        {
            if (_flowsBySession.TryGetValue(flow.CallbackSessionId, out var current) && ReferenceEquals(current, flow))
            {
                _flowsBySession.Remove(flow.CallbackSessionId);
                _flowsByServer.Remove(flow.ServerId);
                return true;
            }
            return false;
        }
    }

    private static Uri? TryReadRedirectUri(StoredOAuthClientRegistration? registration)
        => Uri.TryCreate(registration?.RedirectUri, UriKind.Absolute, out var redirectUri) ? redirectUri : null;

    private static string? GetValue(IReadOnlyDictionary<string, string?> values, string key)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private sealed class McpOAuthFlow(
        string callbackSessionId,
        string serverId,
        Uri redirectUri,
        CancellationTokenSource lifetime)
    {
        public string CallbackSessionId { get; } = callbackSessionId;
        public string ServerId { get; } = serverId;
        public Uri RedirectUri { get; } = redirectUri;
        public CancellationTokenSource Lifetime { get; } = lifetime;
        public TaskCompletionSource<Uri> AuthorizationUri { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> AuthorizationCode { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task RunTask { get; set; } = Task.CompletedTask;
    }

    private sealed record StoredOAuthClientRegistration(string? ClientId, string? ClientSecret, string? RedirectUri);

    private sealed class SecretTokenCache(IPackageContext packageContext, string serverId) : ITokenCache
    {
        public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
            => await packageContext.Secrets.SetSecretAsync(
                McpOAuthSecretKeys.TokenCache(serverId),
                JsonSerializer.Serialize(tokens, JsonOptions),
                cancellationToken);

        public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
        {
            var payload = await packageContext.Secrets.GetSecretAsync(McpOAuthSecretKeys.TokenCache(serverId), cancellationToken);
            if (string.IsNullOrWhiteSpace(payload)) return null;
            try { return JsonSerializer.Deserialize<TokenContainer>(payload, JsonOptions); }
            catch { return null; }
        }
    }
}

internal sealed record McpOAuthCompletion(bool Success, string Message);
