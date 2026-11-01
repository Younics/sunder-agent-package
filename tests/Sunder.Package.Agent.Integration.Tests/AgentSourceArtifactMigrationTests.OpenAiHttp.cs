using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private sealed class OpenAiArtifactHttp : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<OpenAiHttpGate>> _gates = new(StringComparer.Ordinal);
        private int _codexResponses;
        internal ConcurrentQueue<OpenAiHttpRequest> Requests { get; } = new();
        internal string ApiMode { get; set; } = "normal";
        internal string CodexMode { get; set; } = "normal";
        internal bool BadEmbeddingIndexes { get; set; }
        internal int AuthRequests;
        internal string? LastCodexResponseId { get; private set; }

        internal OpenAiHttpGate BlockNext(string resource)
        {
            var gate = new OpenAiHttpGate(); _gates.GetOrAdd(resource, static _ => new()).Enqueue(gate); return gate;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Requests.Enqueue(new(request.RequestUri!, body.RootElement.Clone(), request.Headers.Authorization?.ToString(),
                request.Headers.ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase)));
            var resource = request.RequestUri!.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal) ? "auth"
                : request.RequestUri.AbsolutePath.EndsWith("/codex/responses", StringComparison.Ordinal) ? "codex"
                : request.RequestUri.AbsolutePath.EndsWith("/embeddings", StringComparison.Ordinal) ? "embedding"
                : request.RequestUri.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal) ? "api"
                : throw new InvalidOperationException("Unexpected OpenAI artifact request: " + request.RequestUri.AbsolutePath);
            if (_gates.TryGetValue(resource, out var gates) && gates.TryDequeue(out var blocked))
            {
                blocked.Started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { blocked.Cancelled.TrySetResult(); throw; }
            }
            if (resource == "auth")
            {
                Interlocked.Increment(ref AuthRequests);
                var claims = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"chatgpt_account_id\":\"artifact-account\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                return Response(JsonSerializer.Serialize(new { access_token = "refreshed-access", refresh_token = "refreshed-refresh", expires_in = 3600, id_token = "e30." + claims + ".signature" }));
            }
            if (resource == "embedding") return Response(Embeddings(body.RootElement, BadEmbeddingIndexes));
            var mode = resource == "codex" ? CodexMode : ApiMode;
            if (mode == "context-limit") return Response("{\"error\":{\"code\":\"context_length_exceeded\",\"type\":\"invalid_request_error\",\"message\":\"maximum context length exceeded\"}}", HttpStatusCode.BadRequest);
            if (resource == "api") return Response(ApiStream(mode), mediaType: "text/event-stream");
            LastCodexResponseId = "resp.connected." + Interlocked.Increment(ref _codexResponses);
            return Response(CodexStream(LastCodexResponseId, mode), mediaType: "text/event-stream");
        }

        private static HttpResponseMessage Response(string value, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json")
            => new(status) { Content = new StringContent(value, Encoding.UTF8, mediaType) };

        private static string Embeddings(JsonElement request, bool duplicate)
        {
            var count = request.GetProperty("input").GetArrayLength();
            var base64 = request.TryGetProperty("encoding_format", out var encoding) && encoding.GetString() == "base64";
            var data = Enumerable.Range(0, count).Reverse().Select(index => new
            {
                @object = "embedding", index = duplicate ? 0 : index,
                embedding = base64 ? (object)Encode([index + 0.25f, index + 0.5f, index + 0.75f]) : new[] { index + 0.25f, index + 0.5f, index + 0.75f },
            });
            return JsonSerializer.Serialize(new { @object = "list", model = "text-embedding-3-small", data, usage = new { prompt_tokens = 2, total_tokens = 2 } });
        }
        private static string Encode(float[] values)
        {
            var bytes = new byte[values.Length * sizeof(float)];
            for (var index = 0; index < values.Length; index++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * sizeof(float)), values[index]);
            return Convert.ToBase64String(bytes);
        }

        private static string ApiStream(string mode)
        {
            var part = new { type = "output_text", text = "API text", annotations = Array.Empty<object>(), logprobs = Array.Empty<object>() };
            var message = new { id = "msg.api", type = "message", status = "completed", role = "assistant", content = new[] { part } };
            object Result(string status) => new
            {
                id = "resp.api", @object = "response", created_at = 0, model = "gpt-5.4", status,
                error = status == "failed" ? new { code = "server_error", message = "Controlled failure" } : null,
                incomplete_details = (object?)null, output = status == "completed" ? new object[] { message } : [],
                parallel_tool_calls = true, tools = Array.Empty<object>(), tool_choice = "auto", temperature = 1.0, top_p = 1.0,
                usage = new { input_tokens = 2, output_tokens = 3, total_tokens = 5, input_tokens_details = new { cached_tokens = 0 }, output_tokens_details = new { reasoning_tokens = 0 } },
            };
            if (mode == "failed") return Event(new { type = "response.failed", sequence_number = 0, response = Result("failed") });
            return Event(new { type = "response.created", sequence_number = 0, response = Result("in_progress") })
                + Event(new { type = "response.output_item.added", sequence_number = 1, output_index = 0, item = new { id = "msg.api", type = "message", status = "in_progress", role = "assistant", content = Array.Empty<object>() } })
                + Event(new { type = "response.content_part.added", sequence_number = 2, item_id = "msg.api", output_index = 0, content_index = 0, part = new { type = "output_text", text = "", annotations = Array.Empty<object>(), logprobs = Array.Empty<object>() } })
                + Event(new { type = "response.output_text.delta", sequence_number = 3, item_id = "msg.api", output_index = 0, content_index = 0, delta = "API text", logprobs = Array.Empty<object>() })
                + Event(new { type = "response.output_text.done", sequence_number = 4, item_id = "msg.api", output_index = 0, content_index = 0, text = "API text", logprobs = Array.Empty<object>() })
                + Event(new { type = "response.content_part.done", sequence_number = 5, item_id = "msg.api", output_index = 0, content_index = 0, part })
                + Event(new { type = "response.output_item.done", sequence_number = 6, output_index = 0, item = message })
                + Event(new { type = "response.completed", sequence_number = 7, response = Result("completed") });
        }

        private static string CodexStream(string id, string mode)
        {
            var result = Event(new { type = "response.created", response = new { id, created_at = 0, model = "gpt-5.4" } });
            if (mode == "tools")
                result += Event(new { type = "response.output_item.added", output_index = 0, item = new { type = "function_call", id = "item.connected", call_id = "call.connected", name = "read_file", arguments = "" } })
                    + Event(new { type = "response.function_call_arguments.delta", output_index = 0, delta = "{\"path\":" })
                    + Event(new { type = "response.function_call_arguments.done", output_index = 0, arguments = "{\"path\":\"README.md\"}" });
            else result += Event(new { type = "response.output_text.delta", delta = "Connected text" });
            return result + Event(new { type = "response.completed", response = new { id, status = "completed", usage = new { input_tokens = 2, output_tokens = 3, total_tokens = 5, output_tokens_details = new { reasoning_tokens = 0 } } } });
        }

        private static string Event(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";
    }

    private sealed record OpenAiHttpRequest(Uri Uri, JsonElement Body, string? Authorization, IReadOnlyDictionary<string, string> Headers);
    private sealed class OpenAiHttpGate
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
