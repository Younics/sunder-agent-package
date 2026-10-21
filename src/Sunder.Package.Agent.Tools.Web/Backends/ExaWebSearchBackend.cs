using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using Sunder.Package.Agent.Tools.Web.Services;

namespace Sunder.Package.Agent.Tools.Web.Backends;

public sealed class ExaWebSearchBackend : IDisposable
{
    internal const int MaximumResultCount = 10;
    internal const int MaximumRawResponseBytes = 2 * 1024 * 1024;
    internal const int MaximumDisplayChars = 32 * 1024;
    internal const int MaximumStructuredContentChars = 64 * 1024;
    private const int MaximumParsedContentChars = 128 * 1024;
    private const int MaximumSources = 20;
    private const int MaximumSourceUrlChars = 2_048;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly Regex UrlPattern = new(
        @"https?://[^\s<>\]\[\)\(\}\{\""']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private readonly WebToolsSettingsService _settingsService;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;
    private readonly bool _disposeHttpClient;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public ExaWebSearchBackend(WebToolsSettingsService settingsService)
        : this(settingsService, CreateHttpClient(), DefaultTimeout, disposeHttpClient: true)
    {
    }

    public ExaWebSearchBackend(WebToolsSettingsService settingsService, HttpClient httpClient)
        : this(settingsService, httpClient, DefaultTimeout, disposeHttpClient: false)
    {
    }

    internal ExaWebSearchBackend(
        WebToolsSettingsService settingsService,
        HttpClient httpClient,
        TimeSpan timeout)
        : this(settingsService, httpClient, timeout, disposeHttpClient: false)
    {
    }

    private ExaWebSearchBackend(
        WebToolsSettingsService settingsService,
        HttpClient httpClient,
        TimeSpan timeout,
        bool disposeHttpClient)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(httpClient);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _settingsService = settingsService;
        _httpClient = httpClient;
        _timeout = timeout;
        _disposeHttpClient = disposeHttpClient;
    }

    public string BackendId { get; } = "exa";

    public async ValueTask<AgentToolReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new AgentToolReadiness
        {
            ToolId = "web_search", Status = AgentToolReadinessStatus.Ready,
            Message = string.IsNullOrWhiteSpace(await _settingsService.GetExaApiKeyAsync(cancellationToken))
                ? "Web search is ready via the default Exa MCP-backed route."
                : "Web search is ready via the Exa MCP-backed route using your optional Exa API key.",
        };
    }

    public async Task<WebSearchBackendResult> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken = default)
        => await SearchAsync(query, maxResults,
            await _settingsService.GetExaApiKeyAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

    internal async Task<WebSearchBackendResult> SearchAsync(
        string query,
        int maxResults,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);
        var requestCancellationToken = timeoutSource.Token;
        try
        {
            var endpoint = BuildEndpointUri(apiKey);
            var response = await new WebSearchMcpClient(
                _httpClient, endpoint, MaximumRawResponseBytes, BackendId, "web_search_exa")
                .CallAsync(new
                {
                    query,
                    type = "auto",
                    numResults = Math.Clamp(maxResults, 1, MaximumResultCount),
                    livecrawl = "fallback",
                }, requestCancellationToken).ConfigureAwait(false);
            var parsed = JoinBounded(response.Text, wasTruncated: false);
            var content = Truncate(parsed.Content, MaximumDisplayChars, out var displayTruncated);
            var structuredContent = Truncate(
                parsed.Content,
                MaximumStructuredContentChars,
                out var structuredTruncated);
            var sources = ExtractSources(parsed.Content);
            var wasTruncated = parsed.WasTruncated || displayTruncated || structuredTruncated;
            var structuredPayload = JsonSerializer.Serialize(new
            {
                query,
                backend = BackendId,
                content = structuredContent,
                sources,
                wasTruncated,
            });

            return new WebSearchBackendResult(
                Summary: string.IsNullOrWhiteSpace(content)
                    ? "Exa returned no web search results"
                    : $"Fetched Exa-backed web search results for '{query}'",
                StructuredPayloadJson: structuredPayload,
                Sources: sources,
                WasTruncated: wasTruncated,
                BackendId: BackendId,
                Content: content);
        }
        catch (OperationCanceledException) when (
            timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Exa did not respond within {_timeout.TotalSeconds:0.###} seconds.");
        }
    }

    public void Dispose()
    {
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static Uri BuildEndpointUri(string? apiKey)
        => string.IsNullOrWhiteSpace(apiKey)
            ? new Uri("https://mcp.exa.ai/mcp")
            : new Uri($"https://mcp.exa.ai/mcp?exaApiKey={Uri.EscapeDataString(apiKey)}");

    private static ParsedSearchContent JoinBounded(IReadOnlyList<string> parts, bool wasTruncated)
    {
        if (parts.Count == 0)
        {
            return new ParsedSearchContent(null, wasTruncated);
        }

        var builder = new StringBuilder(Math.Min(MaximumParsedContentChars, parts.Sum(part => part.Length)));
        foreach (var part in parts)
        {
            var separatorLength = builder.Length == 0 ? 0 : 2;
            var remaining = MaximumParsedContentChars - builder.Length - separatorLength;
            if (remaining <= 0)
            {
                wasTruncated = true;
                break;
            }
            if (separatorLength != 0)
            {
                builder.Append("\n\n");
            }
            if (part.Length > remaining)
            {
                builder.Append(part.AsSpan(0, remaining));
                wasTruncated = true;
                break;
            }
            builder.Append(part);
        }

        return new ParsedSearchContent(builder.ToString(), wasTruncated);
    }

    private static IReadOnlyList<AgentToolSourceItem> ExtractSources(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var sources = new List<AgentToolSourceItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in UrlPattern.Matches(content))
        {
            var value = match.Value.TrimEnd('.', ',', ';', ':', '!', '?');
            if (value.Length > MaximumSourceUrlChars
                || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https")
                || !seen.Add(uri.AbsoluteUri))
            {
                continue;
            }

            sources.Add(new AgentToolSourceItem { Title = uri.Host, Url = uri.AbsoluteUri, Snippet = null });
            if (sources.Count == MaximumSources)
            {
                break;
            }
        }
        return sources;
    }

    private static string? Truncate(string? value, int maximumChars, out bool wasTruncated)
    {
        wasTruncated = value?.Length > maximumChars;
        return wasTruncated ? value![..maximumChars] : value;
    }

    private static HttpClient CreateHttpClient()
        => new(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

    private sealed record ParsedSearchContent(string? Content, bool WasTruncated);
}
