using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    // The real worker must never discover models on a developer's LM Studio.
    private sealed class LMStudioLoopbackCatalog : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;
        internal ConcurrentQueue<(string Method, string Path, string? Authorization)> Requests { get; } = new();
        internal string Endpoint { get; }

        internal LMStudioLoopbackCatalog()
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            Endpoint = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Endpoint);
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
                    try
                    {
                        var request = context.Request;
                        Requests.Enqueue((request.HttpMethod, request.RawUrl!, request.Headers["Authorization"]));
                        if (request.HttpMethod != "GET" || request.RawUrl != "/api/v1/models")
                        {
                            context.Response.StatusCode = 400;
                            continue;
                        }
                        var body = Encoding.UTF8.GetBytes("""
                            {"models":[{"key":"artifact","type":"llm","max_context_length":131072,
                              "loaded_instances":[{"config":{"context_length":16384}}],
                              "capabilities":{"vision":true,"trained_for_tool_use":false}}]}
                            """);
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = body.Length;
                        await context.Response.OutputStream.WriteAsync(body, _stop.Token);
                    }
                    finally { context.Response.Close(); }
                }
            }
            catch (Exception error) when (_stop.IsCancellationRequested
                                          && error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try { await _serve.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { _listener.Close(); _stop.Dispose(); }
        }
    }
}
