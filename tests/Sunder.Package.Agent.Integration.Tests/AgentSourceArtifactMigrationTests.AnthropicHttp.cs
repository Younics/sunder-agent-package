using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private sealed class AnthropicArtifactHttp : HttpMessageHandler
    {
        internal ConcurrentQueue<(JsonElement Body, IReadOnlyDictionary<string, string> Headers)> Requests { get; } = new();
        internal string Mode { get; set; } = "normal";
        internal TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool CancellationObserved;
        internal int Responses;
        internal int DisposedResponses;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Requests.Enqueue((body.RootElement.Clone(), request.Headers.ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase)));
            if (Mode == "hang")
            {
                Blocked.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { CancellationObserved = token.IsCancellationRequested; }
            }
            var streaming = body.RootElement.TryGetProperty("stream", out var stream) && stream.GetBoolean();
            var error = Mode == "context-limit";
            var content = error ? "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"prompt is too long: 200001 tokens > 200000 maximum\"}}"
                : streaming ? StreamResponse(Mode == "eof") : ToolResponse(Mode == "malformed-tool");
            Interlocked.Increment(ref Responses);
            return new(error ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            {
                Content = new TrackedContent(content, !error && streaming ? "text/event-stream" : "application/json", () => Interlocked.Increment(ref DisposedResponses)),
            };
        }

        private static string StreamResponse(bool incomplete)
            => "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg.artifact\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"claude-opus-4-8\",\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":2,\"cache_creation_input_tokens\":1,\"cache_read_input_tokens\":3,\"output_tokens\":0}}}\n\n"
               + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"Checking constraints.\"}}\n\n"
               + string.Concat(new[] { "##", " ", "Heading", "\n\n", "-", " ", "item" }.Select(text =>
                   "event: content_block_delta\ndata: " + JsonSerializer.Serialize(new { type = "content_block_delta", index = 1, delta = new { type = "text_delta", text } }) + "\n\n"))
               + (incomplete ? "" : "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":2}}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");

        private static string ToolResponse(bool malformed)
            => $$"""
                {"id":"msg.artifact","type":"message","role":"assistant","model":"claude-opus-4-8",
                 "stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":2,"output_tokens":3},
                 "content":[{"type":"text","text":"Before"},
                   {"type":"thinking","thinking":"Check first.","signature":"artifact-signature"},
                   {"type":"tool_use","id":"call.artifact","name":"read_file","input":{{(malformed ? "\"not-an-object\"" : "{\"path\":\"README.md\"}")}}},
                   {"type":"text","text":"After"}]}
                """;

        private sealed class TrackedContent(string value, string mediaType, Action disposed) : StringContent(value, Encoding.UTF8, mediaType)
        {
            private int _disposed;
            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0) disposed();
            }
        }
    }
}
