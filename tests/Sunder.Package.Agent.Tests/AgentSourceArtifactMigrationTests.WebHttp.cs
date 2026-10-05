using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private sealed class WebHttpPeer
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<WebRequestGate>> _gates = new(StringComparer.Ordinal);
        private int _rebindings;
        internal ConcurrentQueue<string> Hosts { get; } = new();
        internal ConcurrentQueue<Uri> Fetches { get; } = new();
        internal ConcurrentQueue<(Uri Uri, string Method, JsonElement Body)> Searches { get; } = new();
        internal string SearchMode { get; set; } = "normal";
        internal WebTrackedStream? LastLargeStream;
        internal WebRequestGate BlockNext(string key)
        {
            var gate = new WebRequestGate(); _gates.GetOrAdd(key, static _ => new()).Enqueue(gate); return gate;
        }
        private async Task Wait(string key, CancellationToken token)
        {
            if (!_gates.TryGetValue(key, out var gates) || !gates.TryDequeue(out var gate)) return;
            gate.Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { gate.Cancelled.TrySetResult(); throw; }
        }
        internal async Task<IPAddress[]> Resolve(string host, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Hosts.Enqueue(host); await Wait("dns", token);
            return host switch
            {
                "mixed.example" => [IPAddress.Parse("93.184.216.34"), IPAddress.Loopback],
                "empty.example" => [],
                "rebind.example" when Interlocked.Increment(ref _rebindings) > 1 => [IPAddress.Parse("10.0.0.5")],
                _ => [IPAddress.Parse("93.184.216.34")],
            };
        }
        internal async Task<HttpResponseMessage> Fetch(Uri uri, IReadOnlyList<IPAddress> addresses, HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal(uri, request.RequestUri); Assert.Equal(IPAddress.Parse("93.184.216.34"), Assert.Single(addresses));
            Fetches.Enqueue(uri); await Wait("fetch", token);
            if (uri.Host == "rebind.example" || uri.AbsolutePath == "/loop") return Redirect("/loop");
            return uri.AbsolutePath switch
            {
                "/start" => Redirect("/final"),
                "/private" => Redirect("http://127.0.0.1/admin"),
                "/scheme" => Redirect("file:///etc/passwd"),
                "/large" => Large(5 * 1024 * 1024 + 16384),
                "/missing" => new(HttpStatusCode.NotFound) { Content = new StringContent("not found") },
                _ => new(HttpStatusCode.OK) { Content = new StringContent("<html><title>Artifact Page</title><script>do-not-render</script><p>Hello &amp; world</p></html>", Encoding.UTF8, "text/html") },
            };
        }
        internal async Task<HttpResponseMessage> Search(HttpRequestMessage request, CancellationToken token)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var value = document.RootElement; var method = value.GetProperty("method").GetString()!;
            Assert.Equal("https", request.RequestUri!.Scheme); Assert.Equal("mcp.exa.ai", request.RequestUri.Host);
            Assert.Equal(method, Assert.Single(request.Headers.GetValues("Mcp-Method")));
            Searches.Enqueue((request.RequestUri, method, value.Clone()));
            if (method != "initialize")
            {
                Assert.Equal("2025-06-18", Assert.Single(request.Headers.GetValues("MCP-Protocol-Version")));
                Assert.Equal("artifact-session", Assert.Single(request.Headers.GetValues("MCP-Session-Id")));
            }
            if (method == "notifications/initialized") return new(HttpStatusCode.Accepted) { Content = new ByteArrayContent([]) };
            var id = value.GetProperty("id").GetInt32();
            if (method == "initialize")
            {
                var response = Result(id, new { protocolVersion = "2025-06-18", capabilities = new { tools = new { } }, serverInfo = new { name = "artifact", version = "1" } });
                response.Headers.Add("MCP-Session-Id", "artifact-session"); return response;
            }
            if (method == "tools/list")
                return !value.GetProperty("params").TryGetProperty("cursor", out _)
                    ? Result(id, new { tools = new[] { new { name = "other" } }, nextCursor = "next" })
                    : Result(id, new { tools = new[] { new { name = "web_search_exa" } } });
            Assert.Equal("tools/call", method);
            Assert.Equal("web_search_exa", value.GetProperty("params").GetProperty("name").GetString());
            await Wait("search", token);
            return SearchMode switch
            {
                "redirect" => Redirect("https://elsewhere.example/?secret=do-not-echo"),
                "malformed" => new(HttpStatusCode.OK) { Content = new StringContent("data: {not-json\n\n", Encoding.UTF8, "text/event-stream") },
                "wrong-id" => Result(id + 1, new { content = Array.Empty<object>() }),
                "tool-error" => Result(id, new { isError = true, content = new[] { new { type = "text", text = "exa-secret-canary" } } }),
                "large-raw" => Large(2 * 1024 * 1024 + 1),
                _ => Result(id, new { content = new[] { new { type = "text", text = "Result https://example.test/a and https://example.test/a.\n"
                    + (SearchMode == "large-display" ? new string('x', 70000) : "More https://example.test/b") } }, isError = false }, sse: true),
            };
        }
        private HttpResponseMessage Large(int length)
        {
            LastLargeStream = new WebTrackedStream(Encoding.UTF8.GetBytes(new string('x', length)));
            return new(HttpStatusCode.OK) { Content = new WebUnlengthContent(LastLargeStream) };
        }
        private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };
        private static HttpResponseMessage Result(int id, object result, bool sse = false)
        {
            var json = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
            return new(HttpStatusCode.OK) { Content = new StringContent(sse ? "event: message\ndata: " + json + "\n\n" : json, Encoding.UTF8, sse ? "text/event-stream" : "application/json") };
        }
    }
    private sealed class WebRequestGate
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class WebTrackedStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        internal bool WasDisposed;
        internal long FinalPosition;
        protected override void Dispose(bool disposing)
        {
            if (disposing && !WasDisposed) { FinalPosition = Position; WasDisposed = true; }
            base.Dispose(disposing);
        }
    }
    private sealed class WebUnlengthContent(Stream stream) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => stream.CopyToAsync(target);
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
        protected override void Dispose(bool disposing) { if (disposing) stream.Dispose(); base.Dispose(disposing); }
    }
}
