extern alias WebWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using WebWorker::Sunder.Package.Agent.Contracts.Models;
using WebWorker::Sunder.Package.Agent.Tools.Web;
using WebWorker::Sunder.Package.Agent.Tools.Web.Backends;
using WebWorker::Sunder.Package.Agent.Tools.Web.Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class ExaWebSearchTransportTests
{
    [Fact]
    public async Task SearchBoundsDisplayAndStructuredContentAndExtractsSources()
    {
        using var scope = RegressionTestPackageScope.Create();
        var text = "Source https://example.test/result "
                   + new string('x', ExaWebSearchBackend.MaximumDisplayChars + 100);
        using var httpClient = CreateMcpClient((request, _) => RpcResult(
            request,
            new { content = new[] { new { type = "text", text } }, isError = false },
            sse: true));
        var backend = new ExaWebSearchBackend(
            new WebToolsSettingsService(scope.Context),
            httpClient,
            TimeSpan.FromSeconds(1));

        var result = await backend.SearchAsync("bounded search", 100);

        Assert.True(result.WasTruncated);
        Assert.Equal(ExaWebSearchBackend.MaximumDisplayChars, result.Content?.Length);
        var source = Assert.Single(result.Sources);
        Assert.Equal("https://example.test/result", source.Url);
        using var structured = JsonDocument.Parse(result.StructuredPayloadJson);
        Assert.True(structured.RootElement.GetProperty("wasTruncated").GetBoolean());
        Assert.InRange(
            structured.RootElement.GetProperty("content").GetString()!.Length,
            1,
            ExaWebSearchBackend.MaximumStructuredContentChars);
        Assert.False(structured.RootElement.TryGetProperty("raw", out _));
    }

    [Fact]
    public async Task EventFirstSseResponseIsParsed()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var httpClient = CreateMcpClient((request, _) => RpcResult(
            request,
            new { content = new[] { new { type = "text", text = "event-first result" } } },
            sse: true,
            eventFirst: true));
        var backend = new ExaWebSearchBackend(
            new WebToolsSettingsService(scope.Context),
            httpClient,
            TimeSpan.FromSeconds(1));

        var result = await backend.SearchAsync("event first", 5);

        Assert.Equal("event-first result", result.Content);
    }

    [Fact]
    public async Task ChunkedOversizedResponseStopsAtBoundAndDisposesStream()
    {
        using var scope = RegressionTestPackageScope.Create();
        var stream = new TrackingMemoryStream(new byte[ExaWebSearchBackend.MaximumRawResponseBytes + 1]);
        using var httpClient = CreateMcpClient((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnlengthContent(stream),
        });
        var backend = new ExaWebSearchBackend(
            new WebToolsSettingsService(scope.Context),
            httpClient,
            TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<InvalidDataException>(() => backend.SearchAsync("oversized", 5));

        Assert.True(stream.WasDisposed);
        Assert.InRange(stream.FinalPosition, 1, ExaWebSearchBackend.MaximumRawResponseBytes + 1L);
    }

    [Theory]
    [InlineData("data: {not-json\n\n")]
    [InlineData("{not-json")]
    public async Task MalformedSseOrJsonFailsClosed(string payload)
    {
        using var scope = RegressionTestPackageScope.Create();
        using var httpClient = CreateMcpClient((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                payload,
                Encoding.UTF8,
                payload.StartsWith("data:", StringComparison.Ordinal)
                    ? "text/event-stream"
                    : "application/json"),
        });
        var backend = new ExaWebSearchBackend(
            new WebToolsSettingsService(scope.Context),
            httpClient,
            TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<JsonException>(() => backend.SearchAsync("malformed", 5));
    }

    [Fact]
    public async Task CallerCancellationReachesResponseWait()
    {
        using var scope = RegressionTestPackageScope.Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var httpClient = CreateMcpClient(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var backend = new ExaWebSearchBackend(
            new WebToolsSettingsService(scope.Context),
            httpClient,
            TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();

        var search = backend.SearchAsync("cancel", 5, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
    }

    [Fact]
    public async Task TimeoutAndNonSuccessDiagnosticsDoNotExposeCredential()
    {
        using var scope = RegressionTestPackageScope.Create();
        const string credential = "exa-secret-canary";
        await Assert.IsType<RegressionTestSecrets>(scope.Context.Secrets)
            .SetSecretAsync("search.exa.apiKey", credential);
        Uri? requestedUri = null;
        using var failingClient = CreateMcpClient((request, _) =>
        {
            requestedUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("backend echoed " + credential),
            };
        });
        var failingBackend = new ExaWebSearchBackend(
            new WebToolsSettingsService(scope.Context),
            failingClient,
            TimeSpan.FromSeconds(1));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() =>
            failingBackend.SearchAsync("failure", 5));

        Assert.Contains(credential, requestedUri!.Query, StringComparison.Ordinal);
        Assert.DoesNotContain(credential, failure.ToString(), StringComparison.Ordinal);

        using var timeoutClient = CreateMcpClient(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var timeoutBackend = new ExaWebSearchBackend(
            new WebToolsSettingsService(scope.Context),
            timeoutClient,
            TimeSpan.FromMilliseconds(25));
        await Assert.ThrowsAsync<TimeoutException>(() => timeoutBackend.SearchAsync("timeout", 5));
    }

    [Fact]
    public async Task SearchPerformsInitializeNotificationPaginatedDiscoveryAndNegotiatedCall()
    {
        using var scope = RegressionTestPackageScope.Create();
        var requests = new List<McpRequestSnapshot>();
        var listPage = 0;
        using var httpClient = CreateClient((request, _) =>
        {
            var method = RequestMethod(request);
            requests.Add(new McpRequestSnapshot(
                method,
                Header(request, "MCP-Protocol-Version"),
                Header(request, "MCP-Session-Id"),
                Header(request, "Mcp-Method")));
            switch (method)
            {
                case "initialize":
                    var initialized = RpcResult(request, new
                    {
                        protocolVersion = "2025-03-26",
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "exa", version = "test" },
                    });
                    initialized.Headers.TryAddWithoutValidation("MCP-Session-Id", "exa-session");
                    return initialized;
                case "notifications/initialized":
                    return EmptyAccepted();
                case "tools/list":
                    return ++listPage == 1
                        ? RpcResult(request, new
                        {
                            tools = new[] { new { name = "other_tool" } },
                            nextCursor = "next-page",
                        })
                        : RpcResult(request, new
                        {
                            tools = new[] { new { name = "web_search_exa" } },
                        });
                case "tools/call":
                    using (var document = RequestDocument(request))
                    {
                        Assert.Equal(
                            10,
                            document.RootElement.GetProperty("params")
                                .GetProperty("arguments")
                                .GetProperty("numResults")
                                .GetInt32());
                    }
                    return RpcResult(request, new
                    {
                        content = new[] { new { type = "text", text = "negotiated result" } },
                    });
                default:
                    throw new InvalidOperationException(method);
            }
        });
        var backend = new ExaWebSearchBackend(
            new WebToolsSettingsService(scope.Context),
            httpClient,
            TimeSpan.FromSeconds(1));

        var result = await backend.SearchAsync("negotiated", 100);

        Assert.Equal("negotiated result", result.Content);
        Assert.Equal(
            ["initialize", "notifications/initialized", "tools/list", "tools/list", "tools/call"],
            requests.Select(request => request.Method));
        Assert.Null(requests[0].ProtocolVersion);
        Assert.Null(requests[0].SessionId);
        Assert.All(requests.Skip(1), request => Assert.Equal("2025-03-26", request.ProtocolVersion));
        Assert.All(requests.Skip(1), request => Assert.Equal("exa-session", request.SessionId));
        Assert.All(requests, request => Assert.Equal(request.Method, request.MethodHeader));
    }

    [Fact]
    public async Task ToolDiscoveryStopsAtBoundedPageAndHttpCallLimits()
    {
        using var scope = RegressionTestPackageScope.Create();
        var requestCount = 0;
        var listPage = 0;
        using var httpClient = CreateClient((request, _) =>
        {
            requestCount++;
            return RequestMethod(request) switch
            {
                "initialize" => InitializeResult(request),
                "notifications/initialized" => EmptyAccepted(),
                "tools/list" => RpcResult(request, new
                {
                    tools = new[] { new { name = $"other-{++listPage}" } },
                    nextCursor = $"cursor-{listPage}",
                }),
                _ => throw new InvalidOperationException("tools/call must not run without discovery."),
            };
        });
        var backend = new ExaWebSearchBackend(
            new WebToolsSettingsService(scope.Context),
            httpClient,
            TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            backend.SearchAsync("bounded pages", 5));

        Assert.Contains("page limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ExaMcpClient.MaximumToolPages, listPage);
        Assert.Equal(2 + ExaMcpClient.MaximumToolPages, requestCount);
        Assert.True(requestCount < ExaMcpClient.MaximumHttpCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task JsonRpcAndToolErrorsFailInsteadOfReturningSuccessfulSearch(bool protocolError)
    {
        using var scope = RegressionTestPackageScope.Create();
        using var httpClient = CreateMcpClient((request, _) => protocolError
            ? RpcError(request, -32602)
            : RpcResult(request, new
            {
                content = new[] { new { type = "text", text = "error detail" } },
                isError = true,
            }));
        var backend = new ExaWebSearchBackend(
            new WebToolsSettingsService(scope.Context),
            httpClient,
            TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            backend.SearchAsync("must fail", 5));

        Assert.Contains(protocolError ? "JSON-RPC error" : "tool error", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SearchSchemaMatchesBackendMaximum()
    {
        using var scope = RegressionTestPackageScope.Create();
        var backend = new ExaWebSearchBackend(new WebToolsSettingsService(scope.Context));
        var tool = new WebSearchTool(backend, new WebToolsSettingsService(scope.Context));
        using var schema = JsonDocument.Parse(tool.Descriptor.ArgumentsJsonSchema!);

        Assert.Equal(
            ExaWebSearchBackend.MaximumResultCount,
            schema.RootElement.GetProperty("properties").GetProperty("maxResults").GetProperty("maximum").GetInt32());
    }

    private static HttpClient CreateMcpClient(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> callResponse)
        => CreateMcpClient((request, cancellationToken) =>
            Task.FromResult(callResponse(request, cancellationToken)));

    private static HttpClient CreateMcpClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callResponse)
        => CreateClient(async (request, cancellationToken) =>
        {
            return RequestMethod(request) switch
            {
                "initialize" => InitializeResult(request),
                "notifications/initialized" => EmptyAccepted(),
                "tools/list" => RpcResult(request, new
                {
                    tools = new[] { new { name = "web_search_exa" } },
                }),
                "tools/call" => await callResponse(request, cancellationToken),
                var method => throw new InvalidOperationException($"Unexpected MCP method '{method}'."),
            };
        });

    private static HttpResponseMessage InitializeResult(HttpRequestMessage request)
    {
        var response = RpcResult(request, new
        {
            protocolVersion = ExaMcpClient.RequestedProtocolVersion,
            capabilities = new { tools = new { } },
            serverInfo = new { name = "exa", version = "test" },
        });
        response.Headers.TryAddWithoutValidation("MCP-Session-Id", "test-session");
        return response;
    }

    private static HttpResponseMessage RpcResult(
        HttpRequestMessage request,
        object result,
        bool sse = false,
        bool eventFirst = false)
    {
        var envelope = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = RequestId(request),
            result,
        });
        if (!sse)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
            };
        }
        var prefix = eventFirst
            ? "event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{}}\n\n"
              + "event: message\nid: result-1\n"
            : string.Empty;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(prefix + "data: " + envelope + "\n\n", Encoding.UTF8, "text/event-stream"),
        };
    }

    private static HttpResponseMessage RpcError(HttpRequestMessage request, int code)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = RequestId(request),
                error = new { code, message = "sensitive server detail" },
            }), Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage EmptyAccepted()
        => new(HttpStatusCode.Accepted) { Content = new ByteArrayContent([]) };

    private static string RequestMethod(HttpRequestMessage request)
    {
        using var document = RequestDocument(request);
        return document.RootElement.GetProperty("method").GetString()
               ?? throw new InvalidDataException("Test MCP request omitted its method.");
    }

    private static int RequestId(HttpRequestMessage request)
    {
        using var document = RequestDocument(request);
        return document.RootElement.GetProperty("id").GetInt32();
    }

    private static JsonDocument RequestDocument(HttpRequestMessage request)
        => JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());

    private static string? Header(HttpRequestMessage request, string name)
        => request.Headers.TryGetValues(name, out var values) ? Assert.Single(values) : null;

    private static HttpClient CreateClient(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response)
        => CreateClient((request, cancellationToken) =>
            Task.FromResult(response(request, cancellationToken)));

    private static HttpClient CreateClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
        => new(new StubHandler(response)) { Timeout = Timeout.InfiniteTimeSpan };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => response(request, cancellationToken);
    }

    private sealed class UnlengthContent(Stream stream) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context)
            => stream.CopyToAsync(target);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult(stream);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                stream.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class TrackingMemoryStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool WasDisposed { get; private set; }
        public long FinalPosition { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (!WasDisposed)
            {
                FinalPosition = Position;
                WasDisposed = true;
            }
            base.Dispose(disposing);
        }
    }

    private sealed record McpRequestSnapshot(
        string Method,
        string? ProtocolVersion,
        string? SessionId,
        string? MethodHeader);
}
