using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Mcp.Services;

public sealed class McpOAuthService(IPackageContext packageContext)
{
    internal const int PreferredCallbackPort = 1465;
    private const string CallbackPath = "/mcp/oauth/callback";
    private static readonly TimeSpan BrowserAuthorizationTimeout = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IPackageContext _packageContext = packageContext;
    private readonly ILoggerFactory _loggerFactory = packageContext.LoggerFactory;
    private readonly ILogger<McpOAuthService> _logger = packageContext.LoggerFactory.CreateLogger<McpOAuthService>();
    private readonly SemaphoreSlim _authorizationGate = new(1, 1);

    public Task<ClientOAuthOptions?> CreateClientOptionsAsync(
        ConfiguredMcpServerRecord server,
        bool allowInteractive,
        CancellationToken cancellationToken = default)
        => CreateClientOptionsAsync(
            server,
            allowInteractive,
            BuildRedirectUri(PreferredCallbackPort),
            callbackListener: null,
            cancellationToken);

    private async Task<ClientOAuthOptions?> CreateClientOptionsAsync(
        ConfiguredMcpServerRecord server,
        bool allowInteractive,
        Uri redirectUri,
        OAuthCallbackListener? callbackListener,
        CancellationToken cancellationToken)
    {
        if (!server.OAuthEnabled || string.IsNullOrWhiteSpace(server.EndpointUrl))
        {
            return null;
        }

        var registration = allowInteractive
            ? await ReadClientRegistrationAsync(server.ServerId, redirectUri, cancellationToken)
            : await ReadClientRegistrationAsync(server.ServerId, cancellationToken);
        var explicitClientSecret = await _packageContext.Secrets.GetSecretAsync(
            McpOAuthSecretKeys.ClientSecret(server.ServerId), cancellationToken);
        return new ClientOAuthOptions
        {
            RedirectUri = redirectUri,
            ClientId = string.IsNullOrWhiteSpace(server.OAuthClientId) ? registration?.ClientId : server.OAuthClientId,
            ClientSecret = string.IsNullOrWhiteSpace(explicitClientSecret) ? registration?.ClientSecret : explicitClientSecret,
            Scopes = server.OAuthScopes.Length == 0 ? null : server.OAuthScopes,
            TokenCache = new SecretTokenCache(_packageContext, server.ServerId),
            AuthorizationRedirectDelegate = allowInteractive
                ? (authorizationUri, actualRedirectUri, cancellationToken) => StartBrowserAuthorizationAsync(authorizationUri, actualRedirectUri, callbackListener, cancellationToken)
                : static (_, _, _) => Task.FromResult<string?>(null),
            DynamicClientRegistration = string.IsNullOrWhiteSpace(server.OAuthClientId)
                ? new DynamicClientRegistrationOptions
                {
                    ClientName = "Sunder",
                    ResponseDelegate = async (response, cancellationToken) =>
                    {
                        await SaveClientRegistrationAsync(response, server.ServerId, redirectUri, cancellationToken);
                    },
                }
                : null,
        };
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

    public async Task AuthorizeAsync(
        ConfiguredMcpServerRecord server,
        int? discoveryTimeoutMilliseconds,
        CancellationToken cancellationToken = default)
    {
        if (!server.OAuthEnabled)
        {
            throw new InvalidOperationException($"MCP server '{server.DisplayName}' is not configured for OAuth.");
        }

        if (string.IsNullOrWhiteSpace(server.EndpointUrl))
        {
            throw new InvalidOperationException($"MCP server '{server.DisplayName}' is missing an endpoint URL.");
        }

        await _authorizationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var callbackListener = StartCallbackListener(PreferredCallbackPort);
            var options = new HttpClientTransportOptions
            {
                Name = server.DisplayName,
                Endpoint = new Uri(server.EndpointUrl),
                TransportMode = HttpTransportMode.AutoDetect,
                ConnectionTimeout = ToSdkTimeout(discoveryTimeoutMilliseconds),
                OAuth = await CreateClientOptionsAsync(
                    server,
                    allowInteractive: true,
                    callbackListener.RedirectUri,
                    callbackListener,
                    cancellationToken),
            };
            using var httpClient = new HttpClient
            {
                Timeout = ToSdkTimeout(discoveryTimeoutMilliseconds),
            };
            var transport = new HttpClientTransport(options, httpClient, _loggerFactory);
            await using var client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions { InitializationTimeout = ToSdkTimeout(discoveryTimeoutMilliseconds) },
                _loggerFactory,
                cancellationToken).ConfigureAwait(false);
            await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _authorizationGate.Release();
        }
    }

    private Task<string?> StartBrowserAuthorizationAsync(
        Uri authorizationUri,
        Uri redirectUri,
        OAuthCallbackListener? callbackListener,
        CancellationToken cancellationToken)
    {
        if (callbackListener is null)
        {
            throw new InvalidOperationException("Sunder could not prepare an MCP OAuth callback listener.");
        }

        if (!Uri.Equals(callbackListener.RedirectUri, redirectUri))
        {
            throw new InvalidOperationException($"MCP OAuth callback redirect mismatch. Expected {callbackListener.RedirectUri}, received {redirectUri}.");
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(BrowserAuthorizationTimeout);
        return StartBrowserAuthorizationCoreAsync(authorizationUri, callbackListener, source);
    }

    private async Task<string?> StartBrowserAuthorizationCoreAsync(Uri authorizationUri, OAuthCallbackListener callbackListener, CancellationTokenSource cancellation)
    {
        using (cancellation)
        {
            using var stopRegistration = cancellation.Token.Register(() =>
            {
                try
                {
                    callbackListener.Stop();
                }
                catch
                {
                }
            });

            OpenBrowser(authorizationUri);
            try
            {
                var context = await callbackListener.GetContextAsync().ConfigureAwait(false);
                var code = context.Request.QueryString["code"];
                var error = context.Request.QueryString["error_description"] ?? context.Request.QueryString["error"];
                var success = !string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(error);
                await WriteCallbackResponseAsync(
                    context.Response,
                    success,
                    success
                        ? "Authorization complete. You can close this window and return to Sunder."
                        : string.IsNullOrWhiteSpace(error) ? "Authorization failed." : error).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(error))
                {
                    throw new InvalidOperationException(error);
                }

                return string.IsNullOrWhiteSpace(code) ? null : code;
            }
            catch (HttpListenerException) when (cancellation.IsCancellationRequested)
            {
                return null;
            }
            catch (ObjectDisposedException) when (cancellation.IsCancellationRequested)
            {
                return null;
            }
        }
    }

    private void OpenBrowser(Uri authorizationUri)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = authorizationUri.ToString(),
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open browser for MCP OAuth authorization.");
            throw new InvalidOperationException($"Open this URL to authorize the MCP server: {authorizationUri}", ex);
        }
    }

    private Task SaveClientRegistrationAsync(
        DynamicClientRegistrationResponse response,
        string serverId,
        Uri redirectUri,
        CancellationToken cancellationToken)
    {
        var registration = new StoredOAuthClientRegistration(response.ClientId, response.ClientSecret, redirectUri.ToString());
        return _packageContext.Secrets.SetSecretAsync(
            McpOAuthSecretKeys.ClientRegistration(serverId),
            JsonSerializer.Serialize(registration, JsonOptions),
            cancellationToken);
    }

    private async Task<StoredOAuthClientRegistration?> ReadClientRegistrationAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        var payload = await _packageContext.Secrets.GetSecretAsync(
            McpOAuthSecretKeys.ClientRegistration(serverId), cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredOAuthClientRegistration>(payload, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private async Task<StoredOAuthClientRegistration?> ReadClientRegistrationAsync(
        string serverId,
        Uri redirectUri,
        CancellationToken cancellationToken)
    {
        var registration = await ReadClientRegistrationAsync(serverId, cancellationToken);
        if (registration is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(registration.RedirectUri)
               || string.Equals(registration.RedirectUri, redirectUri.ToString(), StringComparison.OrdinalIgnoreCase)
            ? registration
            : null;
    }

    internal static OAuthCallbackListener StartCallbackListener(int preferredPort)
    {
        for (var port = preferredPort; port <= IPEndPoint.MaxPort; port++)
        {
            var listener = new HttpListener();
            var redirectUri = BuildRedirectUri(port);
            listener.Prefixes.Add(BuildListenerPrefix(redirectUri));
            try
            {
                listener.Start();
                return new OAuthCallbackListener(listener, redirectUri);
            }
            catch
            {
                listener.Close();
            }
        }

        throw new InvalidOperationException($"Sunder could not allocate a local MCP OAuth callback port at or above {preferredPort}.");
    }

    private static Uri BuildRedirectUri(int port)
        => new($"http://localhost:{port}{CallbackPath}");

    private static string BuildListenerPrefix(Uri redirectUri)
    {
        var builder = new UriBuilder(redirectUri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
        };
        var prefix = builder.Uri.ToString();
        return prefix.EndsWith("/", StringComparison.Ordinal) ? prefix : prefix + "/";
    }

    private static async Task WriteCallbackResponseAsync(HttpListenerResponse response, bool success, string message)
    {
        var title = WebUtility.HtmlEncode(success ? "Authorization complete" : "Authorization failed");
        var subtitle = WebUtility.HtmlEncode(message);
        var html = $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <title>{{title}}</title>
                <style>
                    body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: #15171a; color: #dedad3; font-family: system-ui, sans-serif; }
                    main { padding: 32px; border: 1px solid rgba(231,183,101,.36); border-radius: 18px; background: #1d2025; max-width: 520px; }
                    h1 { margin: 0 0 8px; font-size: 22px; }
                    p { margin: 0; color: #ccc7be; }
                </style>
            </head>
            <body><main><h1>{{title}}</h1><p>{{subtitle}}</p></main></body>
            </html>
            """;
        var bytes = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.OutputStream.Close();
    }

    private static TimeSpan ToSdkTimeout(int? timeoutMilliseconds)
        => timeoutMilliseconds is > 0
            ? TimeSpan.FromMilliseconds(timeoutMilliseconds.Value)
            : Timeout.InfiniteTimeSpan;

    internal sealed class OAuthCallbackListener(HttpListener listener, Uri redirectUri) : IDisposable
    {
        public Uri RedirectUri { get; } = redirectUri;

        public Task<HttpListenerContext> GetContextAsync() => listener.GetContextAsync();

        public void Stop() => listener.Stop();

        public void Dispose()
        {
            listener.Stop();
            listener.Close();
        }
    }

    private sealed record StoredOAuthClientRegistration(string? ClientId, string? ClientSecret, string? RedirectUri = null);

    private sealed class SecretTokenCache(IPackageContext packageContext, string serverId) : ITokenCache
    {
        public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await packageContext.Secrets.SetSecretAsync(
                McpOAuthSecretKeys.TokenCache(serverId),
                JsonSerializer.Serialize(tokens, JsonOptions),
                cancellationToken);
        }

        public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = await packageContext.Secrets.GetSecretAsync(
                McpOAuthSecretKeys.TokenCache(serverId), cancellationToken);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<TokenContainer>(payload, JsonOptions);
            }
            catch
            {
                return null;
            }
        }
    }
}
