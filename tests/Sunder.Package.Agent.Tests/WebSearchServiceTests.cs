extern alias WebWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using WebWorker::Sunder.Package.Agent.Tools.Web;
using WebWorker::Sunder.Package.Agent.Tools.Web.Backends;
using WebWorker::Sunder.Package.Agent.Tools.Web.Services;
using WebContext = WebWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentToolExecutionContext;
using WebRequest = WebWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentToolRequest;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class WebSearchServiceTests
{
    [Fact]
    public async Task AnonymousParallelSearchUsesStableSessionAndNormalizedBoundedResults()
    {
        using var fixture = new Fixture();
        var session = Guid.NewGuid();
        fixture.Reply = (request, body) => Rpc(body, new
        {
            content = new[] { new { type = "text", text = "provider text is not the structured result" } },
            structuredContent = new { results = new[]
            {
                new { url = "https://example.test/a", title = "First", excerpts = new[] { "one", "two" } },
                new { url = "https://example.test/a", title = "Duplicate", excerpts = Array.Empty<string>() },
                new { url = "https://example.test/b", title = "Second", excerpts = new[] { "three" } },
            } },
        }, sse: true);

        var result = await fixture.Service.SearchAsync("current information", 1, session);
        await fixture.Service.SearchAsync("next query", 5, session);

        Assert.Equal("parallel", result.BackendId);
        var source = Assert.Single(result.Sources);
        Assert.Equal("First", source.Title);
        Assert.Equal("one\n\ntwo", source.Snippet);
        Assert.Contains("https://example.test/a", result.Content);
        Assert.DoesNotContain("provider text", result.Content);
        Assert.All(fixture.Requests, request =>
        {
            Assert.Equal("search.parallel.ai", request.Host);
            Assert.Null(request.Authorization);
            Assert.Equal("sunder-agent-web/1.0.0", request.UserAgent);
            Assert.Empty(request.Query);
        });
        Assert.Equal(2, fixture.Calls.Count);
        Assert.All(fixture.Calls, call => Assert.Equal(session.ToString("N"), call.Body.GetProperty("params").GetProperty("arguments").GetProperty("session_id").GetString()));
        var args = fixture.Calls[0].Body.GetProperty("params").GetProperty("arguments");
        Assert.Equal("current information", args.GetProperty("objective").GetString());
        Assert.Equal("current information", Assert.Single(args.GetProperty("search_queries").EnumerateArray()).GetString());
    }

    [Theory]
    [InlineData("auto", true, "exa")]
    [InlineData("exa", false, "exa")]
    [InlineData("parallel", true, "parallel")]
    [InlineData("invalid", false, "parallel")]
    public async Task SelectionHonorsPinnedProviderAndExistingExaCredentials(string selection, bool keyed, string expected)
    {
        using var fixture = new Fixture();
        fixture.Settings.Values["search.provider"] = selection;
        if (keyed) await fixture.Scope.Context.Secrets.SetSecretAsync("search.exa.apiKey", "exa-key");

        var result = await fixture.Service.SearchAsync("query", 5, null);

        Assert.Equal(expected, result.BackendId);
        Assert.Equal(expected, Assert.Single(fixture.Calls).Provider);
    }

    [Theory]
    [InlineData("seconds")]
    [InlineData("date")]
    [InlineData("invalid")]
    [InlineData("missing")]
    public async Task AutoFailsOverAndSharesCooldownUntilRetryAfterExpires(string header)
    {
        using var fixture = new Fixture();
        fixture.Reply = (request, body) => request.RequestUri!.Host == "search.parallel.ai"
            ? RateLimited(header switch
            {
                "seconds" => "120",
                "date" => fixture.Time.GetUtcNow().AddSeconds(120).ToString("R"),
                "invalid" => "not-a-period",
                _ => null,
            }) : Success(request, body);
        Assert.Equal("exa", (await fixture.Service.SearchAsync("first", 5, Guid.NewGuid())).BackendId);
        Assert.Equal("exa", (await fixture.Service.SearchAsync("second", 5, Guid.NewGuid())).BackendId);
        Assert.Equal(["parallel", "exa", "exa"], fixture.Calls.Select(call => call.Provider));

        fixture.Time.Advance(TimeSpan.FromSeconds(header is "seconds" or "date" ? 120 : 60));
        fixture.Reply = Success;
        Assert.Equal("parallel", (await fixture.Service.SearchAsync("recovered", 5, null)).BackendId);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("false")]
    [InlineData("true")]
    [InlineData("rpc")]
    public async Task ExaQuotaGuidanceTriggersFailoverWithoutBecomingSuccessfulContent(string errorKind)
    {
        using var fixture = new Fixture();
        await fixture.Scope.Context.Secrets.SetSecretAsync("search.exa.apiKey", "exa-key");
        fixture.Reply = (request, body) =>
        {
            if (request.RequestUri!.Host != "mcp.exa.ai") return Success(request, body);
            const string quota = "You've hit Exa's free MCP rate limit. To continue using without limits, create your own Exa API key.";
            if (errorKind == "rpc") return Json(new { jsonrpc = "2.0", id = body.GetProperty("id").GetInt32(), error = new { code = -32000, message = quota } });
            var result = new Dictionary<string, object> { ["content"] = new[] { new { type = "text", text = quota } } };
            if (errorKind != "missing") result["isError"] = errorKind == "true";
            return Rpc(body, result);
        };

        var result = await fixture.Tool.ExecuteAsync(Context(Guid.NewGuid()), Request());

        Assert.False(result.IsError);
        Assert.Equal("parallel", result.BackendId);
        Assert.DoesNotContain("free MCP rate limit", result.Content);
        Assert.Equal(["exa", "parallel"], fixture.Calls.Select(call => call.Provider));
    }

    [Fact]
    public async Task ConcurrentRateLimitResponsesCannotShortenSharedCooldown()
    {
        using var fixture = new Fixture();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        fixture.ReplyAsync = async (request, body, cancellationToken) =>
        {
            if (request.RequestUri!.Host != "search.parallel.ai") return Success(request, body);
            if (Interlocked.Increment(ref attempts) != 1) return RateLimited("120");
            firstStarted.TrySetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
            return RateLimited("30");
        };
        var first = fixture.Service.SearchAsync("first session", 5, Guid.NewGuid());
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("exa", (await fixture.Service.SearchAsync("second session", 5, Guid.NewGuid())).BackendId);
        releaseFirst.TrySetResult();
        Assert.Equal("exa", (await first).BackendId);
        fixture.Time.Advance(TimeSpan.FromSeconds(45));
        Assert.Equal("exa", (await fixture.Service.SearchAsync("third session", 5, Guid.NewGuid())).BackendId);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ExhaustedProvidersFailClearlyAndDoNotRepeatRequestsDuringCooldown()
    {
        using var fixture = new Fixture();
        fixture.Reply = (_, _) => RateLimited(null);
        var first = await fixture.Tool.ExecuteAsync(Context(), Request());
        var second = await fixture.Tool.ExecuteAsync(Context(), Request());

        Assert.True(first.IsError);
        Assert.True(second.IsError);
        Assert.Equal("web-search-rate-limited", first.ErrorCode);
        Assert.Contains("Both web search providers", first.Content);
        Assert.Null(first.Sources);
        Assert.Equal(2, fixture.Calls.Count);
    }

    [Fact]
    public async Task PinnedProviderDoesNotFailOverAndCredentialChangeRecoversImmediately()
    {
        using var fixture = new Fixture();
        fixture.Settings.Values["search.provider"] = "parallel";
        fixture.Reply = (_, _) => RateLimited("120");
        var limited = await fixture.Tool.ExecuteAsync(Context(), Request());
        Assert.True(limited.IsError);
        Assert.Equal("web-search-rate-limited", limited.ErrorCode);
        Assert.Equal("parallel", Assert.Single(fixture.Calls).Provider);

        const string key = "parallel-secret-canary";
        await fixture.Scope.Context.Secrets.SetSecretAsync("search.parallel.apiKey", key);
        fixture.Reply = Success;
        Assert.False((await fixture.Tool.ExecuteAsync(Context(), Request())).IsError);
        Assert.All(fixture.Requests.Skip(4), request => Assert.Equal("Bearer " + key, request.Authorization));
        Assert.All(fixture.Requests, request => Assert.DoesNotContain(key, request.Query));
        fixture.Reply = (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent(key) };
        var failure = await fixture.Tool.ExecuteAsync(Context(), Request());
        Assert.True(failure.IsError);
        Assert.DoesNotContain(key, failure.Content);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(500)]
    [InlineData(200)]
    public async Task AuthenticationServerAndMalformedResponsesDoNotTriggerFailover(int status)
    {
        using var fixture = new Fixture();
        fixture.Reply = (_, body) => status == 200
            ? Rpc(body, new { content = Array.Empty<object>(), structuredContent = new { results = new[] { new { url = "file:///private", excerpts = Array.Empty<string>() } } } })
            : new HttpResponseMessage((HttpStatusCode)status);

        var result = await fixture.Tool.ExecuteAsync(Context(), Request());

        Assert.True(result.IsError);
        Assert.Equal("web-search-http", result.ErrorCode);
        Assert.Equal("parallel", Assert.Single(fixture.Calls).Provider);
    }

    [Fact]
    public async Task ParallelBoundsTitlesExcerptsAndModelContent()
    {
        using var fixture = new Fixture();
        fixture.Reply = (_, body) => Rpc(body, new
        {
            content = Array.Empty<object>(),
            structuredContent = new { results = Enumerable.Range(0, 10).Select(index => new
            {
                url = $"https://example.test/{index}", title = new string('t', 600),
                excerpts = new[] { new string('x', 20_000) },
            }).ToArray() },
        });

        var result = await fixture.Service.SearchAsync("bounded", 10, null);

        Assert.True(result.WasTruncated);
        Assert.InRange(result.Content!.Length, 1, ExaWebSearchBackend.MaximumDisplayChars);
        Assert.All(result.Sources, source => { Assert.Equal(512, source.Title.Length); Assert.Equal(4_096, source.Snippet!.Length); });
    }

    [Fact]
    public async Task CallerCancellationReachesParallelAndNeverStartsFallback()
    {
        using var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.WaitForCancellation = started;
        using var cancellation = new CancellationTokenSource();
        var task = fixture.Service.SearchAsync("cancel", 5, null, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal("parallel", Assert.Single(fixture.Calls).Provider);
    }

    private static WebRequest Request() => new() { ToolId = "web_search", ArgumentsJson = "{\"query\":\"query\"}" };

    private static WebContext Context(Guid? sessionId = null) => new()
    {
        SessionId = sessionId, AllowOutsideConfiguredScope = false, ApprovedResourceCapabilities = [],
        ApprovedResourceClaims = [], ApprovedResourceReferences = [], TranscriptEpoch = 0,
    };

    private static HttpResponseMessage RateLimited(string? retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    private static HttpResponseMessage Success(HttpRequestMessage request, JsonElement body)
        => request.RequestUri!.Host == "mcp.exa.ai"
            ? Rpc(body, new { content = new[] { new { type = "text", text = "Result https://example.test/exa" } } })
            : Rpc(body, new { content = Array.Empty<object>(), structuredContent = new { results = new[] { new { url = "https://example.test/parallel", title = "Parallel result", excerpts = new[] { "current information" } } } } });

    private static HttpResponseMessage Rpc(JsonElement body, object result, bool sse = false)
        => Json(new { jsonrpc = "2.0", id = body.GetProperty("id").GetInt32(), result }, sse);

    private static HttpResponseMessage Json(object value, bool sse = false)
    {
        var json = JsonSerializer.Serialize(value);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse ? "event: message\ndata: " + json + "\n\n" : json, Encoding.UTF8,
                sse ? "text/event-stream" : "application/json"),
        };
    }

    private sealed class Fixture : IDisposable
    {
        public RegressionTestPackageScope Scope { get; } = RegressionTestPackageScope.Create();
        public MemorySettings Settings { get; } = new();
        public TestTime Time { get; } = new();
        public List<RequestSnapshot> Requests { get; } = [];
        public List<(string Provider, JsonElement Body)> Calls { get; } = [];
        public Func<HttpRequestMessage, JsonElement, HttpResponseMessage> Reply { get; set; } = Success;
        public Func<HttpRequestMessage, JsonElement, CancellationToken, Task<HttpResponseMessage>>? ReplyAsync { get; set; }
        public TaskCompletionSource? WaitForCancellation { get; set; }
        public WebSearchService Service { get; }
        public WebSearchTool Tool { get; }
        private readonly HttpClient _http;
        private readonly ExaWebSearchBackend _exa;
        private readonly ParallelWebSearchBackend _parallel;

        public Fixture()
        {
            var settings = new WebToolsSettingsService(new SettingsContext(Scope.Context, Settings));
            _http = new HttpClient(new Handler(this)) { Timeout = Timeout.InfiniteTimeSpan };
            _exa = new ExaWebSearchBackend(settings, _http);
            _parallel = new ParallelWebSearchBackend(settings, _http);
            Service = new WebSearchService(_exa, _parallel, settings, Time);
            Tool = new WebSearchTool(Service, settings);
        }

        public void Dispose() { _exa.Dispose(); _parallel.Dispose(); _http.Dispose(); Scope.Dispose(); }
    }

    private sealed record RequestSnapshot(string Host, string Query, string? Authorization, string UserAgent);

    private sealed class Handler(Fixture fixture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = document.RootElement;
            var method = body.GetProperty("method").GetString();
            fixture.Requests.Add(new(request.RequestUri!.Host, request.RequestUri.Query,
                request.Headers.Authorization?.ToString(), request.Headers.UserAgent.ToString()));
            Assert.Contains("application/json", request.Headers.Accept.ToString());
            Assert.Equal(method, Assert.Single(request.Headers.GetValues("Mcp-Method")));
            if (method != "initialize")
            {
                Assert.Equal(WebSearchMcpClient.RequestedProtocolVersion, Assert.Single(request.Headers.GetValues("MCP-Protocol-Version")));
                Assert.Equal("test-session", Assert.Single(request.Headers.GetValues("MCP-Session-Id")));
            }
            switch (method)
            {
                case "initialize":
                    var response = Rpc(body, new { protocolVersion = WebSearchMcpClient.RequestedProtocolVersion, capabilities = new { tools = new { } } });
                    response.Headers.Add("MCP-Session-Id", "test-session");
                    return response;
                case "notifications/initialized":
                    return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new ByteArrayContent([]) };
                case "tools/list":
                    return Rpc(body, new { tools = new[] { new { name = request.RequestUri.Host == "mcp.exa.ai" ? "web_search_exa" : "web_search" } } });
                case "tools/call":
                    fixture.Calls.Add((request.RequestUri.Host == "mcp.exa.ai" ? "exa" : "parallel", body.Clone()));
                    if (fixture.WaitForCancellation is { } started)
                    {
                        started.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    return fixture.ReplyAsync is { } reply
                        ? await reply(request, body, cancellationToken) : fixture.Reply(request, body);
                default: throw new InvalidOperationException(method);
            }
        }
    }

    private sealed class TestTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class MemorySettings : IPackageSettings
    {
        public Dictionary<string, string> Values { get; } = [];
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(Values.GetValueOrDefault(key));
        public Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default) => GetValueAsync(key, cancellationToken);
        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default) { Values[key] = value; return Task.CompletedTask; }
        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default) { Values.Remove(key); return Task.CompletedTask; }
    }

    private sealed class SettingsContext(IPackageContext inner, IPackageSettings settings) : IPackageContext
    {
        public string PackageId => inner.PackageId;
        public string Version => inner.Version;
        public string ContentRootPath => inner.ContentRootPath;
        public IPackageStorageContext Storage => inner.Storage;
        public IPackageSettings Settings => settings;
        public IPackageSecrets Secrets => inner.Secrets;
        public IPackageLogging Logging => inner.Logging;
    }
}
