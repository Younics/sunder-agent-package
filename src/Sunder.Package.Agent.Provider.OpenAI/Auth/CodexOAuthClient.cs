using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Provider.OpenAI.Auth;

internal sealed class CodexOAuthClient
{
    internal const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    internal const int MaximumTokenResponseBytes = 64 * 1024;
    private const string TokenUrl = "https://auth.openai.com/oauth/token";
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Func<HttpClient> _httpClientFactory;
    private readonly CodexOAuthTokenParser _tokenParser;
    private readonly CodexAuthTelemetry _telemetry;
    private readonly TimeSpan _operationTimeout;

    public CodexOAuthClient(
        Func<HttpClient> httpClientFactory,
        CodexOAuthTokenParser tokenParser,
        CodexAuthTelemetry telemetry,
        TimeSpan? operationTimeout = null)
    {
        _httpClientFactory = httpClientFactory;
        _tokenParser = tokenParser;
        _telemetry = telemetry;
        _operationTimeout = operationTimeout ?? DefaultOperationTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_operationTimeout, TimeSpan.Zero);
    }

    public async Task<CodexRefreshResult> RefreshAsync(
        OpenAiCodexSession existingSession,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await _telemetry.WriteAsync(
            PackageLogLevel.Information,
            "openai.codex.auth.refresh.start",
            "Refreshing Codex auth session.",
            attributes: NetworkAttributes());
        HttpStatusCode? responseStatusCode = null;
        using var deadlineCancellation = new CancellationTokenSource(_operationTimeout);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadlineCancellation.Token);
        try
        {
            using var client = _httpClientFactory();
            client.Timeout = Timeout.InfiniteTimeSpan;
            using var request = CreateRefreshTokenRequest(existingSession.RefreshToken);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                operationCancellation.Token).ConfigureAwait(false);
            responseStatusCode = response.StatusCode;
            await LogHeadersAsync("refresh", response, stopwatch.ElapsedMilliseconds);
            var payload = await ReadTokenPayloadAsync(response.Content, operationCancellation.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = ParseOAuthError(payload);
                await _telemetry.WriteAsync(
                    PackageLogLevel.Warning,
                    "openai.codex.auth.refresh.provider_rejected",
                    "OpenAI rejected the Codex refresh request.",
                    stopwatch.ElapsedMilliseconds,
                    FailureAttributes(response.StatusCode, error.Code));
                return CodexRefreshResult.Failure(ClassifyFailure(response.StatusCode, payload), response.StatusCode);
            }

            var session = _tokenParser.TryParse(payload, existingSession);
            if (session is null)
            {
                await _telemetry.WriteAsync(
                    PackageLogLevel.Warning,
                    "openai.codex.auth.refresh.parse_failed",
                    "Codex refresh response was missing required session fields.",
                    stopwatch.ElapsedMilliseconds);
                return CodexRefreshResult.Failure(CodexAuthFailureKind.Transient, response.StatusCode);
            }

            await _telemetry.WriteAsync(
                PackageLogLevel.Information,
                "openai.codex.auth.refresh.completed",
                "Codex auth session refreshed.",
                stopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?> { ["auth.expires_at"] = session.ExpiresAtUtc });
            return CodexRefreshResult.Success(session);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _telemetry.WriteAsync(
                PackageLogLevel.Warning,
                "openai.codex.auth.refresh.canceled",
                "Codex auth session refresh was canceled.",
                stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (OperationCanceledException) when (deadlineCancellation.IsCancellationRequested)
        {
            await _telemetry.WriteAsync(
                PackageLogLevel.Warning,
                "openai.codex.auth.refresh.timeout",
                "Codex auth session refresh timed out while reading the provider response.",
                stopwatch.ElapsedMilliseconds,
                StatusAttributes(responseStatusCode));
            return CodexRefreshResult.Failure(ClassifyStatus(responseStatusCode), responseStatusCode);
        }
        catch (OperationCanceledException)
        {
            await _telemetry.WriteAsync(
                PackageLogLevel.Warning,
                "openai.codex.auth.refresh.network_failure",
                "Codex auth session refresh was canceled by the provider.",
                stopwatch.ElapsedMilliseconds);
            return CodexRefreshResult.Failure(ClassifyStatus(responseStatusCode), responseStatusCode);
        }
        catch (HttpRequestException ex)
        {
            await _telemetry.WriteAsync(
                PackageLogLevel.Warning,
                "openai.codex.auth.refresh.network_failure",
                "Codex auth session refresh failed transiently.",
                stopwatch.ElapsedMilliseconds,
                StatusAttributes(responseStatusCode ?? ex.StatusCode));
            return CodexRefreshResult.Failure(
                ClassifyStatus(responseStatusCode ?? ex.StatusCode),
                responseStatusCode ?? ex.StatusCode);
        }
        catch (InvalidDataException)
        {
            await _telemetry.WriteAsync(
                PackageLogLevel.Warning,
                "openai.codex.auth.refresh.parse_failed",
                "Codex refresh response exceeded the allowed size.",
                stopwatch.ElapsedMilliseconds,
                StatusAttributes(responseStatusCode));
            return CodexRefreshResult.Failure(ClassifyStatus(responseStatusCode), responseStatusCode);
        }
        catch (IOException)
        {
            await _telemetry.WriteAsync(
                PackageLogLevel.Warning,
                "openai.codex.auth.refresh.network_failure",
                "Codex auth session refresh failed while reading the response.",
                stopwatch.ElapsedMilliseconds,
                StatusAttributes(responseStatusCode));
            return CodexRefreshResult.Failure(ClassifyStatus(responseStatusCode), responseStatusCode);
        }
        catch (DecoderFallbackException)
        {
            await _telemetry.WriteAsync(
                PackageLogLevel.Warning,
                "openai.codex.auth.refresh.parse_failed",
                "Codex refresh response was not valid UTF-8.",
                stopwatch.ElapsedMilliseconds,
                StatusAttributes(responseStatusCode));
            return CodexRefreshResult.Failure(ClassifyStatus(responseStatusCode), responseStatusCode);
        }
    }

    public async Task<OpenAiCodexSession> ExchangeAuthorizationCodeAsync(
        string code,
        string verifier,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await _telemetry.WriteAsync(
            PackageLogLevel.Information,
            "openai.codex.auth.token_exchange.start",
            "Exchanging OpenAI authorization code for tokens.",
            attributes: new Dictionary<string, object?>
            {
                ["auth.redirect_uri"] = redirectUri,
                ["network.address_family"] = CodexHttpClientFactory.NetworkAddressFamily,
            });
        HttpStatusCode? responseStatusCode = null;
        using var deadlineCancellation = new CancellationTokenSource(_operationTimeout);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadlineCancellation.Token);
        try
        {
            using var client = _httpClientFactory();
            client.Timeout = Timeout.InfiniteTimeSpan;
            using var request = CreateTokenRequest(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = ClientId,
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["redirect_uri"] = redirectUri,
            });
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                operationCancellation.Token).ConfigureAwait(false);
            responseStatusCode = response.StatusCode;
            await LogHeadersAsync("token_exchange", response, stopwatch.ElapsedMilliseconds);
            var payload = await ReadTokenPayloadAsync(response.Content, operationCancellation.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = ParseOAuthError(payload);
                await _telemetry.WriteAsync(
                    PackageLogLevel.Warning,
                    "openai.codex.auth.token_exchange.provider_rejected",
                    "OpenAI rejected the authorization code exchange.",
                    stopwatch.ElapsedMilliseconds,
                    FailureAttributes(response.StatusCode, error.Code));
                throw new CodexOAuthException(
                    ClassifyFailure(response.StatusCode, payload),
                    FormatProviderRejection(response.StatusCode, error),
                    response.StatusCode);
            }

            var session = _tokenParser.TryParse(payload);
            if (session is null)
            {
                await _telemetry.WriteAsync(
                    PackageLogLevel.Warning,
                    "openai.codex.auth.token_exchange.parse_failed",
                    "OpenAI token response was missing required session fields.",
                    stopwatch.ElapsedMilliseconds,
                    StatusAttributes(response.StatusCode));
                throw new CodexOAuthException(
                    CodexAuthFailureKind.Terminal,
                    "OpenAI Codex token response was missing required fields.",
                    response.StatusCode);
            }

            await _telemetry.WriteAsync(
                PackageLogLevel.Information,
                "openai.codex.auth.token_exchange.completed",
                "OpenAI token exchange completed.",
                stopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?> { ["auth.expires_at"] = session.ExpiresAtUtc });
            return session;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _telemetry.WriteAsync(
                PackageLogLevel.Warning,
                "openai.codex.auth.token_exchange.canceled",
                "OpenAI token exchange was canceled.",
                stopwatch.ElapsedMilliseconds,
                StatusAttributes(responseStatusCode));
            throw;
        }
        catch (OperationCanceledException ex) when (deadlineCancellation.IsCancellationRequested)
        {
            await _telemetry.WriteAsync(
                PackageLogLevel.Warning,
                "openai.codex.auth.token_exchange.timeout",
                "OpenAI token exchange timed out.",
                stopwatch.ElapsedMilliseconds,
                StatusAttributes(responseStatusCode));
            throw new CodexOAuthException(
                ClassifyStatus(responseStatusCode),
                "OpenAI token exchange timed out.",
                responseStatusCode,
                innerException: ex);
        }
        catch (OperationCanceledException ex)
        {
            await _telemetry.WriteAsync(
                PackageLogLevel.Warning,
                "openai.codex.auth.token_exchange.network_failure",
                "OpenAI token exchange was canceled by the provider.",
                stopwatch.ElapsedMilliseconds,
                StatusAttributes(responseStatusCode));
            throw new CodexOAuthException(
                ClassifyStatus(responseStatusCode),
                "OpenAI token exchange was canceled by the provider.",
                responseStatusCode,
                ex);
        }
        catch (HttpRequestException ex)
        {
            await _telemetry.WriteAsync(
                PackageLogLevel.Warning,
                "openai.codex.auth.token_exchange.network_failure",
                "OpenAI token exchange failed due to a network error.",
                stopwatch.ElapsedMilliseconds,
                StatusAttributes(responseStatusCode ?? ex.StatusCode));
            throw new CodexOAuthException(
                ClassifyStatus(responseStatusCode ?? ex.StatusCode),
                "OpenAI token exchange failed due to a transient network error.",
                responseStatusCode ?? ex.StatusCode,
                ex);
        }
        catch (InvalidDataException ex)
        {
            await _telemetry.WriteAsync(
                PackageLogLevel.Warning,
                "openai.codex.auth.token_exchange.parse_failed",
                "OpenAI token response exceeded the allowed size.",
                stopwatch.ElapsedMilliseconds,
                StatusAttributes(responseStatusCode));
            throw new CodexOAuthException(
                CodexAuthFailureKind.Terminal,
                "OpenAI token response exceeded the allowed size.",
                responseStatusCode,
                ex);
        }
        catch (IOException ex)
        {
            await _telemetry.WriteAsync(
                PackageLogLevel.Warning,
                "openai.codex.auth.token_exchange.network_failure",
                "OpenAI token exchange failed while reading the response.",
                stopwatch.ElapsedMilliseconds,
                StatusAttributes(responseStatusCode));
            throw new CodexOAuthException(
                ClassifyStatus(responseStatusCode),
                "OpenAI token exchange failed while reading the provider response.",
                responseStatusCode,
                ex);
        }
        catch (DecoderFallbackException ex)
        {
            await _telemetry.WriteAsync(
                PackageLogLevel.Warning,
                "openai.codex.auth.token_exchange.parse_failed",
                "OpenAI token response was not valid UTF-8.",
                stopwatch.ElapsedMilliseconds,
                StatusAttributes(responseStatusCode));
            throw new CodexOAuthException(
                CodexAuthFailureKind.Terminal,
                "OpenAI token response was not valid UTF-8.",
                responseStatusCode,
                ex);
        }
    }

    internal static CodexAuthFailureKind ClassifyFailure(HttpStatusCode statusCode, string payload)
    {
        if (statusCode == HttpStatusCode.Unauthorized)
        {
            return CodexAuthFailureKind.Terminal;
        }

        var code = ParseOAuthError(payload).Code;
        if (string.Equals(code, "invalid_grant", StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, "refresh_token_expired", StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, "refresh_token_reused", StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, "refresh_token_invalidated", StringComparison.OrdinalIgnoreCase))
        {
            return CodexAuthFailureKind.Terminal;
        }

        return CodexAuthFailureKind.Transient;
    }

    private static HttpRequestMessage CreateTokenRequest(IReadOnlyDictionary<string, string> values)
        => new(HttpMethod.Post, TokenUrl) { Content = new FormUrlEncodedContent(values) };

    private static HttpRequestMessage CreateRefreshTokenRequest(string refreshToken)
    {
        var content = new StringContent(
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
            }),
            Encoding.UTF8,
            "application/json");
        content.Headers.ContentType!.CharSet = null;
        return new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = content,
        };
    }

    private ValueTask LogHeadersAsync(string operation, HttpResponseMessage response, long elapsedMilliseconds)
        => _telemetry.WriteAsync(
            response.IsSuccessStatusCode ? PackageLogLevel.Debug : PackageLogLevel.Warning,
            $"openai.codex.auth.{operation}.headers_received",
            "OpenAI token endpoint response headers were received.",
            elapsedMilliseconds,
            new Dictionary<string, object?>
            {
                ["http.status_code"] = (int)response.StatusCode,
                ["network.address_family"] = CodexHttpClientFactory.NetworkAddressFamily,
            });

    private static async Task<string> ReadTokenPayloadAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumTokenResponseBytes)
        {
            throw new InvalidDataException("The OpenAI token response exceeded the allowed size.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var payload = new byte[MaximumTokenResponseBytes + 1];
        var length = 0;
        while (length < payload.Length)
        {
            var read = await stream.ReadAsync(payload.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        if (length > MaximumTokenResponseBytes)
        {
            throw new InvalidDataException("The OpenAI token response exceeded the allowed size.");
        }

        return StrictUtf8.GetString(payload.AsSpan(0, length));
    }

    private static OAuthErrorDetails ParseOAuthError(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            string? code = null;
            string? nestedMessage = null;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    code = SanitizeErrorCode(error.GetString());
                }
                else if (error.ValueKind == JsonValueKind.Object)
                {
                    code = SanitizeErrorCode(GetString(error, "code"))
                        ?? SanitizeErrorCode(GetString(error, "type"));
                    nestedMessage = SanitizeErrorDetail(GetString(error, "message"));
                }
            }

            code ??= SanitizeErrorCode(GetString(root, "code"));

            var description = SanitizeErrorDetail(GetString(root, "error_description"))
                ?? nestedMessage
                ?? SanitizeErrorDetail(GetString(root, "message"));
            return new OAuthErrorDetails(code, description);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string FormatProviderRejection(HttpStatusCode statusCode, OAuthErrorDetails error)
    {
        var code = error.Code is null ? string.Empty : $" ({error.Code})";
        var detail = error.Description is null ? string.Empty : $": {error.Description}";
        return $"OpenAI token exchange was rejected with status {(int)statusCode}{code}{detail}.";
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? SanitizeErrorCode(string? value)
    {
        var candidate = value?.Trim();
        if (string.IsNullOrEmpty(candidate)
            || candidate.Length > 64
            || candidate.Any(static character => !char.IsAsciiLetterOrDigit(character)
                && character is not ('_' or '-' or '.')))
        {
            return null;
        }

        return candidate;
    }

    private static string? SanitizeErrorDetail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var sanitized = new StringBuilder(Math.Min(value.Length, 512));
        var pendingSpace = false;
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                pendingSpace = sanitized.Length > 0;
                continue;
            }

            if (pendingSpace && sanitized.Length < 512)
            {
                sanitized.Append(' ');
            }

            if (sanitized.Length >= 512)
            {
                break;
            }

            sanitized.Append(character);
            pendingSpace = false;
        }

        return sanitized.Length == 0 ? null : sanitized.ToString();
    }

    private static IReadOnlyDictionary<string, object?> FailureAttributes(
        HttpStatusCode statusCode,
        string? errorCode)
    {
        var attributes = new Dictionary<string, object?>
        {
            ["http.status_code"] = (int)statusCode,
            ["network.address_family"] = CodexHttpClientFactory.NetworkAddressFamily,
        };
        if (errorCode is not null)
        {
            attributes["oauth.error_code"] = errorCode;
        }

        return attributes;
    }

    private static IReadOnlyDictionary<string, object?>? StatusAttributes(HttpStatusCode? statusCode)
        => statusCode is null
            ? NetworkAttributes()
            : new Dictionary<string, object?>
            {
                ["http.status_code"] = (int)statusCode.Value,
                ["network.address_family"] = CodexHttpClientFactory.NetworkAddressFamily,
            };

    private static CodexAuthFailureKind ClassifyStatus(HttpStatusCode? statusCode)
        => statusCode == HttpStatusCode.Unauthorized
            ? CodexAuthFailureKind.Terminal
            : CodexAuthFailureKind.Transient;

    private static IReadOnlyDictionary<string, object?> NetworkAttributes()
        => new Dictionary<string, object?>
        {
            ["network.address_family"] = CodexHttpClientFactory.NetworkAddressFamily,
        };

    private readonly record struct OAuthErrorDetails(string? Code, string? Description);
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
