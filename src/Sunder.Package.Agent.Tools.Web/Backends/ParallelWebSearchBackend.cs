using System.Text;
using System.Text.Json;
using Sunder.Generated.Packages.Sunder_Package_Agent_Models;
using Sunder.Package.Agent.Tools.Web.Services;

namespace Sunder.Package.Agent.Tools.Web.Backends;

public sealed class ParallelWebSearchBackend : IDisposable
{
    private static readonly Uri Endpoint = new("https://search.parallel.ai/mcp");
    private readonly WebToolsSettingsService _settings;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public ParallelWebSearchBackend(WebToolsSettingsService settings)
        : this(settings, new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        })
    {
        _disposeHttpClient = true;
    }

    public ParallelWebSearchBackend(WebToolsSettingsService settings, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(httpClient);
        _settings = settings;
        _httpClient = httpClient;
    }

    public string BackendId => "parallel";

    public async Task<WebSearchBackendResult> SearchAsync(
        string query, int maxResults, Guid? sessionId, CancellationToken cancellationToken = default)
        => await SearchAsync(query, maxResults, sessionId,
            await _settings.GetParallelApiKeyAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

    internal async Task<WebSearchBackendResult> SearchAsync(
        string query, int maxResults, Guid? sessionId, string? apiKey, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            // The conversation id is host-owned and stable across calls, not regenerated to reset quotas.
            var arguments = new Dictionary<string, object>
            {
                ["objective"] = query,
                ["search_queries"] = new[] { query },
            };
            if (sessionId is { } id)
            {
                arguments["session_id"] = id.ToString("N");
            }
            var response = await new WebSearchMcpClient(_httpClient, Endpoint,
                ExaWebSearchBackend.MaximumRawResponseBytes, BackendId, "web_search", apiKey)
                .CallAsync(arguments, timeout.Token).ConfigureAwait(false);
            return Normalize(query, Math.Clamp(maxResults, 1, ExaWebSearchBackend.MaximumResultCount),
                response.StructuredContent);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Parallel did not respond within 30 seconds.");
        }
    }

    private WebSearchBackendResult Normalize(string query, int maxResults, JsonElement? structured)
    {
        if (structured is not { ValueKind: JsonValueKind.Object } payload
            || !payload.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() > 128)
        {
            throw new InvalidDataException("Parallel search returned an invalid structured results array.");
        }

        var sources = new List<AgentToolSourceItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var content = new StringBuilder();
        var wasTruncated = false;
        foreach (var result in results.EnumerateArray())
        {
            if (result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("url", out var urlElement)
                || urlElement.ValueKind != JsonValueKind.String
                || urlElement.GetString() is not { Length: <= 2_048 } url
                || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https")
                || !result.TryGetProperty("excerpts", out var excerpts)
                || excerpts.ValueKind != JsonValueKind.Array
                || excerpts.GetArrayLength() > 128)
            {
                throw new InvalidDataException("Parallel search returned an invalid result item.");
            }
            var title = uri.Host;
            if (result.TryGetProperty("title", out var titleElement) && titleElement.ValueKind != JsonValueKind.Null)
            {
                if (titleElement.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("Parallel search returned an invalid result title.");
                }
                var value = titleElement.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    title = value.Length <= 512 ? value : value[..512];
                    wasTruncated |= value.Length > 512;
                }
            }
            var snippet = new StringBuilder();
            foreach (var excerpt in excerpts.EnumerateArray())
            {
                if (excerpt.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("Parallel search returned an invalid excerpt.");
                }
                if (snippet.Length != 0)
                {
                    AppendBounded(snippet, "\n\n", 4_096, ref wasTruncated);
                }
                AppendBounded(snippet, excerpt.GetString()!, 4_096, ref wasTruncated);
            }
            if (sources.Count >= maxResults || !seen.Add(uri.AbsoluteUri))
            {
                continue;
            }
            sources.Add(new AgentToolSourceItem
            {
                Title = title, Url = uri.AbsoluteUri, Snippet = snippet.Length == 0 ? null : snippet.ToString(),
            });
            AppendBounded(content, $"Title: {title}\nURL: {uri.AbsoluteUri}\n{snippet}\n\n",
                ExaWebSearchBackend.MaximumDisplayChars, ref wasTruncated);
        }
        var text = content.ToString().TrimEnd();
        return new WebSearchBackendResult(
            sources.Count == 0 ? "Parallel returned no web search results" : $"Fetched Parallel web search results for '{query}'",
            JsonSerializer.Serialize(new { query, backend = BackendId, content = text, sources, wasTruncated }),
            sources, wasTruncated, BackendId, text);
    }

    private static void AppendBounded(StringBuilder builder, string value, int maximum, ref bool wasTruncated)
    {
        var count = Math.Min(value.Length, maximum - builder.Length);
        builder.Append(value.AsSpan(0, count));
        wasTruncated |= count != value.Length;
    }

    public void Dispose()
    {
        if (_disposeHttpClient) _httpClient.Dispose();
    }
}
