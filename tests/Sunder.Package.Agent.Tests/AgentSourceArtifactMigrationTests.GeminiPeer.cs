using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private sealed class GeminiLoopbackPeer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;
        internal ConcurrentQueue<(string Path, JsonElement Body)> Requests { get; } = new();
        internal string Endpoint { get; }
        internal GeminiLoopbackPeer()
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            Endpoint = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Endpoint); _listener.Start(); _serve = Serve();
        }
        private async Task Serve()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().WaitAsync(_stop.Token);
                    try
                    {
                        if (context.Request.HttpMethod != "POST" || context.Request.ContentLength64 is < 0 or > 1024 * 1024)
                        { context.Response.StatusCode = 400; continue; }
                        using var request = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: _stop.Token);
                        var path = context.Request.RawUrl!; Requests.Enqueue((path, request.RootElement.Clone()));
                        var stream = path.Contains(":streamGenerateContent", StringComparison.Ordinal);
                        var body = path.Contains(":batchEmbedContents", StringComparison.Ordinal) ? "{\"embeddings\":[{\"values\":[0.25,0.5,0.75]}]}"
                            : stream ? "data: {\"responseId\":\"response.artifact\",\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"artifact response\"}]},\"finishReason\":\"STOP\"}]}\n\n"
                            : "{\"responseId\":\"response.tool\",\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"thought\":true,\"text\":\"Checking.\",\"thoughtSignature\":\"AQI=\"},{\"functionCall\":{\"id\":\"call.artifact\",\"name\":\"read_file\",\"args\":{\"path\":\"README.md\"}}}]},\"finishReason\":\"STOP\"}]}";
                        var bytes = Encoding.UTF8.GetBytes(body);
                        context.Response.ContentType = stream ? "text/event-stream" : "application/json";
                        context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes, _stop.Token);
                    }
                    finally { context.Response.Close(); }
                }
            }
            catch (Exception error) when (_stop.IsCancellationRequested && error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync(); _listener.Stop();
            try { await _serve.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { _listener.Close(); _stop.Dispose(); }
        }
    }
}
