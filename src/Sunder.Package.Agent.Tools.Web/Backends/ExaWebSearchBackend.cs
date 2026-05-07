using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Tools.Web.Services;

namespace Sunder.Package.Agent.Tools.Web.Backends;

public sealed class ExaWebSearchBackend(WebToolsSettingsService settingsService)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WebToolsSettingsService _settingsService = settingsService;

    public string BackendId { get; } = "exa";

    public ValueTask<AgentToolReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentToolReadiness(
            "web_search",
            AgentToolReadinessStatus.Ready,
            string.IsNullOrWhiteSpace(_settingsService.GetExaApiKey())
                ? "Web search is ready via the default Exa MCP-backed route."
                : "Web search is ready via the Exa MCP-backed route using your optional Exa API key."));
    }

    public async Task<WebSearchBackendResult> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default)
    {
        using var httpClient = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpointUri())
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "tools/call",
                    @params = new
                    {
                        name = "web_search_exa",
                        arguments = new
                        {
                            query,
                            type = "auto",
                            numResults = Math.Clamp(maxResults, 1, 10),
                            livecrawl = "fallback",
                        }
                    }
                }),
                System.Text.Encoding.UTF8,
                "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        var content = ParseSearchContent(payload);

        return new WebSearchBackendResult(
            Summary: string.IsNullOrWhiteSpace(content)
                ? "Exa returned no web search results"
                : $"Fetched Exa-backed web search results for '{query}'",
            StructuredPayloadJson: JsonSerializer.Serialize(new
            {
                query,
                backend = BackendId,
                raw = payload,
            }),
            Sources: [],
            WasTruncated: false,
            BackendId: BackendId,
            Content: content);
    }

    private Uri BuildEndpointUri()
    {
        var apiKey = _settingsService.GetExaApiKey();
        return string.IsNullOrWhiteSpace(apiKey)
            ? new Uri("https://mcp.exa.ai/mcp")
            : new Uri($"https://mcp.exa.ai/mcp?exaApiKey={Uri.EscapeDataString(apiKey)}");
    }

    private static string? ParseSearchContent(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        var sseText = ParseSsePayload(payload);
        if (!string.IsNullOrWhiteSpace(sseText))
        {
            return sseText;
        }

        using var document = JsonDocument.Parse(payload);
        return TryExtractText(document.RootElement);
    }

    private static string? ParseSsePayload(string payload)
    {
        foreach (var line in payload.Split('\n'))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var json = line[6..].Trim();
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            using var document = JsonDocument.Parse(json);
            var text = TryExtractText(document.RootElement);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static string? TryExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var resultElement)
            || !resultElement.TryGetProperty("content", out var contentElement)
            || contentElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var item in contentElement.EnumerateArray())
        {
            if (item.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
            {
                var text = textElement.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    parts.Add(text);
                }
            }
        }

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }
}
