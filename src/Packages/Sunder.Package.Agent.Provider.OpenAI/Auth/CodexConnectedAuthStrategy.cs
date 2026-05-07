using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.OpenAI.Auth;

public sealed class CodexConnectedAuthStrategy(IPackageContext packageContext)
{
    private const string SessionSecretKey = "auth.codex.session";
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string AuthorizeUrl = "https://auth.openai.com/oauth/authorize";
    private const string TokenUrl = "https://auth.openai.com/oauth/token";
    private const string RedirectUri = "http://localhost:1455/auth/callback";
    private const string Scope = "openid profile email offline_access";

    private readonly IPackageContext _packageContext = packageContext;
    private readonly Dictionary<string, PendingBrowserAuth> _pendingBrowserAuth = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    public string ModeId { get; } = "codex-connected";

    public OpenAiCodexSession? GetCachedSession()
    {
        var payload = _packageContext.Secrets.GetSecret(SessionSecretKey);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<OpenAiCodexSession>(payload);
        }
        catch
        {
            return null;
        }
    }

    public async Task<OpenAiCodexSession> EnsureAuthenticatedAsync(CancellationToken cancellationToken = default)
        => await EnsureAuthenticatedAsync(allowInteractive: true, cancellationToken);

    public async Task<OpenAiCodexSession?> TryEnsureAuthenticatedSilentlyAsync(CancellationToken cancellationToken = default)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            var cached = GetCachedSession();
            if (cached is null)
            {
                return null;
            }

            if (cached.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return cached;
            }

            var refreshed = await RefreshCoreAsync(cached.RefreshToken, clearSessionOnTerminalFailure: true, cancellationToken);
            if (refreshed is null)
            {
                return null;
            }

            SaveSession(refreshed);
            return refreshed;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task<OpenAiCodexSession> EnsureAuthenticatedAsync(bool allowInteractive, CancellationToken cancellationToken = default)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            var cached = GetCachedSession();
            if (cached is not null && cached.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return cached;
            }

            if (cached is not null)
            {
                var refreshed = await RefreshCoreAsync(cached.RefreshToken, clearSessionOnTerminalFailure: true, cancellationToken);
                if (refreshed is not null)
                {
                    SaveSession(refreshed);
                    return refreshed;
                }
            }

            if (!allowInteractive)
            {
                throw new InvalidOperationException("OpenAI Codex session could not be refreshed silently.");
            }

            var authenticated = await SignInWithBrowserAsync(cancellationToken);
            SaveSession(authenticated);
            return authenticated;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task<OpenAiCodexSession?> TryRefreshSessionAsync(OpenAiCodexSession? expectedSession, CancellationToken cancellationToken = default)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            var cached = GetCachedSession();
            if (expectedSession is not null
                && cached is not null
                && !string.Equals(cached.AccessToken, expectedSession.AccessToken, StringComparison.Ordinal))
            {
                return cached;
            }

            var refreshToken = cached?.RefreshToken ?? expectedSession?.RefreshToken;
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                return null;
            }

            var refreshed = await RefreshCoreAsync(refreshToken, clearSessionOnTerminalFailure: true, cancellationToken);
            if (refreshed is null)
            {
                return null;
            }

            SaveSession(refreshed);
            return refreshed;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private void SaveSession(OpenAiCodexSession session)
    {
        _packageContext.Secrets.SetSecret(SessionSecretKey, JsonSerializer.Serialize(session));
    }

    public void ClearSession()
    {
        _packageContext.Secrets.DeleteSecret(SessionSecretKey);
    }

    private async Task<OpenAiCodexSession?> RefreshCoreAsync(
        string refreshToken,
        bool clearSessionOnTerminalFailure,
        CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient();
        using var response = await httpClient.PostAsync(
            TokenUrl,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = ClientId,
            }),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            if (clearSessionOnTerminalFailure && IsTerminalRefreshFailure(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken)))
            {
                _packageContext.Secrets.DeleteSecret(SessionSecretKey);
            }

            return null;
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        return TryParseTokenResponse(payload);
    }

    public string CreateAuthorizationUrl(string authSessionId, Uri callbackUri)
    {
        var state = authSessionId;
        var verifier = CreatePkceVerifier();
        var challenge = CreatePkceChallenge(verifier);
        _pendingBrowserAuth[authSessionId] = new PendingBrowserAuth(verifier, callbackUri.ToString());
        return BuildAuthorizationUrl(state, challenge, callbackUri.ToString());
    }

    public async Task<OpenAiCodexSession> CompleteAuthorizationAsync(
        string authSessionId,
        IReadOnlyDictionary<string, string?> queryValues,
        CancellationToken cancellationToken = default)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            if (!_pendingBrowserAuth.TryGetValue(authSessionId, out var pending))
            {
                throw new InvalidOperationException("No pending OpenAI browser auth session was found.");
            }

            if (queryValues.TryGetValue("error", out var error) && !string.IsNullOrWhiteSpace(error))
            {
                queryValues.TryGetValue("error_description", out var errorDescription);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorDescription) ? error : errorDescription);
            }

            if (!queryValues.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            {
                throw new InvalidOperationException("OpenAI browser sign-in did not return an authorization code.");
            }

            var session = await ExchangeAuthorizationCodeAsync(code, pending.Verifier, pending.RedirectUri, cancellationToken);
            SaveSession(session);
            return session;
        }
        finally
        {
            _pendingBrowserAuth.Remove(authSessionId);
            _sessionGate.Release();
        }
    }

    private async Task<OpenAiCodexSession> SignInWithBrowserAsync(CancellationToken cancellationToken)
    {
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var verifier = CreatePkceVerifier();
        var challenge = CreatePkceChallenge(verifier);

        using var listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:1455/auth/callback/");
        listener.Start();

        var authorizationUrl = BuildAuthorizationUrl(state, challenge, RedirectUri);
        OpenBrowser(authorizationUrl);

        using var registration = cancellationToken.Register(() =>
        {
            try { listener.Stop(); } catch { }
        });

        var context = await listener.GetContextAsync();
        var returnedState = context.Request.QueryString["state"];
        var code = context.Request.QueryString["code"];

        await WriteBrowserCompletionAsync(context.Response, returnedState == state && !string.IsNullOrWhiteSpace(code));

        if (!string.Equals(returnedState, state, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("OpenAI Codex browser sign-in failed or returned an invalid state.");
        }

        return await ExchangeAuthorizationCodeAsync(code, verifier, RedirectUri, cancellationToken);
    }

    private async Task<OpenAiCodexSession> ExchangeAuthorizationCodeAsync(
        string code,
        string verifier,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient();
        using var response = await httpClient.PostAsync(
            TokenUrl,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = ClientId,
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["redirect_uri"] = redirectUri,
            }),
            cancellationToken);

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        return TryParseTokenResponse(payload) ?? throw new InvalidOperationException("OpenAI Codex token response was missing required fields.");
    }

    private static string BuildAuthorizationUrl(string state, string challenge, string redirectUri)
    {
        var uriBuilder = new UriBuilder(AuthorizeUrl);
        uriBuilder.Query = string.Join("&", new[]
        {
            "response_type=code",
            $"client_id={Uri.EscapeDataString(ClientId)}",
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
            $"scope={Uri.EscapeDataString(Scope)}",
            $"code_challenge={Uri.EscapeDataString(challenge)}",
            "code_challenge_method=S256",
            $"state={Uri.EscapeDataString(state)}",
            "id_token_add_organizations=true",
            "codex_cli_simplified_flow=true",
            "originator=sunder",
        });

        return uriBuilder.ToString();
    }

    private static void OpenBrowser(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static async Task WriteBrowserCompletionAsync(HttpListenerResponse response, bool success)
    {
        var html = BuildBrowserCompletionPage(success);
        var bytes = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.OutputStream.Close();
    }

    private static string BuildBrowserCompletionPage(bool success)
    {
        var title = WebUtility.HtmlEncode(success ? "OpenAI sign-in complete." : "OpenAI sign-in failed.");
        var subtitle = WebUtility.HtmlEncode(success
            ? "You can close this window and return to Sunder."
            : "You can close this window and retry from Sunder.");

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <title>{{title}}</title>
                <link rel="preconnect" href="https://fonts.googleapis.com" />
                <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin />
                <link href="https://fonts.googleapis.com/css2?family=IBM+Plex+Sans:wght@400;500;600;700&display=swap" rel="stylesheet" />
                <style>
                    :root {
                        color-scheme: dark;
                        --bg: #080b10;
                        --text: #f2efe8;
                        --muted: #9ea8b7;
                        --accent-strong: #ffcf7d;
                        font-family: "IBM Plex Sans", system-ui, sans-serif;
                    }

                    * {
                        box-sizing: border-box;
                    }

                    body {
                        margin: 0;
                        min-height: 100vh;
                        background:
                            radial-gradient(circle at 20% 10%, rgba(255, 180, 76, 0.18), transparent 28rem),
                            radial-gradient(circle at 85% 0%, rgba(110, 231, 183, 0.12), transparent 24rem),
                            linear-gradient(180deg, #0d1118 0%, var(--bg) 42rem);
                        color: var(--text);
                    }

                    .boot-shell {
                        display: flex;
                        align-items: center;
                        justify-content: center;
                        gap: 14px;
                        min-height: 100vh;
                    }

                    .boot-mark {
                        display: grid;
                        width: 44px;
                        height: 44px;
                        place-items: center;
                        border: 1px solid rgba(255, 180, 76, 0.46);
                        border-radius: 14px;
                        background: linear-gradient(135deg, rgba(255, 180, 76, 0.2), rgba(255, 255, 255, 0.04));
                        color: var(--accent-strong);
                        font-weight: 800;
                    }

                    .boot-title {
                        font-weight: 800;
                    }

                    .boot-subtitle {
                        color: var(--muted);
                    }
                </style>
            </head>
            <body>
                <main class="boot-shell">
                    <div class="boot-mark">S</div>
                    <div>
                        <div class="boot-title">{{title}}</div>
                        <div class="boot-subtitle">{{subtitle}}</div>
                    </div>
                </main>
            </body>
            </html>
            """;
    }

    private static string CreatePkceVerifier()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string CreatePkceChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static OpenAiCodexSession? TryParseTokenResponse(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        if (!root.TryGetProperty("access_token", out var accessTokenElement)
            || !root.TryGetProperty("refresh_token", out var refreshTokenElement)
            || !root.TryGetProperty("expires_in", out var expiresInElement)
            || accessTokenElement.ValueKind != JsonValueKind.String
            || refreshTokenElement.ValueKind != JsonValueKind.String
            || expiresInElement.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var accessToken = accessTokenElement.GetString()!;
        var chatGptAccountId = root.TryGetProperty("id_token", out var idTokenElement) && idTokenElement.ValueKind == JsonValueKind.String
            ? ExtractChatGptAccountId(idTokenElement.GetString()!) ?? ExtractChatGptAccountId(accessToken)
            : ExtractChatGptAccountId(accessToken);
        if (string.IsNullOrWhiteSpace(chatGptAccountId))
        {
            return null;
        }

        return new OpenAiCodexSession(
            accessToken,
            refreshTokenElement.GetString()!,
            DateTimeOffset.UtcNow.AddSeconds(expiresInElement.GetInt32()),
            chatGptAccountId);
    }

    internal static string? ExtractChatGptAccountId(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            var payloadBytes = Convert.FromBase64String(PadBase64(parts[1].Replace('-', '+').Replace('_', '/')));
            using var payloadDocument = JsonDocument.Parse(payloadBytes);
            return ExtractChatGptAccountId(payloadDocument.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractChatGptAccountId(JsonElement claims)
    {
        if (claims.TryGetProperty("chatgpt_account_id", out var topLevelAccountIdElement)
            && topLevelAccountIdElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(topLevelAccountIdElement.GetString()))
        {
            return topLevelAccountIdElement.GetString();
        }

        if (claims.TryGetProperty("https://api.openai.com/auth", out var authClaimElement)
            && authClaimElement.TryGetProperty("chatgpt_account_id", out var authAccountIdElement)
            && authAccountIdElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(authAccountIdElement.GetString()))
        {
            return authAccountIdElement.GetString();
        }

        if (claims.TryGetProperty("organizations", out var organizationsElement)
            && organizationsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var organizationElement in organizationsElement.EnumerateArray())
            {
                if (organizationElement.TryGetProperty("id", out var organizationIdElement)
                    && organizationIdElement.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(organizationIdElement.GetString()))
                {
                    return organizationIdElement.GetString();
                }
            }
        }

        return null;
    }

    private static string PadBase64(string value)
        => value.PadRight(value.Length + (4 - value.Length % 4) % 4, '=');

    private static bool IsTerminalRefreshFailure(HttpStatusCode statusCode, string payload)
    {
        if (statusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            return payload.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)
                   || payload.Contains("invalid refresh", StringComparison.OrdinalIgnoreCase)
                   || payload.Contains("refresh token", StringComparison.OrdinalIgnoreCase)
                   || payload.Contains("expired", StringComparison.OrdinalIgnoreCase)
                   || payload.Contains("revoked", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private sealed record PendingBrowserAuth(string Verifier, string RedirectUri);
}
