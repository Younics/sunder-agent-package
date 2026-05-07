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
            data: {"type":"response.completed","response":{"usage":{"input_tokens":1,"output_tokens":1,"output_tokens_details":{}}}}

            """);

        var updates = await ReadUpdatesAsync(response);

        Assert.Single(updates);
        Assert.Equal("Hi", updates[0].Text);
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

            """);

        var updates = await ReadUpdatesAsync(response, toolAware: true);
        var functionCall = Assert.IsType<FunctionCallContent>(Assert.Single(updates).Contents.Single());

        Assert.Equal("call-1", functionCall.CallId);
        Assert.Equal("read", functionCall.Name);
        Assert.Equal("README.md", Assert.IsType<JsonElement>(functionCall.Arguments!["path"]).GetString());
    }

    [Fact]
    public async Task ParseAsync_FunctionCallWithDuplicateArguments_UsesLastValue()
    {
        using var response = CreateSseResponse("""
            event: response.output_item.added
            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"item-1","call_id":"call-1","name":"stitch_generate_screen_from_text","arguments":""}}

            event: response.function_call_arguments.done
            data: {"type":"response.function_call_arguments.done","output_index":0,"arguments":"{\"projectId\":\"project-1\",\"modelId\":\"GEMINI_3_PRO\",\"modelId\":\"GEMINI_3_FLASH\"}"}

            """);

        var updates = await ReadUpdatesAsync(response, toolAware: true);
        var functionCall = Assert.IsType<FunctionCallContent>(Assert.Single(updates).Contents.Single());

        Assert.Equal("stitch_generate_screen_from_text", functionCall.Name);
        Assert.Equal("project-1", Assert.IsType<JsonElement>(functionCall.Arguments!["projectId"]).GetString());
        Assert.Equal("GEMINI_3_FLASH", Assert.IsType<JsonElement>(functionCall.Arguments["modelId"]).GetString());
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

    private static HttpResponseMessage CreateSseResponse(string content)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(NormalizeLines(content), Encoding.UTF8, "text/event-stream"),
        };

    private static string NormalizeLines(string content)
        => string.Join("\n", content.Split('\n').Select(line => line.TrimStart())) + "\n";
}
