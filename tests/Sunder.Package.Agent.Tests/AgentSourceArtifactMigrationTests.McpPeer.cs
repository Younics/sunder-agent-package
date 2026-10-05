using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private sealed class McpLoopbackPeer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _requests = [];
        private readonly Task _serve;
        internal int ToolCalls;
        internal TaskCompletionSource BlockedCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string Endpoint { get; }
        internal McpLoopbackPeer()
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            Endpoint = $"http://127.0.0.1:{port}/mcp";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _serve = Serve();
        }
        private async Task Serve()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().WaitAsync(_stop.Token);
                    _requests.Add(Handle(context));
                }
            }
            catch (Exception error) when (_stop.IsCancellationRequested && error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
        }
        private async Task Handle(HttpListenerContext context)
        {
            try
            {
                if (context.Request.HttpMethod != "POST") { context.Response.StatusCode = 405; return; }
                if (context.Request.ContentLength64 > 1024 * 1024) { context.Response.StatusCode = 413; return; }
                using var request = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: _stop.Token);
                var value = request.RootElement;
                if (!value.TryGetProperty("id", out var id)) { context.Response.StatusCode = 202; return; }
                var method = value.GetProperty("method").GetString();
                object result;
                if (method == "initialize") result = new
                {
                    protocolVersion = value.GetProperty("params").GetProperty("protocolVersion").GetString(),
                    capabilities = new { tools = new { listChanged = false } }, serverInfo = new { name = "sunder-artifact-test", version = "1.0.0" },
                };
                else if (method == "tools/list") result = new { tools = new[] { new
                {
                    name = "echo", description = "Echo artifact text", inputSchema = new { type = "object", properties = new { message = new { type = "string" } }, required = new[] { "message" } },
                    annotations = new { readOnlyHint = true },
                } } };
                else if (method == "tools/call")
                {
                    Interlocked.Increment(ref ToolCalls);
                    var message = value.GetProperty("params").GetProperty("arguments").GetProperty("message").GetString();
                    if (message == "wait") { BlockedCall.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, _stop.Token); }
                    result = new { content = new[] { new { type = "text", text = "artifact:" + message } }, isError = false };
                }
                else throw new InvalidOperationException("Unexpected MCP fixture method: " + method);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = id.Clone(), result });
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally { context.Response.Close(); }
        }
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync(); _listener.Stop();
            try { await _serve; await Task.WhenAll(_requests); }
            finally { _listener.Close(); _stop.Dispose(); }
        }
    }
}
