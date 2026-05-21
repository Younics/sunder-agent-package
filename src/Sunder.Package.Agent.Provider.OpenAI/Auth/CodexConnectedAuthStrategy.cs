using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Provider.OpenAI.Auth;

public sealed class CodexConnectedAuthStrategy(IPackageContext packageContext)
{
    private const string SessionSecretKey = "auth.codex.session";
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string AuthorizeUrl = "https://auth.openai.com/oauth/authorize";
    private const string TokenUrl = "https://auth.openai.com/oauth/token";
    private const int PreferredCallbackPort = 1455;
    private const int FallbackCallbackPort = 1457;
    private const string CallbackPath = "/auth/callback";
    private const string RedirectUri = "http://localhost:1455/auth/callback";
    private const string Scope = "openid profile email offline_access api.connectors.read api.connectors.invoke";

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

            LogAuthEvent(
                PackageLogLevel.Information,
                "openai.codex.auth.silent.refresh_required",
                "Cached Codex session is expired or near expiry; refreshing silently.",
                attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["auth.expires_at"] = cached.ExpiresAtUtc,
                });
            var refreshed = await RefreshCoreAsync(cached.RefreshToken, clearSessionOnTerminalFailure: true, cancellationToken);
            if (refreshed is null)
            {
                LogAuthEvent(
                    PackageLogLevel.Warning,
                    "openai.codex.auth.silent.refresh_failed",
                    "Cached Codex session could not be refreshed silently.");
                return null;
            }

            SaveSession(refreshed);
            LogSessionSaved("silent_refresh", refreshed);
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
                LogAuthEvent(
                    PackageLogLevel.Information,
                    "openai.codex.auth.refresh_required",
                    "Cached Codex session is expired or near expiry; refreshing.",
                    attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["auth.allow_interactive"] = allowInteractive,
                        ["auth.expires_at"] = cached.ExpiresAtUtc,
                    });
                var refreshed = await RefreshCoreAsync(cached.RefreshToken, clearSessionOnTerminalFailure: true, cancellationToken);
                if (refreshed is not null)
                {
                    SaveSession(refreshed);
                    LogSessionSaved("refresh", refreshed);
                    return refreshed;
                }
            }

            if (!allowInteractive)
            {
                throw new InvalidOperationException("OpenAI Codex session could not be refreshed silently.");
            }

            LogAuthEvent(
                PackageLogLevel.Information,
                "openai.codex.auth.interactive.start",
                "Starting interactive Codex browser authorization.");
            var authenticated = await SignInWithBrowserAsync(cancellationToken);
            SaveSession(authenticated);
            LogSessionSaved("interactive", authenticated);
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
                LogAuthEvent(
                    PackageLogLevel.Warning,
                    "openai.codex.auth.refresh.no_token",
                    "No Codex refresh token is available for silent refresh.");
                return null;
            }

            var refreshed = await RefreshCoreAsync(refreshToken, clearSessionOnTerminalFailure: true, cancellationToken);
            if (refreshed is null)
            {
                return null;
            }

            SaveSession(refreshed);
            LogSessionSaved("explicit_refresh", refreshed);
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
        var stopwatch = Stopwatch.StartNew();
        LogAuthEvent(
            PackageLogLevel.Information,
            "openai.codex.auth.refresh.start",
            "Refreshing Codex auth session.",
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["network.address_family"] = CodexHttpClientFactory.NetworkAddressFamily,
            });
        try
        {
            using var httpClient = CodexHttpClientFactory.CreateAuthClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                    ["client_id"] = ClientId,
                }),
            };
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            LogAuthEvent(
                response.IsSuccessStatusCode ? PackageLogLevel.Debug : PackageLogLevel.Warning,
                "openai.codex.auth.refresh.headers_received",
                $"{(int)response.StatusCode} {response.ReasonPhrase}",
                stopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["http.status_code"] = (int)response.StatusCode,
                    ["http.reason_phrase"] = response.ReasonPhrase,
                    ["network.address_family"] = CodexHttpClientFactory.NetworkAddressFamily,
                });

            if (!response.IsSuccessStatusCode)
            {
                if (clearSessionOnTerminalFailure && IsTerminalRefreshFailure(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken)))
                {
                    _packageContext.Secrets.DeleteSecret(SessionSecretKey);
                    LogAuthEvent(
                        PackageLogLevel.Warning,
                        "openai.codex.auth.refresh.terminal_failure",
                        "Codex refresh failed terminally; cached session was removed.",
                        stopwatch.ElapsedMilliseconds,
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["http.status_code"] = (int)response.StatusCode,
                        });
                }

                return null;
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            var session = TryParseTokenResponse(payload);
            LogAuthEvent(
                session is null ? PackageLogLevel.Warning : PackageLogLevel.Information,
                session is null ? "openai.codex.auth.refresh.parse_failed" : "openai.codex.auth.refresh.completed",
                session is null
                    ? "Codex refresh response was missing required session fields."
                    : "Codex auth session refreshed.",
                stopwatch.ElapsedMilliseconds,
                session is null
                    ? null
                    : new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["auth.expires_at"] = session.ExpiresAtUtc,
                    });
            return session;
        }
        catch (OperationCanceledException ex)
        {
            LogAuthEvent(
                PackageLogLevel.Warning,
                "openai.codex.auth.refresh.canceled",
                cancellationToken.IsCancellationRequested
                    ? "Codex auth session refresh was canceled by the caller."
                    : "Codex auth session refresh was canceled or timed out before completion.",
                stopwatch.ElapsedMilliseconds,
                exception: ex);
            throw;
        }
        catch (Exception ex)
        {
            LogAuthEvent(
                PackageLogLevel.Error,
                "openai.codex.auth.refresh.failed",
                "Codex auth session refresh failed.",
                stopwatch.ElapsedMilliseconds,
                exception: ex);
            throw;
        }
    }

    public string CreateAuthorizationUrl(string authSessionId, Uri callbackUri)
    {
        var state = authSessionId;
        var verifier = CreatePkceVerifier();
        var challenge = CreatePkceChallenge(verifier);
        _pendingBrowserAuth[authSessionId] = new PendingBrowserAuth(verifier, callbackUri.ToString());
        LogAuthEvent(
            PackageLogLevel.Information,
            "openai.codex.auth.browser.start",
            "OpenAI browser authorization URL was created.",
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["auth.flow"] = "runtime_callback",
                ["auth.callback_uri"] = callbackUri.GetLeftPart(UriPartial.Path),
            });
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
            LogAuthEvent(
                PackageLogLevel.Information,
                "openai.codex.auth.callback.received",
                "OpenAI browser authorization callback was received.",
                attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["auth.flow"] = "runtime_callback",
                    ["auth.has_code"] = queryValues.TryGetValue("code", out var codeValue) && !string.IsNullOrWhiteSpace(codeValue),
                    ["auth.has_error"] = queryValues.TryGetValue("error", out var errorValue) && !string.IsNullOrWhiteSpace(errorValue),
                });

            if (!_pendingBrowserAuth.TryGetValue(authSessionId, out var pending))
            {
                LogAuthEvent(
                    PackageLogLevel.Warning,
                    "openai.codex.auth.callback.no_pending_session",
                    "OpenAI browser authorization callback did not match a pending session.",
                    attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["auth.flow"] = "runtime_callback",
                    });
                throw new InvalidOperationException("No pending OpenAI browser auth session was found.");
            }

            if (queryValues.TryGetValue("error", out var error) && !string.IsNullOrWhiteSpace(error))
            {
                queryValues.TryGetValue("error_description", out var errorDescription);
                LogAuthEvent(
                    PackageLogLevel.Warning,
                    "openai.codex.auth.callback.error",
                    "OpenAI browser authorization callback reported an error.",
                    attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["auth.flow"] = "runtime_callback",
                        ["auth.error"] = error,
                    });
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorDescription) ? error : errorDescription);
            }

            if (!queryValues.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            {
                LogAuthEvent(
                    PackageLogLevel.Warning,
                    "openai.codex.auth.callback.missing_code",
                    "OpenAI browser authorization callback did not include an authorization code.",
                    attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["auth.flow"] = "runtime_callback",
                    });
                throw new InvalidOperationException("OpenAI browser sign-in did not return an authorization code.");
            }

            var session = await ExchangeAuthorizationCodeAsync(code, pending.Verifier, pending.RedirectUri, cancellationToken);
            SaveSession(session);
            LogSessionSaved("runtime_callback", session);
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

        HttpListener listener;
        string redirectUri;
        LogAuthEvent(
            PackageLogLevel.Information,
            "openai.codex.auth.listener.start",
            "Starting local OpenAI browser authorization callback listener.",
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["auth.flow"] = "direct_listener",
                ["auth.callback_uri"] = RedirectUri,
                ["auth.callback_fallback_uri"] = CreateRedirectUri(FallbackCallbackPort),
            });
        try
        {
            listener = StartCallbackListener(out redirectUri);
        }
        catch (Exception ex)
        {
            LogAuthEvent(
                PackageLogLevel.Error,
                "openai.codex.auth.listener.start_failed",
                "Failed to start local OpenAI browser authorization callback listener.",
                attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["auth.flow"] = "direct_listener",
                },
                exception: ex);
            throw;
        }
        using var listenerRegistration = listener;
        LogAuthEvent(
            PackageLogLevel.Debug,
            "openai.codex.auth.listener.started",
            "Local OpenAI browser authorization callback listener started.",
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["auth.flow"] = "direct_listener",
                ["auth.callback_uri"] = redirectUri,
            });

        var authorizationUrl = BuildAuthorizationUrl(state, challenge, redirectUri);
        try
        {
            OpenBrowser(authorizationUrl);
            LogAuthEvent(
                PackageLogLevel.Information,
                "openai.codex.auth.browser.opened",
                "Opened browser for OpenAI authorization.",
                attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["auth.flow"] = "direct_listener",
                });
        }
        catch (Exception ex)
        {
            LogAuthEvent(
                PackageLogLevel.Error,
                "openai.codex.auth.browser.open_failed",
                "Failed to open browser for OpenAI authorization.",
                attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["auth.flow"] = "direct_listener",
                },
                exception: ex);
            throw;
        }

        using var registration = cancellationToken.Register(() =>
        {
            try { listener.Stop(); } catch { }
        });

        LogAuthEvent(
            PackageLogLevel.Information,
            "openai.codex.auth.callback.wait_start",
            "Waiting for OpenAI browser authorization callback.",
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["auth.flow"] = "direct_listener",
            });
        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync();
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested
                                   && ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
            LogAuthEvent(
                PackageLogLevel.Warning,
                "openai.codex.auth.callback.wait_canceled",
                "Waiting for OpenAI browser authorization callback was canceled or timed out.",
                attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["auth.flow"] = "direct_listener",
                },
                exception: ex);
            throw new OperationCanceledException(cancellationToken);
        }
        var returnedState = context.Request.QueryString["state"];
        var code = context.Request.QueryString["code"];
        var callbackIsValid = string.Equals(returnedState, state, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(code);
        LogAuthEvent(
            PackageLogLevel.Information,
            "openai.codex.auth.callback.received",
            "OpenAI browser authorization callback was received.",
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["auth.flow"] = "direct_listener",
                ["auth.has_code"] = !string.IsNullOrWhiteSpace(code),
                ["auth.state_matches"] = string.Equals(returnedState, state, StringComparison.Ordinal),
                ["auth.has_error"] = !string.IsNullOrWhiteSpace(context.Request.QueryString["error"]),
            });

        await WriteBrowserCompletionAsync(context.Response, callbackIsValid);

        if (!callbackIsValid)
        {
            LogAuthEvent(
                PackageLogLevel.Warning,
                "openai.codex.auth.callback.invalid",
                "OpenAI Codex browser sign-in failed or returned an invalid state.",
                attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["auth.flow"] = "direct_listener",
                    ["auth.has_code"] = !string.IsNullOrWhiteSpace(code),
                    ["auth.state_matches"] = string.Equals(returnedState, state, StringComparison.Ordinal),
                });
            throw new InvalidOperationException("OpenAI Codex browser sign-in failed or returned an invalid state.");
        }

        return await ExchangeAuthorizationCodeAsync(code!, verifier, redirectUri, cancellationToken);
    }

    private async Task<OpenAiCodexSession> ExchangeAuthorizationCodeAsync(
        string code,
        string verifier,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        LogAuthEvent(
            PackageLogLevel.Information,
            "openai.codex.auth.token_exchange.start",
            "Exchanging OpenAI authorization code for tokens.",
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["auth.redirect_uri"] = redirectUri,
                ["network.address_family"] = CodexHttpClientFactory.NetworkAddressFamily,
            });
        try
        {
            using var httpClient = CodexHttpClientFactory.CreateAuthClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = ClientId,
                    ["code"] = code,
                    ["code_verifier"] = verifier,
                    ["redirect_uri"] = redirectUri,
                }),
            };
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            LogAuthEvent(
                response.IsSuccessStatusCode ? PackageLogLevel.Debug : PackageLogLevel.Warning,
                "openai.codex.auth.token_exchange.headers_received",
                $"{(int)response.StatusCode} {response.ReasonPhrase}",
                stopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["http.status_code"] = (int)response.StatusCode,
                    ["http.reason_phrase"] = response.ReasonPhrase,
                    ["network.address_family"] = CodexHttpClientFactory.NetworkAddressFamily,
                });
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            var session = TryParseTokenResponse(payload) ?? throw new InvalidOperationException("OpenAI Codex token response was missing required fields.");
            LogAuthEvent(
                PackageLogLevel.Information,
                "openai.codex.auth.token_exchange.completed",
                "OpenAI token exchange completed.",
                stopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["auth.expires_at"] = session.ExpiresAtUtc,
                });
            return session;
        }
        catch (OperationCanceledException ex)
        {
            LogAuthEvent(
                PackageLogLevel.Warning,
                "openai.codex.auth.token_exchange.canceled",
                cancellationToken.IsCancellationRequested
                    ? "OpenAI token exchange was canceled by the caller."
                    : "OpenAI token exchange was canceled or timed out before completion.",
                stopwatch.ElapsedMilliseconds,
                exception: ex);
            throw;
        }
        catch (Exception ex)
        {
            LogAuthEvent(
                PackageLogLevel.Error,
                "openai.codex.auth.token_exchange.failed",
                "OpenAI token exchange failed.",
                stopwatch.ElapsedMilliseconds,
                exception: ex);
            throw;
        }
    }

    private void LogSessionSaved(string flow, OpenAiCodexSession session)
    {
        LogAuthEvent(
            PackageLogLevel.Information,
            "openai.codex.auth.session.saved",
            "Codex auth session was saved.",
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["auth.flow"] = flow,
                ["auth.expires_at"] = session.ExpiresAtUtc,
            });
    }

    private void LogAuthEvent(
        PackageLogLevel level,
        string eventName,
        string message,
        long? elapsedMilliseconds = null,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null)
    {
        var mergedAttributes = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["auth.mode"] = ModeId,
        };
        if (elapsedMilliseconds is not null)
        {
            mergedAttributes["duration.ms"] = elapsedMilliseconds.Value;
        }

        if (attributes is not null)
        {
            foreach (var attribute in attributes)
            {
                mergedAttributes[attribute.Key] = attribute.Value;
            }
        }

        try
        {
            _packageContext.Logging.Events.WriteAsync(level, eventName, message, mergedAttributes, exception).GetAwaiter().GetResult();
        }
        catch
        {
            // Auth diagnostics must not interrupt authorization.
        }
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

    private static HttpListener StartCallbackListener(out string redirectUri)
    {
        if (TryStartCallbackListener(PreferredCallbackPort, out var preferredListener, out var preferredException))
        {
            redirectUri = CreateRedirectUri(PreferredCallbackPort);
            return preferredListener!;
        }

        if (TryStartCallbackListener(FallbackCallbackPort, out var fallbackListener, out var fallbackException))
        {
            redirectUri = CreateRedirectUri(FallbackCallbackPort);
            return fallbackListener!;
        }

        throw new InvalidOperationException(
            $"Sunder could not start the local OpenAI browser callback listener on {CreateRedirectUri(PreferredCallbackPort)} or {CreateRedirectUri(FallbackCallbackPort)}. Close other Codex/Sunder auth listeners or applications using ports {PreferredCallbackPort} and {FallbackCallbackPort} and retry.",
            new AggregateException(preferredException!, fallbackException!));
    }

    private static bool TryStartCallbackListener(int port, out HttpListener? listener, out Exception? exception)
    {
        listener = new HttpListener();
        listener.Prefixes.Add($"{CreateRedirectUri(port)}/");
        try
        {
            listener.Start();
            exception = null;
            return true;
        }
        catch (Exception ex)
        {
            listener.Close();
            listener = null;
            exception = ex;
            return false;
        }
    }

    private static string CreateRedirectUri(int port)
        => $"http://localhost:{port}{CallbackPath}";

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
        var title = WebUtility.HtmlEncode(success ? "OpenAI callback received." : "OpenAI sign-in failed.");
        var subtitle = WebUtility.HtmlEncode(success
            ? "Sunder is finishing authorization. Return to Sunder to see the final status."
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
                        --bg: #15171a;
                        --bg-lifted: #1d2025;
                        --text: #dedad3;
                        --muted: #ccc7be;
                        --accent-strong: #e7b765;
                        --accent-rgb: 217, 154, 58;
                        --success-rgb: 110, 231, 183;
                        --white-rgb: 255, 255, 255;
                        font-family: "IBM Plex Sans", system-ui, sans-serif;
                    }

                    * {
                        box-sizing: border-box;
                    }

                    body {
                        margin: 0;
                        min-height: 100vh;
                        background:
                            radial-gradient(circle at 20% 10%, rgba(var(--accent-rgb), 0.18), transparent 28rem),
                            radial-gradient(circle at 85% 0%, rgba(var(--success-rgb), 0.12), transparent 24rem),
                            linear-gradient(180deg, var(--bg-lifted) 0%, var(--bg) 42rem);
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
                        border: 1px solid rgba(var(--accent-rgb), 0.46);
                        border-radius: 14px;
                        background: linear-gradient(135deg, rgba(var(--accent-rgb), 0.2), rgba(var(--white-rgb), 0.04));
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
