using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.OpenAI.Transport;
using Xunit;

namespace Sunder.Package.Agent.Provider.OpenAI.Tests;

public sealed class CodexResponsesStreamParserTests
{
    [Fact]
    public async Task ParseAsync_TextDelta_YieldsStreamingText()
    {
        using var response = CreateSseResponse("""
            event: response.created
            data: {"type":"response.created","response":{"id":"resp-1","created_at":0,"model":"gpt-5.5"}}

            event: response.output_text.delta
            data: {"type":"response.output_text.delta","delta":"Hi"}

            event: response.completed
            data: {"type":"response.completed","response":{"id":"resp-1","status":"completed","usage":{"input_tokens":1,"output_tokens":1,"output_tokens_details":{}}}}

            """);

        var updates = await ReadUpdatesAsync(response);

        Assert.Equal(2, updates.Count);
        Assert.Equal("Hi", updates[0].Text);
        Assert.Equal("resp-1", updates[0].ResponseId);
        var usage = Assert.IsType<UsageContent>(Assert.Single(updates[1].Contents)).Details;
        Assert.Equal(1, usage.InputTokenCount);
        Assert.Equal(1, usage.OutputTokenCount);
        Assert.Equal(2, usage.TotalTokenCount);
    }

    [Fact]
    public async Task ParseAsync_TextDeltas_PreserveWhitespaceAndSplitMarkdownChunks()
    {
        using var response = CreateSseResponse("""
            data: {"type":"response.output_text.delta","delta":"##"}

            data: {"type":"response.output_text.delta","delta":" "}

            data: {"type":"response.output_text.delta","delta":"Heading"}

            data: {"type":"response.output_text.delta","delta":"\n"}

            data: {"type":"response.output_text.delta","delta":"\n"}

            data: {"type":"response.output_text.delta","delta":"-"}

            data: {"type":"response.output_text.delta","delta":" "}

            data: {"type":"response.output_text.delta","delta":"item"}

            data: {"type":"response.completed","response":{"id":"resp-1","status":"completed"}}

            """);

        var updates = await ReadUpdatesAsync(response);
        string[] expectedChunks = ["##", " ", "Heading", "\n", "\n", "-", " ", "item"];

        Assert.Equal(expectedChunks, updates.Select(update => update.Text));
        Assert.Equal("## Heading\n\n- item", string.Concat(updates.Select(update => update.Text)));
    }

    [Fact]
    public async Task ParseAsync_FunctionCall_YieldsFunctionCallContent()
    {
        using var response = CreateSseResponse("""
            event: response.output_item.added
            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"item-1","call_id":"call-1","name":"read","arguments":""}}

            event: response.function_call_arguments.delta
            data: {"type":"response.function_call_arguments.delta","output_index":0,"delta":"{\"path\":"}

            event: response.function_call_arguments.done
            data: {"type":"response.function_call_arguments.done","output_index":0,"arguments":"{\"path\":\"README.md\"}"}

            event: response.completed
            data: {"type":"response.completed","response":{"id":"resp-1","status":"completed"}}

            """);

        var updates = await ReadUpdatesAsync(response, toolAware: true);
        var functionCall = Assert.IsType<FunctionCallContent>(Assert.Single(updates).Contents.Single());

        Assert.Equal("call-1", functionCall.CallId);
        Assert.Equal("read", functionCall.Name);
        Assert.Equal("README.md", Assert.IsType<JsonElement>(functionCall.Arguments!["path"]).GetString());
    }

    [Fact]
    public async Task ParseAsync_ReasoningSummaryDelta_YieldsReasoningContent()
    {
        using var response = CreateSseResponse("""
            event: response.reasoning_summary_text.delta
            data: {"type":"response.reasoning_summary_text.delta","delta":"Checking the repo shape."}

            event: response.output_text.delta
            data: {"type":"response.output_text.delta","delta":"Done"}

            event: response.completed
            data: {"type":"response.completed","response":{"id":"resp-1","status":"completed"}}

            """);

        var updates = await ReadUpdatesAsync(response);

        var reasoning = Assert.IsType<TextReasoningContent>(updates[0].Contents.Single());
        Assert.Equal("Checking the repo shape.", reasoning.Text);
        Assert.Equal("Done", updates[1].Text);
    }

