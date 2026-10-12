using System.Net;
using System.Text;
using System.Text.Json;

namespace Sunder.Package.Agent.Tools.Web.Backends;

internal sealed class WebSearchMcpClient(
    HttpClient httpClient,
    Uri endpoint,
    int maximumResponseBytes,
    string backendId,
    string toolName,
    string? bearerToken = null)
{
    internal const string RequestedProtocolVersion = "2025-06-18";
    internal const int MaximumHttpCalls = 7;
    internal const int MaximumToolPages = 4;
    internal const int MaximumSseEvents = 128;
    internal const int MaximumContentParts = 64;
    private const int MaximumTools = 128;
    private const int MaximumCursorChars = 1_024;
    private const int MaximumSessionIdChars = 1_024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 32 };
    private static readonly HashSet<string> SupportedProtocolVersions = new(StringComparer.Ordinal)
    {
        RequestedProtocolVersion,
        "2025-03-26",
        "2024-11-05",
    };

    private string? _protocolVersion;
    private string? _sessionId;
    private int _nextRequestId;
    private int _httpCallCount;

    public async Task<WebSearchMcpResult> CallAsync(
        object arguments,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await SendInitializedAsync(cancellationToken).ConfigureAwait(false);
        await RequireSearchToolAsync(cancellationToken).ConfigureAwait(false);
        return await CallSearchToolAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        using var response = await SendRequestAsync(
            "initialize",
            new
            {
                protocolVersion = RequestedProtocolVersion,
                capabilities = new { },
                clientInfo = new
                {
                    name = "sunder-agent-web",
                    version = "1.0.0",
                },
            },
            includeNegotiatedHeaders: false,
            cancellationToken).ConfigureAwait(false);
        var result = ReadResult(response.Document, response.RequestId, "initialize");
        if (!result.TryGetProperty("protocolVersion", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.String
            || versionElement.GetString() is not { } version
            || !SupportedProtocolVersions.Contains(version))
        {
            throw new InvalidDataException("Web search MCP initialize returned an unsupported protocol version.");
        }
        if (!result.TryGetProperty("capabilities", out var capabilities)
            || capabilities.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Web search MCP initialize response omitted server capabilities.");
        }

        _protocolVersion = version;
        _sessionId = response.SessionId;
    }

    private async Task SendInitializedAsync(CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized",
        });
        using var response = await SendAsync(
            HttpMethod.Post,
            "notifications/initialized",
            bytes,
            includeNegotiatedHeaders: true,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "notifications/initialized");
        ValidateResponseSession(response);
        var body = await ReadBoundedBytesAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (body.Length != 0)
        {
            throw new InvalidDataException("Web search MCP initialized notification returned an unexpected response body.");
        }
    }

    private async Task RequireSearchToolAsync(CancellationToken cancellationToken)
    {
        string? cursor = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        var toolCount = 0;
        for (var page = 1; page <= MaximumToolPages; page++)
        {
            using var response = await SendRequestAsync(
                "tools/list",
                cursor is null ? new { } : new { cursor },
                includeNegotiatedHeaders: true,
                cancellationToken).ConfigureAwait(false);
            var result = ReadResult(response.Document, response.RequestId, "tools/list");
            if (!result.TryGetProperty("tools", out var tools)
                || tools.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Web search MCP tools/list response omitted its tools array.");
            }

            foreach (var tool in tools.EnumerateArray())
            {
                if (++toolCount > MaximumTools)
                {
                    throw new InvalidDataException(
                        $"Web search MCP tools/list exceeds the {MaximumTools}-tool limit.");
                }
                if (tool.ValueKind != JsonValueKind.Object
                    || !tool.TryGetProperty("name", out var nameElement)
                    || nameElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(nameElement.GetString()))
                {
                    throw new InvalidDataException("Web search MCP tools/list contains an invalid tool descriptor.");
                }
                if (string.Equals(nameElement.GetString(), toolName, StringComparison.Ordinal))
                {
                    return;
                }
            }

            cursor = ReadNextCursor(result);
            if (cursor is null)
            {
                throw new InvalidOperationException($"Web search MCP server does not advertise {toolName}.");
            }
            if (page == MaximumToolPages)
            {
                throw new InvalidDataException(
                    $"Web search MCP tools/list exceeds the {MaximumToolPages}-page limit.");
            }
            if (!seenCursors.Add(cursor))
            {
                throw new InvalidDataException("Web search MCP tools/list repeated a pagination cursor.");
            }
        }
    }

    private async Task<WebSearchMcpResult> CallSearchToolAsync(
        object arguments,
        CancellationToken cancellationToken)
    {
        using var response = await SendRequestAsync(
            "tools/call",
            new
            {
                name = toolName,
                arguments,
            },
            includeNegotiatedHeaders: true,
            cancellationToken).ConfigureAwait(false);
        var result = ReadResult(response.Document, response.RequestId, "tools/call");
        var toolError = false;
        if (result.TryGetProperty("isError", out var isError))
        {
            if (isError.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new InvalidDataException("Web search MCP tools/call returned an invalid isError value.");
            }
            toolError = isError.GetBoolean();
        }
        if (!result.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Web search MCP tools/call response omitted its content array.");
        }

        var parts = new List<string>();
        var partCount = 0;
        foreach (var part in content.EnumerateArray())
        {
            if (++partCount > MaximumContentParts)
            {
                throw new InvalidDataException(
                    $"Web search MCP tools/call exceeds the {MaximumContentParts}-part content limit.");
            }
            if (part.ValueKind != JsonValueKind.Object
                || !part.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Web search MCP tools/call contains an invalid content block.");
            }
            if (string.Equals(type.GetString(), "text", StringComparison.Ordinal)
                && part.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String
                && text.GetString() is { } value
                && !string.IsNullOrWhiteSpace(value))
            {
                parts.Add(value);
            }
        }
        // Some deployed Exa versions return quota guidance as plain text without isError.
        // Recognize the service's exact leading diagnostic, not arbitrary search snippets.
        if (parts.Any(IsQuotaMessage))
        {
            throw new WebSearchRateLimitException(backendId);
        }
        if (toolError)
        {
            throw new InvalidOperationException($"Web search MCP {toolName} reported a tool error.");
        }
        return new WebSearchMcpResult(parts,
            result.TryGetProperty("structuredContent", out var structured) ? structured.Clone() : null);
    }

    private async Task<McpResponse> SendRequestAsync(
        string method,
        object parameters,
        bool includeNegotiatedHeaders,
        CancellationToken cancellationToken)
    {
        var requestId = checked(++_nextRequestId);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id = requestId,
            method,
            @params = parameters,
        });
        using var response = await SendAsync(
            HttpMethod.Post,
            method,
            bytes,
            includeNegotiatedHeaders,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, method);
        var sessionId = ReadSessionId(response);
        if (includeNegotiatedHeaders)
        {
            ValidateResponseSession(sessionId);
        }
        var document = await ReadResponseDocumentAsync(
            response,
            requestId,
            method,
            cancellationToken).ConfigureAwait(false);
        return new McpResponse(document, requestId, sessionId);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string mcpMethod,
        byte[]? body,
        bool includeNegotiatedHeaders,
        CancellationToken cancellationToken)
    {
        if (++_httpCallCount > MaximumHttpCalls)
        {
            throw new InvalidDataException(
                $"Web search MCP operation exceeds the {MaximumHttpCalls}-call HTTP limit.");
        }
        using var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.UserAgent.ParseAdd("sunder-agent-web/1.0.0");
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new("Bearer", bearerToken);
        }
        request.Headers.TryAddWithoutValidation("Mcp-Method", mcpMethod);
        if (includeNegotiatedHeaders)
        {
            if (_protocolVersion is null)
            {
                throw new InvalidOperationException("Web search MCP session has not completed initialization.");
            }
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocolVersion);
            if (_sessionId is not null)
            {
                request.Headers.TryAddWithoutValidation("MCP-Session-Id", _sessionId);
            }
        }
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        }
        return await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> ReadResponseDocumentAsync(
        HttpResponseMessage response,
        int requestId,
        string method,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedBytesAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            throw new InvalidDataException($"Web search MCP {method} returned an empty response.");
        }
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return JsonDocument.Parse(bytes, JsonOptions);
        }
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            string payload;
            try
            {
                payload = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("Web search MCP SSE response is not valid UTF-8.", exception);
            }
            return ParseSseResponse(payload, requestId, method);
        }
        throw new InvalidDataException($"Web search MCP {method} returned an unsupported content type.");
    }

    private static JsonDocument ParseSseResponse(string payload, int requestId, string method)
    {
        using var reader = new StringReader(payload);
        var data = new StringBuilder();
        string? matchedResponse = null;
        var eventCount = 0;
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                ProcessEvent();
                continue;
            }
            if (line.StartsWith(':'))
            {
                continue;
            }
            var separator = line.IndexOf(':');
            var field = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? string.Empty : line[(separator + 1)..].TrimStart(' ');
            if (string.Equals(field, "data", StringComparison.Ordinal))
            {
                if (data.Length != 0)
                {
                    data.Append('\n');
                }
                data.Append(value);
            }
        }
        ProcessEvent();
        return matchedResponse is null
            ? throw new InvalidDataException($"Web search MCP {method} SSE stream omitted the matching response.")
            : JsonDocument.Parse(matchedResponse, JsonOptions);

        void ProcessEvent()
        {
            if (data.Length == 0)
            {
                return;
            }
            if (++eventCount > MaximumSseEvents)
            {
                throw new InvalidDataException(
                    $"Web search MCP response exceeds the {MaximumSseEvents}-event SSE limit.");
            }
            var json = data.ToString();
            data.Clear();
            if (string.Equals(json, "[DONE]", StringComparison.Ordinal))
            {
                return;
            }
            using var document = JsonDocument.Parse(json, JsonOptions);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.Number
                && id.TryGetInt32(out var value)
                && value == requestId)
            {
                if (matchedResponse is not null)
                {
                    throw new InvalidDataException($"Web search MCP {method} SSE stream returned duplicate responses.");
                }
                matchedResponse = json;
            }
        }
    }

    private async Task<byte[]> ReadBoundedBytesAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } declaredLength
            && declaredLength > maximumResponseBytes)
        {
            throw new InvalidDataException(
                $"Web search MCP response exceeds the {maximumResponseBytes}-byte limit.");
        }
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream(Math.Min(maximumResponseBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var remaining = maximumResponseBytes - checked((int)memory.Length);
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (read > remaining)
            {
                throw new InvalidDataException(
                    $"Web search MCP response exceeds the {maximumResponseBytes}-byte limit.");
            }
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    private JsonElement ReadResult(JsonDocument document, int requestId, string method)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("jsonrpc", out var jsonRpc)
            || jsonRpc.ValueKind != JsonValueKind.String
            || !string.Equals(jsonRpc.GetString(), "2.0", StringComparison.Ordinal)
            || !root.TryGetProperty("id", out var id)
            || id.ValueKind != JsonValueKind.Number
            || !id.TryGetInt32(out var responseId)
            || responseId != requestId)
        {
            throw new InvalidDataException($"Web search MCP {method} returned an invalid JSON-RPC envelope.");
        }
        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                && IsQuotaMessage(message.GetString()!))
            {
                throw new WebSearchRateLimitException(backendId);
            }
            var code = error.ValueKind == JsonValueKind.Object
                       && error.TryGetProperty("code", out var codeElement)
                       && codeElement.TryGetInt32(out var numericCode)
                ? numericCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "unknown";
            throw new InvalidOperationException(
                $"Web search MCP {method} failed with JSON-RPC error {code}.");
        }
        if (!root.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Web search MCP {method} response omitted its result.");
        }
        return result;
    }

    private static string? ReadNextCursor(JsonElement result)
    {
        if (!result.TryGetProperty("nextCursor", out var cursor)
            || cursor.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (cursor.ValueKind != JsonValueKind.String
            || cursor.GetString() is not { Length: > 0 } value
            || value.Length > MaximumCursorChars)
        {
            throw new InvalidDataException("Web search MCP tools/list returned an invalid pagination cursor.");
        }
        return value;
    }

    private void EnsureSuccess(HttpResponseMessage response, string method)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new WebSearchRateLimitException(backendId,
                response.Headers.RetryAfter?.Delta, response.Headers.RetryAfter?.Date);
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Web search MCP {method} failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                inner: null,
                response.StatusCode);
        }
    }

    private string? ReadSessionId(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("MCP-Session-Id", out var values))
        {
            return null;
        }
        var sessions = values.ToArray();
        if (sessions is not [{ Length: > 0 } session]
            || session.Length > MaximumSessionIdChars
            || session.Any(character => character is < '!' or > '~'))
        {
            throw new InvalidDataException("Web search MCP returned an invalid session id.");
        }
        return session;
    }

    private void ValidateResponseSession(HttpResponseMessage response)
        => ValidateResponseSession(ReadSessionId(response));

    private void ValidateResponseSession(string? responseSessionId)
    {
        if (responseSessionId is not null
            && !string.Equals(responseSessionId, _sessionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Web search MCP response changed the negotiated session id.");
        }
    }

    private bool IsQuotaMessage(string text)
        => backendId == "exa" && text.TrimStart().StartsWith(
            "You've hit Exa's free MCP rate limit.", StringComparison.Ordinal);

    private sealed class McpResponse(JsonDocument document, int requestId, string? sessionId) : IDisposable
    {
        public JsonDocument Document { get; } = document;
        public int RequestId { get; } = requestId;
        public string? SessionId { get; } = sessionId;
        public void Dispose() => Document.Dispose();
    }
}

internal sealed record WebSearchMcpResult(IReadOnlyList<string> Text, JsonElement? StructuredContent);
