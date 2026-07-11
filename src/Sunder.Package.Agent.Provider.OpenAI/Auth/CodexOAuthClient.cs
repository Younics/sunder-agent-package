using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Provider.OpenAI.Auth;

internal sealed class CodexOAuthClient(
    Func<HttpClient> httpClientFactory,
    CodexOAuthTokenParser tokenParser,
    CodexAuthTelemetry telemetry)
{
    internal const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string TokenUrl = "https://auth.openai.com/oauth/token";
    private readonly Func<HttpClient> _httpClientFactory = httpClientFactory;
    private readonly CodexOAuthTokenParser _tokenParser = tokenParser;
    private readonly CodexAuthTelemetry _telemetry = telemetry;

    public async Task<CodexRefreshResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        _telemetry.Write(
            PackageLogLevel.Information,
            "openai.codex.auth.refresh.start",
            "Refreshing Codex auth session.",
            attributes: NetworkAttributes());
        try
        {
            using var client = _httpClientFactory();
            using var request = CreateTokenRequest(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = ClientId,
            });
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            LogHeaders("refresh", response, stopwatch.ElapsedMilliseconds);
            if (!response.IsSuccessStatusCode)
            {
                return CodexRefreshResult.Failure(ClassifyFailure(response.StatusCode, payload), response.StatusCode);
            }

            var session = _tokenParser.TryParse(payload, refreshToken);
            if (session is null)
            {
                _telemetry.Write(
                    PackageLogLevel.Warning,
                    "openai.codex.auth.refresh.parse_failed",
                    "Codex refresh response was missing required session fields.",
                    stopwatch.ElapsedMilliseconds);
                return CodexRefreshResult.Failure(CodexAuthFailureKind.Transient, response.StatusCode);
            }

            _telemetry.Write(
                PackageLogLevel.Information,
                "openai.codex.auth.refresh.completed",
                "Codex auth session refreshed.",
                stopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?> { ["auth.expires_at"] = session.ExpiresAtUtc });
            return CodexRefreshResult.Success(session);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _telemetry.Write(
                PackageLogLevel.Warning,
                "openai.codex.auth.refresh.transient_failure",
                "Codex auth session refresh timed out transiently.",
                stopwatch.ElapsedMilliseconds,
                exception: ex);
            return CodexRefreshResult.Failure(CodexAuthFailureKind.Transient, null);
        }
        catch (OperationCanceledException ex)
        {
            _telemetry.Write(
                PackageLogLevel.Warning,
                "openai.codex.auth.refresh.canceled",
                "Codex auth session refresh was canceled.",
                stopwatch.ElapsedMilliseconds,
                exception: ex);
            throw;
        }
        catch (HttpRequestException ex)
        {
            _telemetry.Write(
                PackageLogLevel.Warning,
                "openai.codex.auth.refresh.transient_failure",
                "Codex auth session refresh failed transiently.",
                stopwatch.ElapsedMilliseconds,
                exception: ex);
            return CodexRefreshResult.Failure(CodexAuthFailureKind.Transient, ex.StatusCode);
        }
    }

    public async Task<OpenAiCodexSession> ExchangeAuthorizationCodeAsync(
        string code,
        string verifier,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        _telemetry.Write(
            PackageLogLevel.Information,
            "openai.codex.auth.token_exchange.start",
            "Exchanging OpenAI authorization code for tokens.",
            attributes: new Dictionary<string, object?>
            {
                ["auth.redirect_uri"] = redirectUri,
                ["network.address_family"] = CodexHttpClientFactory.NetworkAddressFamily,
            });
        try
        {
            using var client = _httpClientFactory();
            using var request = CreateTokenRequest(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = ClientId,
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["redirect_uri"] = redirectUri,
            });
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            LogHeaders("token_exchange", response, stopwatch.ElapsedMilliseconds);
            if (!response.IsSuccessStatusCode)
            {
                throw new CodexOAuthException(
                    ClassifyFailure(response.StatusCode, payload),
                    $"OpenAI token exchange failed with status {(int)response.StatusCode}.",
                    response.StatusCode);
            }

            var session = _tokenParser.TryParse(payload)
                ?? throw new CodexOAuthException(
                    CodexAuthFailureKind.Terminal,
                    "OpenAI Codex token response was missing required fields.",
                    response.StatusCode);
            _telemetry.Write(
                PackageLogLevel.Information,
                "openai.codex.auth.token_exchange.completed",
                "OpenAI token exchange completed.",
                stopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?> { ["auth.expires_at"] = session.ExpiresAtUtc });
            return session;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CodexOAuthException(
                CodexAuthFailureKind.Transient,
                "OpenAI token exchange timed out.",
                innerException: ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new CodexOAuthException(
                CodexAuthFailureKind.Transient,
                "OpenAI token exchange failed due to a transient network error.",
                ex.StatusCode,
                ex);
        }
    }

    internal static CodexAuthFailureKind ClassifyFailure(HttpStatusCode statusCode, string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && string.Equals(error.GetString(), "invalid_grant", StringComparison.Ordinal))
            {
                return CodexAuthFailureKind.Terminal;
            }
        }
        catch (JsonException)
        {
        }

        return CodexAuthFailureKind.Transient;
    }

    private static HttpRequestMessage CreateTokenRequest(IReadOnlyDictionary<string, string> values)
        => new(HttpMethod.Post, TokenUrl) { Content = new FormUrlEncodedContent(values) };

    private void LogHeaders(string operation, HttpResponseMessage response, long elapsedMilliseconds)
        => _telemetry.Write(
            response.IsSuccessStatusCode ? PackageLogLevel.Debug : PackageLogLevel.Warning,
            $"openai.codex.auth.{operation}.headers_received",
            $"{(int)response.StatusCode} {response.ReasonPhrase}",
            elapsedMilliseconds,
            new Dictionary<string, object?>
            {
                ["http.status_code"] = (int)response.StatusCode,
                ["http.reason_phrase"] = response.ReasonPhrase,
                ["network.address_family"] = CodexHttpClientFactory.NetworkAddressFamily,
            });

    private static IReadOnlyDictionary<string, object?> NetworkAttributes()
        => new Dictionary<string, object?>
        {
            ["network.address_family"] = CodexHttpClientFactory.NetworkAddressFamily,
        };
}

internal enum CodexAuthFailureKind
{
    Transient,
    Terminal,
}

internal sealed record CodexRefreshResult(
    OpenAiCodexSession? Session,
    CodexAuthFailureKind? FailureKind,
    HttpStatusCode? StatusCode)
{
    public static CodexRefreshResult Success(OpenAiCodexSession session) => new(session, null, null);

    public static CodexRefreshResult Failure(CodexAuthFailureKind failureKind, HttpStatusCode? statusCode)
        => new(null, failureKind, statusCode);
}

internal sealed class CodexOAuthException(
    CodexAuthFailureKind failureKind,
    string message,
    HttpStatusCode? statusCode = null,
    Exception? innerException = null) : InvalidOperationException(message, innerException)
{
    public CodexAuthFailureKind FailureKind { get; } = failureKind;

    public HttpStatusCode? StatusCode { get; } = statusCode;
}