    [Fact]
    public async Task ParseAsync_FunctionCallWithDuplicateArguments_RejectsAmbiguity()
    {
        using var response = CreateSseResponse("""
            event: response.output_item.added
            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"item-1","call_id":"call-1","name":"stitch_generate_screen_from_text","arguments":""}}

            event: response.function_call_arguments.done
            data: {"type":"response.function_call_arguments.done","output_index":0,"arguments":"{\"projectId\":\"project-1\",\"modelId\":\"GEMINI_3_PRO\",\"modelId\":\"GEMINI_3_FLASH\"}"}

            event: response.completed
            data: {"type":"response.completed","response":{"id":"resp-1","status":"completed"}}

            """);

        var error = await Assert.ThrowsAsync<AgentChatProviderException>(() => ReadUpdatesAsync(response, toolAware: true));
        Assert.Equal("openai-malformed-tool-call", error.ErrorCode);
    }

    [Fact]
    public async Task ParseAsync_OversizedSseLine_RejectsBeforeJsonParsing()
    {
        using var response = CreateSseResponse("data: " + new string('x', AgentPayloadLimits.MaxProviderSseLineBytes + 1));

        var error = await Assert.ThrowsAsync<AgentChatProviderException>(() => ReadUpdatesAsync(response));

        Assert.Equal("openai-stream-line-too-large", error.ErrorCode);
    }

    [Fact]
    public async Task ParseAsync_MultipleFunctionCalls_ThrowsProviderException()
    {
        using var response = CreateSseResponse("""
            event: response.output_item.added
            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"item-1","call_id":"call-1","name":"read","arguments":""}}

            event: response.output_item.added
            data: {"type":"response.output_item.added","output_index":1,"item":{"type":"function_call","id":"item-2","call_id":"call-2","name":"write","arguments":""}}

            """);

        await Assert.ThrowsAsync<AgentChatProviderException>(async () =>
        {
            await foreach (var _ in CodexResponsesStreamParser.ParseAsync(
                               response,
                               new AgentChatClientContext("openai", "openai/gpt-5.5"),
                               new ChatOptions { ModelId = "openai/gpt-5.5" },
                               "resp-1",
                               "msg-1",
                               toolAware: true,
                               CancellationToken.None))
            {
            }
        });
    }

