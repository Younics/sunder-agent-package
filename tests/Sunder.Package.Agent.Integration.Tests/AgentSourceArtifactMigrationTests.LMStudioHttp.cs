using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private sealed class LMStudioArtifactClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        internal void Advance() => Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(11).Ticks);
    }

    private sealed class LMStudioArtifactHttp : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<LMStudioHttpGate>> _gates = new(StringComparer.Ordinal);
        internal ConcurrentQueue<LMStudioHttpRequest> Requests { get; } = new();
        internal string CatalogMode { get; set; } = "normal";
        internal string ChatMode { get; set; } = "normal";
        internal string EmbeddingMode { get; set; } = "normal";
        internal int ModelRequests;
        internal bool IsDisposed;

        internal LMStudioHttpGate BlockNext(string resource, string? response = null)
        {
            var gate = new LMStudioHttpGate(response);
            _gates.GetOrAdd(resource, static _ => new()).Enqueue(gate);
            return gate;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this); token.ThrowIfCancellationRequested();
            JsonElement? body = request.Content is null ? null : JsonSerializer.Deserialize<JsonElement>(await request.Content.ReadAsStringAsync(token));
            Requests.Enqueue(new(request.RequestUri!, request.Headers.Authorization?.ToString(), body));
            var resource = request.RequestUri!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) ? "chat/completions"
                : request.RequestUri.AbsolutePath.EndsWith("/embeddings", StringComparison.Ordinal) ? "embeddings"
                : request.RequestUri.AbsolutePath.EndsWith("/models", StringComparison.Ordinal) ? "models"
                : throw new InvalidOperationException("Unexpected LM Studio artifact request: " + request.RequestUri.AbsolutePath);
            if (resource == "models") Interlocked.Increment(ref ModelRequests);
            var catalogMode = CatalogMode; var chatMode = ChatMode; var embeddingMode = EmbeddingMode;
            LMStudioHttpGate? blocked = null;
            if (_gates.TryGetValue(resource, out var gates) && gates.TryDequeue(out blocked))
            {
                blocked.Started.TrySetResult();
                try { await blocked.Release.Task.WaitAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { blocked.Cancelled.TrySetResult(); throw; }
            }
            if (blocked?.Response is { } controlled) return Response(controlled);
            if (resource == "models") return catalogMode switch
            {
                "empty" => Response("{\"data\":[]}"),
                "failure" => Response("{\"error\":\"offline\"}", HttpStatusCode.ServiceUnavailable),
                "invalid" => Response("{invalid-json"),
                _ => Response("""
                    {"data":[
                      {"id":"embedding-model","display_name":"Vector","type":"embeddings","dimensions":3},
                      {"id":"both-model","display_name":"Shared","capabilities":{"chat":true,"embeddings":true},"dimensions":2},
                      {"id":"chat-model","display_name":"Alpha","type":"llm","max_context_length":8192,"max_output_tokens":1536},
                      {"id":"mystery-model","display_name":"Mystery","object":"model"}]}
                    """),
            };
            if (resource == "embeddings") return Response(Embeddings(body!.Value, embeddingMode));
            if (chatMode == "context-limit") return Response("{\"error\":{\"code\":\"context_length_exceeded\",\"type\":\"invalid_request_error\",\"message\":\"maximum context length exceeded\"}}", HttpStatusCode.BadRequest);
            return Response(Chat(chatMode), mediaType: "text/event-stream");
        }

        private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json")
            => new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

        private static string Embeddings(JsonElement request, string mode)
        {
            var count = request.GetProperty("input").GetArrayLength();
            var base64 = request.TryGetProperty("encoding_format", out var format) && format.GetString() == "base64";
            var data = Enumerable.Range(0, mode == "missing" ? Math.Max(0, count - 1) : count).Reverse().Select(index => new
            {
                @object = "embedding", index = mode == "duplicate" ? 0 : mode == "out-of-range" ? count : index,
                embedding = base64 ? (object)Encode([index + 0.25f, index + 0.5f, index + 0.75f]) : new[] { index + 0.25f, index + 0.5f, index + 0.75f },
            });
            return JsonSerializer.Serialize(new { @object = "list", model = "embedding-model", data, usage = new { prompt_tokens = 2, total_tokens = 2 } });
        }

        private static string Encode(float[] values)
        {
            var bytes = new byte[values.Length * sizeof(float)];
            for (var index = 0; index < values.Length; index++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * sizeof(float)), values[index]);
            return Convert.ToBase64String(bytes);
        }

        private static string Chat(string mode)
        {
            var result = new StringBuilder();
            if (mode is "tools" or "malformed-tool")
            {
                result.Append(Frame(new { content = "Before" }));
                result.Append(Frame(new { tool_calls = new[] { new { index = 0, id = "call.artifact", type = "function", function = new { name = "read_file", arguments = "{\"path\":" } } } }));
                result.Append(Frame(new { tool_calls = new[] { new { index = 0, function = new { arguments = mode == "malformed-tool" ? "bad-json" : "\"README.md\"}" } } } }));
                result.Append(Frame(new { content = "After" }));
                result.Append(Frame(new { }, "tool_calls"));
            }
            else
            {
                foreach (var text in new[] { "##", " ", "Heading", "\n\n", "-", " ", "item" }) result.Append(Frame(new { content = text }));
                if (mode != "eof") result.Append(Frame(new { }, "stop", new
                {
                    prompt_tokens = 12, completion_tokens = 7, total_tokens = 19,
                    prompt_tokens_details = new { cached_tokens = 4 }, completion_tokens_details = new { reasoning_tokens = 3 },
                }));
            }
            if (mode != "eof") result.Append("data: [DONE]\n\n");
            return result.ToString();
        }

        private static string Frame(object delta, string? finish = null, object? usage = null)
            => "data: " + JsonSerializer.Serialize(new
            {
                id = "chatcmpl-artifact", @object = "chat.completion.chunk", created = 0, model = "chat-model",
                choices = new[] { new { index = 0, delta, finish_reason = finish } }, usage,
            }) + "\n\n";

        protected override void Dispose(bool disposing)
        {
            if (disposing) IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed record LMStudioHttpRequest(Uri Uri, string? Authorization, JsonElement? Body);
    private sealed class LMStudioHttpGate(string? response)
    {
        internal string? Response { get; } = response;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
