using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.OpenAI.Transport;
using Xunit;

namespace Sunder.Package.Agent.Provider.OpenAI.Tests;

public sealed class CodexResponsesRequestBuilderTests
{
    [Fact]
    public void Build_Gpt55TextRequest_AppliesOpenCodeCompatibleDefaults()
    {
        var request = CodexResponsesRequestBuilder.Build(
            new AgentChatClientContext("openai", "openai/gpt-5.5"),
            [new ChatMessage(ChatRole.User, "Say hi.")],
            new ChatOptions
            {
                Instructions = "Be brief.",
                ConversationId = "session-123",
            },
            toolAware: false);

        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;

        Assert.Equal("gpt-5.5", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal("session-123", root.GetProperty("prompt_cache_key").GetString());
        Assert.False(root.TryGetProperty("service_tier", out _));
        Assert.Equal("medium", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("auto", root.GetProperty("reasoning").GetProperty("summary").GetString());
        Assert.Equal("low", root.GetProperty("text").GetProperty("verbosity").GetString());
        Assert.Equal("Be brief.", root.GetProperty("instructions").GetString());
        Assert.Equal("reasoning.encrypted_content", root.GetProperty("include")[0].GetString());
        Assert.False(root.TryGetProperty("tool_choice", out _));

        var input = root.GetProperty("input").EnumerateArray().ToArray();
        Assert.Single(input);
        Assert.Equal("user", input[0].GetProperty("role").GetString());
        Assert.Equal("input_text", input[0].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("Say hi.", input[0].GetProperty("content")[0].GetProperty("text").GetString());

        Assert.False(request.UsesDeveloperInstructionInput);
        Assert.True(request.HasTopLevelInstructions);
        Assert.Null(request.ServiceTier);
        Assert.True(request.HasPromptCacheKey);
        Assert.True(request.HasIncludeOptions);
        Assert.True(request.HasReasoningOptions);
        Assert.True(request.HasTextOptions);
        Assert.Null(request.ToolChoice);
    }

    [Fact]
    public void Build_Gpt55FastRequest_UsesBaseModelAndPriorityServiceTier()
    {
        var request = CodexResponsesRequestBuilder.Build(
            new AgentChatClientContext("openai", "openai/gpt-5.5-fast"),
            [new ChatMessage(ChatRole.User, "Say hi.")],
            new ChatOptions
            {
                ConversationId = "session-123",
            },
            toolAware: false);

        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;

        Assert.Equal("gpt-5.5", root.GetProperty("model").GetString());
        Assert.Equal("priority", root.GetProperty("service_tier").GetString());
        Assert.Equal("priority", request.ServiceTier);
    }

    [Theory]
    [InlineData(ReasoningEffort.None, "none")]
    [InlineData(ReasoningEffort.High, "high")]
    [InlineData(ReasoningEffort.ExtraHigh, "xhigh")]
    public void Build_Gpt55Request_AppliesExplicitReasoningEffort(ReasoningEffort effort, string expectedEffort)
    {
        var request = CodexResponsesRequestBuilder.Build(
            new AgentChatClientContext("openai", "openai/gpt-5.5"),
            [new ChatMessage(ChatRole.User, "Say hi.")],
            new ChatOptions
            {
                Reasoning = new ReasoningOptions { Effort = effort },
            },
            toolAware: false);

        using var document = JsonDocument.Parse(request.Body);
        var reasoning = document.RootElement.GetProperty("reasoning");

        Assert.Equal(expectedEffort, reasoning.GetProperty("effort").GetString());
        Assert.Equal("auto", reasoning.GetProperty("summary").GetString());
    }

    [Fact]
    public void Build_Gpt55SystemMessage_UsesDeveloperInput()
    {
        var request = CodexResponsesRequestBuilder.Build(
            new AgentChatClientContext("openai", "openai/gpt-5.5"),
            [
                new ChatMessage(ChatRole.System, "System guidance."),
                new ChatMessage(ChatRole.User, "Say hi.")
            ],
            new ChatOptions { ConversationId = "session-123" },
            toolAware: false);

        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        var input = root.GetProperty("input").EnumerateArray().ToArray();

        Assert.False(root.TryGetProperty("instructions", out _));
        Assert.Equal("developer", input[0].GetProperty("role").GetString());
        Assert.Equal("System guidance.", input[0].GetProperty("content").GetString());
        Assert.Equal("user", input[1].GetProperty("role").GetString());
        Assert.True(request.UsesDeveloperInstructionInput);
        Assert.False(request.HasTopLevelInstructions);
    }

    [Fact]
    public void Build_ToolAwareRequest_AddsFunctionToolsWithoutForcingStrictOrParallelCalls()
    {
        using var schemaDocument = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "path": { "type": "string" }
              }
            }
            """);
        var tool = AIFunctionFactory.CreateDeclaration(
            "read",
            "Read a file.",
            schemaDocument.RootElement.Clone(),
            returnJsonSchema: null);

        var request = CodexResponsesRequestBuilder.Build(
            new AgentChatClientContext("openai", "openai/gpt-5.5"),
            [new ChatMessage(ChatRole.User, "Read the file.")],
            new ChatOptions
            {
                ConversationId = "session-123",
                ToolMode = new AutoChatToolMode(),
                Tools = [tool],
            },
            toolAware: true);

        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        var toolJson = root.GetProperty("tools")[0];

        Assert.False(root.TryGetProperty("parallel_tool_calls", out _));
        Assert.Equal("auto", root.GetProperty("tool_choice").GetString());
        Assert.Equal("function", toolJson.GetProperty("type").GetString());
        Assert.Equal("read", toolJson.GetProperty("name").GetString());
        Assert.False(toolJson.GetProperty("strict").GetBoolean());
        Assert.False(toolJson.GetProperty("parameters").GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["path"], toolJson.GetProperty("parameters").GetProperty("required").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal(1, request.ToolCount);
        Assert.Equal("auto", request.ToolChoice);
    }

    [Fact]
    public void Build_ToolContinuation_UsesResponsesFunctionCallItems()
    {
        var request = CodexResponsesRequestBuilder.Build(
            new AgentChatClientContext("openai", "openai/gpt-5.5"),
            [
                new ChatMessage(ChatRole.Assistant,
                [
                    new FunctionCallContent("call-1", "read", new Dictionary<string, object?>
                    {
                        ["path"] = "README.md",
                    })
                ]),
                new ChatMessage(ChatRole.Tool,
                [
                    new FunctionResultContent("call-1", "file contents")
                ])
            ],
            new ChatOptions { ConversationId = "session-123" },
            toolAware: false);

        using var document = JsonDocument.Parse(request.Body);
        var input = document.RootElement.GetProperty("input").EnumerateArray().ToArray();

        Assert.Equal("function_call", input[0].GetProperty("type").GetString());
        Assert.Equal("call-1", input[0].GetProperty("call_id").GetString());
        Assert.Equal("read", input[0].GetProperty("name").GetString());
        Assert.Equal("README.md", JsonDocument.Parse(input[0].GetProperty("arguments").GetString()!).RootElement.GetProperty("path").GetString());
        Assert.Equal("function_call_output", input[1].GetProperty("type").GetString());
        Assert.Equal("call-1", input[1].GetProperty("call_id").GetString());
        Assert.Equal("file contents", input[1].GetProperty("output").GetString());
    }

    [Fact]
    public void Build_OrphanToolResult_SkipsResponsesFunctionCallOutput()
    {
        var request = CodexResponsesRequestBuilder.Build(
            new AgentChatClientContext("openai", "openai/gpt-5.5"),
            [
                new ChatMessage(ChatRole.Tool,
                [
                    new FunctionResultContent("missing-call", "orphaned result")
                ]),
                new ChatMessage(ChatRole.User, "Continue.")
            ],
            new ChatOptions { ConversationId = "session-123" },
            toolAware: false);

        using var document = JsonDocument.Parse(request.Body);
        var input = document.RootElement.GetProperty("input").EnumerateArray().ToArray();

        Assert.DoesNotContain(input, item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
        Assert.Single(input);
        Assert.Equal("user", input[0].GetProperty("role").GetString());
    }

    [Fact]
    public void Build_UserDataContent_MapsImageAndPdfParts()
    {
        var image = new DataContent(new byte[] { 1, 2, 3 }, "image/png") { Name = "image.png" };
        var pdf = new DataContent(new byte[] { 4, 5, 6 }, "application/pdf") { Name = "doc.pdf" };

        var request = CodexResponsesRequestBuilder.Build(
            new AgentChatClientContext("openai", "openai/gpt-5.5"),
            [new ChatMessage(ChatRole.User, [new TextContent("Use these."), image, pdf])],
            new ChatOptions { ConversationId = "session-123" },
            toolAware: false);

        using var document = JsonDocument.Parse(request.Body);
        var content = document.RootElement.GetProperty("input")[0].GetProperty("content").EnumerateArray().ToArray();

        Assert.Equal("input_text", content[0].GetProperty("type").GetString());
        Assert.Equal("input_image", content[1].GetProperty("type").GetString());
        Assert.StartsWith("data:image/png;base64,", content[1].GetProperty("image_url").GetString());
        Assert.Equal("input_file", content[2].GetProperty("type").GetString());
        Assert.Equal("doc.pdf", content[2].GetProperty("filename").GetString());
        Assert.StartsWith("data:application/pdf;base64,", content[2].GetProperty("file_data").GetString());
    }
}