    [Fact]
    public async Task ParseAsync_FailedResponse_PreservesPartialUpdatesAndThrows()
    {
        using var response = CreateSseResponse("""
            event: response.output_text.delta
            data: {"type":"response.output_text.delta","delta":"Partial"}

            event: response.failed
            data: {"type":"response.failed","response":{"id":"resp-1","status":"failed","error":{"message":"backend failed"}}}

            """);
        var updates = new List<ChatResponseUpdate>();

        var exception = await Assert.ThrowsAsync<AgentChatProviderException>(async () =>
        {
            await foreach (var update in ParseAsync(response))
            {
                updates.Add(update);
            }
        });

        Assert.Equal("Partial", Assert.Single(updates).Text);
        Assert.Contains("backend failed", exception.Content, StringComparison.Ordinal);
        Assert.Contains("7 text characters", exception.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_IncompleteResponse_ThrowsWithReason()
    {
        using var response = CreateSseResponse("""
            event: response.incomplete
            data: {"type":"response.incomplete","response":{"id":"resp-1","status":"incomplete","incomplete_details":{"reason":"max_output_tokens"}}}

            """);

        var exception = await Assert.ThrowsAsync<AgentChatProviderException>(async () =>
        {
            await foreach (var _ in ParseAsync(response))
            {
            }
        });

        Assert.Contains("max_output_tokens", exception.Content, StringComparison.Ordinal);
        Assert.Equal(AgentChatProviderFailureKind.Unknown, exception.FailureKind);
    }

    [Theory]
    [InlineData(
        "context_length_exceeded",
        "Your input exceeds the context window of this model.",
        AgentChatProviderFailureKind.ContextWindowExceeded)]
    [InlineData("insufficient_quota", "You exceeded your current quota.", AgentChatProviderFailureKind.Unknown)]
    [InlineData("invalid_request_error", "Generic bad request.", AgentChatProviderFailureKind.Unknown)]
    [InlineData("invalid_request_error", "max_output_tokens is too large.", AgentChatProviderFailureKind.Unknown)]
    public async Task ParseAsync_FailedResponseClassifiesOnlyContextWindowOverflow(
        string code,
        string message,
        AgentChatProviderFailureKind expectedKind)
    {
        var payload = JsonSerializer.Serialize(new
        {
            type = "response.failed",
            response = new
            {
                id = "resp-1",
                status = "failed",
                error = new { code, message },
            },
        });
        using var response = CreateSseResponse($"data: {payload}\n\n");

        var exception = await Assert.ThrowsAsync<AgentChatProviderException>(() => ReadUpdatesAsync(response));

        Assert.Equal(expectedKind, exception.FailureKind);
    }

    [Fact]
    public async Task ParseAsync_FailedResponseIgnoresContextPhrasesOutsideErrorFields()
    {
        using var response = CreateSseResponse("""
            event: response.failed
            data: {"type":"response.failed","response":{"id":"resp-1","status":"failed","error":{"code":"backend_error","message":"backend failed"},"output":[{"type":"message","content":"context_length_exceeded was mentioned by the user"}]}}

            """);

        var exception = await Assert.ThrowsAsync<AgentChatProviderException>(() => ReadUpdatesAsync(response));

        Assert.Equal(AgentChatProviderFailureKind.Unknown, exception.FailureKind);
    }

    [Fact]
    public async Task ParseAsync_PrematureEof_ThrowsInsteadOfCompleting()
    {
        using var response = CreateSseResponse("""
            event: response.output_text.delta
            data: {"type":"response.output_text.delta","delta":"Partial"}

            """);

        var exception = await Assert.ThrowsAsync<AgentChatProviderException>(async () =>
        {
            await foreach (var _ in ParseAsync(response))
            {
            }
        });

        Assert.Contains("before a completed response event", exception.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("incomplete")]
    public async Task ParseAsync_CompletedEventWithoutCompletedStatus_Throws(string? status)
    {
        var statusProperty = status is null ? string.Empty : $",\"status\":\"{status}\"";
        using var response = CreateSseResponse(
            "event: response.completed\n"
            + $"data: {{\"type\":\"response.completed\",\"response\":{{\"id\":\"resp-1\"{statusProperty}}}}}\n");

        var exception = await Assert.ThrowsAsync<AgentChatProviderException>(async () =>
        {
            await foreach (var _ in ParseAsync(response))
            {
            }
        });

        Assert.Contains("valid completed status", exception.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_CompletedResponseRejectsUnfinishedToolCall()
    {
        using var response = CreateSseResponse("""
            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","call_id":"call-1","name":"read"}}

            data: {"type":"response.completed","response":{"id":"resp-1","status":"completed"}}

            """);

        var exception = await Assert.ThrowsAsync<AgentChatProviderException>(async () =>
        {
            await foreach (var _ in CodexResponsesStreamParser.ParseAsync(
                               response,
                               new AgentChatClientContext("openai", "openai/gpt-5.5"),
                               new ChatOptions(),
                               "resp-1",
                               "msg-1",
                               toolAware: true,
                               CancellationToken.None))
            {
            }
        });

        Assert.Equal("openai-malformed-tool-call", exception.ErrorCode);
    }

    [Fact]
    public async Task ParseAsync_UnknownEvent_IsReportedAndDoesNotHideLaterContent()
    {
        using var response = CreateSseResponse("""
            data: {"type":"response.future_event","value":1}

            data: {"type":"response.output_text.delta","delta":"still handled"}

            data: {"type":"response.completed","response":{"id":"resp-1","status":"completed"}}

            """);
        var logger = new RecordingEventLogger();
        var updates = new List<ChatResponseUpdate>();

        await foreach (var update in CodexResponsesStreamParser.ParseAsync(
                           response,
                           new AgentChatClientContext("openai", "openai/gpt-5.5", logger),
                           new ChatOptions(),
                           "resp-1",
                           "msg-1",
                           toolAware: false,
                           CancellationToken.None))
        {
            updates.Add(update);
        }

        Assert.Equal("still handled", Assert.Single(updates).Text);
        Assert.Contains("provider.response.unsupported_content", logger.EventNames);
    }

    [Fact]
    public async Task ParseAsync_RejectsMissingToolNameAndMalformedArguments()
    {
        using var missingNameResponse = CreateSseResponse("""
            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","call_id":"call-1"}}

            data: {"type":"response.function_call_arguments.done","output_index":0,"arguments":"{}"}

            """);
        var missingName = await Assert.ThrowsAsync<AgentChatProviderException>(async () =>
        {
            await foreach (var _ in ParseToolResponseAsync(missingNameResponse))
            {
            }
        });

        using var malformedArgumentsResponse = CreateSseResponse("""
            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","call_id":"call-1","name":"read"}}

            data: {"type":"response.function_call_arguments.done","output_index":0,"arguments":"{not-json"}

            """);
        var malformedArguments = await Assert.ThrowsAsync<AgentChatProviderException>(async () =>
        {
            await foreach (var _ in ParseToolResponseAsync(malformedArgumentsResponse))
            {
            }
        });

        Assert.Equal("openai-malformed-tool-call", missingName.ErrorCode);
        Assert.Equal("openai-malformed-tool-call", malformedArguments.ErrorCode);
    }

    private static async Task<IReadOnlyList<ChatResponseUpdate>> ReadUpdatesAsync(HttpResponseMessage response, bool toolAware = false)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in CodexResponsesStreamParser.ParseAsync(
                           response,
                           new AgentChatClientContext("openai", "openai/gpt-5.5"),
                           new ChatOptions { ModelId = "openai/gpt-5.5" },
                           "resp-1",
                           "msg-1",
                           toolAware,
                           CancellationToken.None))
        {
            updates.Add(update);
        }

        return updates;
    }

    private static IAsyncEnumerable<ChatResponseUpdate> ParseAsync(HttpResponseMessage response)
        => CodexResponsesStreamParser.ParseAsync(
            response,
            new AgentChatClientContext("openai", "openai/gpt-5.5"),
            new ChatOptions { ModelId = "openai/gpt-5.5" },
            "resp-1",
            "msg-1",
            toolAware: false,
            CancellationToken.None);

    private static IAsyncEnumerable<ChatResponseUpdate> ParseToolResponseAsync(HttpResponseMessage response)
        => CodexResponsesStreamParser.ParseAsync(
            response,
            new AgentChatClientContext("openai", "openai/gpt-5.5"),
            new ChatOptions(),
            "resp-1",
            "msg-1",
            toolAware: true,
            CancellationToken.None);

    private static HttpResponseMessage CreateSseResponse(string content)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(NormalizeLines(content), Encoding.UTF8, "text/event-stream"),
        };

    private static string NormalizeLines(string content)
        => string.Join("\n", content.Split('\n').Select(line => line.TrimStart())) + "\n";

    private sealed class RecordingEventLogger : Sunder.Sdk.Logging.IPackageEventLogger
    {
        public List<string> EventNames { get; } = [];

        public ValueTask WriteAsync(
            Sunder.Sdk.Logging.PackageLogLevel level,
            string eventName,
            string message,
            IReadOnlyDictionary<string, object?>? attributes = null,
            Exception? exception = null,
            CancellationToken cancellationToken = default)
        {
            EventNames.Add(eventName);
            return ValueTask.CompletedTask;
        }
    }
}
