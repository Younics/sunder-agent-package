using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Provider.TestSupport;
using Xunit;

namespace Sunder.Package.Agent.Provider.Anthropic.Tests;

public sealed class AnthropicProviderTests
{
    private const string ModelId = "anthropic/claude-opus-4-8";

    [Fact]
    public async Task StandardRequest_WritesRolesMixedContentToolsAndAttachmentsToWire()
    {
        var handler = new CapturingHandler(_ => JsonResponse(TextResponse("Accepted")));
        var client = CreateChatClient(handler);
        var image = new DataContent(new byte[] { 1, 2, 3 }, "image/png") { Name = "image.png" };
        var pdf = new DataContent(new byte[] { 4, 5, 6 }, "application/pdf") { Name = "doc.pdf" };
        var tool = CreateTool();
        ChatMessage[] messages =
        [
            new(ChatRole.User,
            [
                new TextContent("Inspect these files."),
                image,
                new TextContent("Compare them."),
                pdf,
            ]),
            new(ChatRole.Assistant,
            [
                new TextContent("I will inspect them."),
                new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?>
                {
                    ["path"] = "README.md",
                }),
            ]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "contents")]),
        ];
        var options = new ChatOptions
        {
            Instructions = "Be precise.",
            MaxOutputTokens = 4096,
            ToolMode = new AutoChatToolMode(),
            Tools = [tool],
        };

        var updates = await ReadUpdatesAsync(client, messages, options);

        Assert.Equal("Accepted", Assert.Single(updates).Text);
        using var document = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var root = document.RootElement;
        Assert.Equal("claude-opus-4-8", root.GetProperty("model").GetString());
        Assert.Equal(4096, root.GetProperty("max_tokens").GetInt64());
        Assert.Equal("Be precise.", root.GetProperty("system").GetString());

        var wireMessages = root.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(
            "user,assistant,user",
            string.Join(",", wireMessages.Select(message => message.GetProperty("role").GetString())));
        var mixedContent = wireMessages[0].GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal(
            "text,image,text,document",
            string.Join(",", mixedContent.Select(content => content.GetProperty("type").GetString())));
        Assert.Equal("AQID", mixedContent[1].GetProperty("source").GetProperty("data").GetString());
        Assert.Equal("application/pdf", mixedContent[3].GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal("doc.pdf", mixedContent[3].GetProperty("title").GetString());

        var assistantContent = wireMessages[1].GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal(["text", "tool_use"], assistantContent.Select(content => content.GetProperty("type").GetString()));
        var toolUse = assistantContent[1];
        Assert.Equal("tool_use", toolUse.GetProperty("type").GetString());
        Assert.Equal("README.md", toolUse.GetProperty("input").GetProperty("path").GetString());
        var toolResult = wireMessages[2].GetProperty("content")[0];
        Assert.Equal("tool_result", toolResult.GetProperty("type").GetString());
        Assert.Equal("contents", toolResult.GetProperty("content").GetString());

        var wireTool = root.GetProperty("tools")[0];
        Assert.Equal("read_file", wireTool.GetProperty("name").GetString());
        Assert.Equal("string", wireTool.GetProperty("input_schema").GetProperty("properties").GetProperty("path").GetProperty("type").GetString());
        Assert.Equal("path", wireTool.GetProperty("input_schema").GetProperty("required")[0].GetString());
        Assert.True(root.GetProperty("tool_choice").GetProperty("disable_parallel_tool_use").GetBoolean());
    }

    [Fact]
    public async Task ToolContinuation_RoundTripsSignedThinkingBlockUnchanged()
    {
        var handler = new CapturingHandler(_ => JsonResponse(TextResponse("Continued")));
        var client = CreateChatClient(handler);
        ChatMessage[] messages =
        [
            new(ChatRole.User, "Inspect the file."),
            new(ChatRole.Assistant,
            [
                new TextReasoningContent("I should inspect it.") { ProtectedData = "signed-thinking" },
                new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?>
                {
                    ["path"] = "README.md",
                }),
            ]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "contents")]),
        ];

        await ReadUpdatesAsync(
            client,
            messages,
            new ChatOptions
            {
                Reasoning = new ReasoningOptions
                {
                    Effort = ReasoningEffort.High,
                    Output = ReasoningOutput.Summary,
                },
                ToolMode = ChatToolMode.Auto,
                Tools = [CreateTool()],
            });

        using var document = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var thinking = document.RootElement.GetProperty("messages")[1].GetProperty("content")[0];
        Assert.Equal("thinking", thinking.GetProperty("type").GetString());
        Assert.Equal("I should inspect it.", thinking.GetProperty("thinking").GetString());
        Assert.Equal("signed-thinking", thinking.GetProperty("signature").GetString());
    }

    [Theory]
    [InlineData(ReasoningEffort.Low, "low")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.High, "high")]
    [InlineData(ReasoningEffort.ExtraHigh, "xhigh")]
    public async Task AutoToolChoice_PreservesSelectedReasoningOnWire(
        ReasoningEffort effort,
        string expectedEffort)
    {
        var handler = new CapturingHandler(_ => JsonResponse(TextResponse("Done")));
        var client = CreateChatClient(handler);
        var options = new ChatOptions
        {
            MaxOutputTokens = 4096,
            Reasoning = new ReasoningOptions
            {
                Effort = effort,
                Output = ReasoningOutput.Summary,
            },
            ToolMode = new AutoChatToolMode(),
            Tools = [CreateTool()],
        };

        await ReadUpdatesAsync(client, [new ChatMessage(ChatRole.User, "Think.")], options);

        using var document = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var root = document.RootElement;
        Assert.Equal(4096, root.GetProperty("max_tokens").GetInt64());
        Assert.Equal("adaptive", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(root.GetProperty("thinking").TryGetProperty("budget_tokens", out _));
        Assert.Equal("summarized", root.GetProperty("thinking").GetProperty("display").GetString());
        Assert.Equal(expectedEffort, root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("auto", root.GetProperty("tool_choice").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForcedToolChoice_WithVisibleReasoning_IsRejectedBeforeTransport(bool requireSpecificTool)
    {
        var handler = new CapturingHandler(_ => throw new InvalidOperationException("Request should not be sent."));
        var client = CreateChatClient(handler);
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions
            {
                Effort = ReasoningEffort.High,
                Output = ReasoningOutput.Summary,
            },
            ToolMode = requireSpecificTool
                ? ChatToolMode.RequireSpecific("read_file")
                : ChatToolMode.RequireAny,
            Tools = [CreateTool()],
        };

        var exception = await Assert.ThrowsAsync<AgentChatProviderException>(() => ReadUpdatesAsync(
            client,
            [new ChatMessage(ChatRole.User, "Think and use a tool.")],
            options));

        Assert.Equal("anthropic-unsupported-reasoning-tool-choice", exception.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void LegacyToolReasoning_UsesManualThinkingBudgetWithAutomaticToolChoice()
    {
        var request = AnthropicOptionsTranslator.Translate(
            AnthropicMessageTranslator.TranslateMessages([new ChatMessage(ChatRole.User, "Think.")]),
            new ChatOptions
            {
                MaxOutputTokens = 4096,
                Reasoning = new ReasoningOptions
                {
                    Effort = ReasoningEffort.Medium,
                    Output = ReasoningOutput.Summary,
                },
                ToolMode = ChatToolMode.Auto,
                Tools = [CreateTool()],
            },
            "anthropic/claude-opus-4-5");

        Assert.Equal(6144, request.Parameters.MaxTokens);
        Assert.NotNull(request.Parameters.Thinking);
        Assert.Equal(2048, request.TokenBudget.ThinkingTokens);
        Assert.NotNull(request.Parameters.OutputConfig);
    }

    [Fact]
    public void ExtraHighBudget_IsCappedByCatalogModelLimitAndKeepsVisibleAllowance()
    {
        var options = new ChatOptions
        {
            MaxOutputTokens = 128000,
            Reasoning = new ReasoningOptions
            {
                Effort = ReasoningEffort.ExtraHigh,
                Output = ReasoningOutput.Full,
            },
        };

        var budget = AnthropicOptionsTranslator.TranslateTokenBudget(options, ModelId);

        Assert.Equal(128000, budget.ModelLimit);
        Assert.Equal(128000, budget.MaxTokens);
        Assert.Equal(8192, budget.ThinkingTokens);
        Assert.True(budget.MaxTokens - budget.ThinkingTokens >= 1024);
    }

    [Fact]
    public void ReasoningEffortWithoutRequestedOutput_DoesNotAllocateThinkingTokens()
    {
        var request = AnthropicOptionsTranslator.Translate(
            AnthropicMessageTranslator.TranslateMessages([new ChatMessage(ChatRole.User, "Think.")]),
            new ChatOptions
            {
                MaxOutputTokens = 4096,
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            },
            "claude-opus-4-8");

        Assert.Equal(4096, request.Parameters.MaxTokens);
        Assert.Null(request.Parameters.Thinking);
        Assert.Equal(
            global::Anthropic.Models.Messages.Effort.Xhigh,
            request.Parameters.OutputConfig!.Effort!.Value());
        Assert.Equal(128000, request.TokenBudget.ModelLimit);
    }

    [Fact]
    public async Task FastMode_UsesBetaHeaderAndSpeedOnWire()
    {
        var handler = new CapturingHandler(_ => JsonResponse(TextResponse("Fast")));
        var client = CreateChatClient(handler);
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [AgentChatModelOptionKeys.SpeedOptionId] = "fast",
            },
            ToolMode = new AutoChatToolMode(),
            Tools = [CreateTool()],
        };

        var updates = await ReadUpdatesAsync(client, [new ChatMessage(ChatRole.User, "Go fast.")], options);

        Assert.Equal("Fast", Assert.Single(updates).Text);
        var request = Assert.Single(handler.Requests);
        Assert.Contains(request.Headers,
            header => string.Equals(header.Key, "anthropic-beta", StringComparison.OrdinalIgnoreCase)
                && header.Value.Contains("fast-mode-2026-02-01", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal("fast", document.RootElement.GetProperty("speed").GetString());
    }

    [Fact]
    public async Task FastMode_StreamsThroughBetaTransport()
    {
        var handler = new CapturingHandler(_ => SseResponse(
            """
             event: content_block_delta
             data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Fast stream"}}

             event: message_delta
             data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}

             event: message_stop
            data: {"type":"message_stop"}

            """));
        var client = CreateChatClient(handler);

        var updates = await ReadUpdatesAsync(
            client,
            [new ChatMessage(ChatRole.User, "Stream fast.")],
            new ChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [AgentChatModelOptionKeys.SpeedOptionId] = "fast",
                },
            });

        Assert.Equal("Fast stream", Assert.Single(updates).Text);
        Assert.Contains(
            Assert.Single(handler.Requests).Headers,
            header => string.Equals(header.Key, "anthropic-beta", StringComparison.OrdinalIgnoreCase)
                && header.Value.Contains("fast-mode-2026-02-01", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StandardStreaming_TranslatesReasoningSummaryAndTextDeltas()
    {
        var sink = new RecordingSink();
        var handler = new CapturingHandler(_ => SseResponse(
            """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg-1","type":"message","role":"assistant","content":[],"model":"claude-opus-4-8","stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":0}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Checking constraints."}}

             event: content_block_delta
             data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Done"}}

             event: message_delta
             data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}

             event: message_stop
            data: {"type":"message_stop"}

            """));
        var client = CreateChatClient(handler, sink);

        var updates = await ReadUpdatesAsync(
            client,
            [new ChatMessage(ChatRole.User, "Think.")],
            new ChatOptions
            {
                Reasoning = new ReasoningOptions
                {
                    Effort = ReasoningEffort.High,
                    Output = ReasoningOutput.Summary,
                },
            });

        Assert.Equal(2, updates.Count);
        Assert.Equal("Checking constraints.", Assert.IsType<TextReasoningContent>(updates[0].Contents.Single()).Text);
        Assert.Equal("Done", updates[1].Text);
        Assert.Equal(
            "provider.request.start,provider.stream.start,provider.stream.first_event,provider.stream.completed",
            string.Join(",", sink.EventNames));
    }

    [Fact]
    public async Task ToolResponse_RejectsMalformedArgumentValue()
    {
        var handler = new CapturingHandler(_ => JsonResponse(ToolResponse("{not-json")));
        var client = CreateChatClient(handler);
        var options = new ChatOptions
        {
            ToolMode = new AutoChatToolMode(),
            Tools = [CreateTool()],
        };

        var exception = await Assert.ThrowsAsync<AgentChatProviderException>(() => ReadUpdatesAsync(
            client,
            [new ChatMessage(ChatRole.User, "Read.")],
            options));

        Assert.Equal("anthropic-malformed-tool-call", exception.ErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolResponse_RespectsMultipleToolCallOption(bool allowMultipleToolCalls)
    {
        var handler = new CapturingHandler(_ => JsonResponse(MultipleToolResponse()));
        var client = CreateChatClient(handler);
        var options = new ChatOptions
        {
            ToolMode = new AutoChatToolMode(),
            Tools = [CreateTool()],
            AllowMultipleToolCalls = allowMultipleToolCalls,
        };

        if (!allowMultipleToolCalls)
        {
            var exception = await Assert.ThrowsAsync<AgentChatProviderException>(() => ReadUpdatesAsync(
                client,
                [new ChatMessage(ChatRole.User, "Read both.")],
                options));
            Assert.Equal("anthropic-multiple-tool-calls", exception.ErrorCode);
            return;
        }

        var updates = await ReadUpdatesAsync(
            client,
            [new ChatMessage(ChatRole.User, "Read both.")],
            options);
        Assert.Equal(2, Assert.Single(updates).Contents.OfType<FunctionCallContent>().Count());
    }

    [Fact]
    public async Task StreamingCancellation_IsNotMappedAndIsLogged()
    {
        var handler = new BlockingHandler();
        var sink = new RecordingSink();
        var client = CreateChatClient(handler, sink);
        using var cancellation = new CancellationTokenSource();
        var readTask = ReadUpdatesAsync(
            client,
            [new ChatMessage(ChatRole.User, "Wait.")],
            cancellationToken: cancellation.Token);
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
        Assert.Contains("provider.stream.canceled", sink.EventNames);
    }

    [Fact]
    public async Task HttpFailure_IsMappedAndLogged()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(
                "{\"type\":\"error\",\"error\":{\"type\":\"api_error\",\"message\":\"boom\"}}",
                Encoding.UTF8,
                "application/json"),
        });
        var sink = new RecordingSink();
        var client = CreateChatClient(handler, sink);

        var exception = await Assert.ThrowsAsync<AgentChatProviderException>(() => ReadUpdatesAsync(
            client,
            [new ChatMessage(ChatRole.User, "Fail.")]));

        Assert.Equal("anthropic-http-error", exception.ErrorCode);
        Assert.Contains("provider.stream.failed", sink.EventNames);
    }

    [Fact]
    public async Task LoggingFailure_DoesNotFailProviderRequest()
    {
        var handler = new CapturingHandler(_ => SseResponse(
            """
             event: content_block_delta
             data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Still works"}}

             event: message_delta
             data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}

             event: message_stop
            data: {"type":"message_stop"}

            """));
        var client = CreateChatClient(handler, new ThrowingSink());

        var updates = await ReadUpdatesAsync(client, [new ChatMessage(ChatRole.User, "Continue.")]);

        Assert.Equal("Still works", Assert.Single(updates).Text);
    }

    [Fact]
    public async Task LoggingCancellation_PropagatesBeforeWireRequest()
    {
        var handler = new CapturingHandler(_ => throw new InvalidOperationException("Request should not be sent."));
        var client = CreateChatClient(handler, new CancelingSink());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadUpdatesAsync(
            client,
            [new ChatMessage(ChatRole.User, "Do not send.")],
            cancellationToken: cancellation.Token));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ProviderCatalog_PreservesReasoningAndFastContracts()
    {
        var context = new ProviderTestPackageContext("sunder.package.agent.provider.anthropic");
        var provider = new AnthropicAgentProvider(context);

        var models = await provider.GetAvailableModelsAsync();

        var opus = models.Single(model => model.ModelId == ModelId);
        Assert.Contains(opus.Variants ?? [], variant => variant.ReasoningEffort == AgentReasoningEffort.ExtraHigh);
        Assert.Contains(opus.SpeedOptions ?? [], option => option.SpeedOptionId == "fast");
        Assert.Equal(128000, opus.MaxOutputTokens);
    }

    [Fact]
    public async Task ToolResponse_PreservesMixedTextAndToolOrdering()
    {
        var handler = new CapturingHandler(_ => JsonResponse(MixedToolResponse()));
        var client = CreateChatClient(handler);

        var updates = await ReadUpdatesAsync(
            client,
            [new ChatMessage(ChatRole.User, "Read.")],
            new ChatOptions { ToolMode = ChatToolMode.Auto, Tools = [CreateTool()] });

        var contents = Assert.Single(updates).Contents;
        Assert.Equal("Before", Assert.IsType<TextContent>(contents[0]).Text);
        Assert.Equal("read_file", Assert.IsType<FunctionCallContent>(contents[1]).Name);
        Assert.Equal("After", Assert.IsType<TextContent>(contents[2]).Text);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("any")]
    [InlineData("tool")]
    public async Task ToolModes_MapToAnthropicToolChoice(string expectedType)
    {
        var handler = new CapturingHandler(_ => JsonResponse(SingleToolResponse()));
        var client = CreateChatClient(handler);
        ChatToolMode mode = expectedType switch
        {
            "auto" => ChatToolMode.Auto,
            "any" => ChatToolMode.RequireAny,
            _ => ChatToolMode.RequireSpecific("read_file"),
        };

        await ReadUpdatesAsync(
            client,
            [new ChatMessage(ChatRole.User, "Read.")],
            new ChatOptions { ToolMode = mode, Tools = [CreateTool()] });

        using var document = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var choice = document.RootElement.GetProperty("tool_choice");
        Assert.Equal(expectedType, choice.GetProperty("type").GetString());
        if (expectedType == "tool")
        {
            Assert.Equal("read_file", choice.GetProperty("name").GetString());
        }
    }

    [Fact]
    public async Task StreamingPrematureEof_IsFailure()
    {
        var handler = new CapturingHandler(_ => SseResponse(
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"partial\"}}\n\n"));
        var client = CreateChatClient(handler);

        var exception = await Assert.ThrowsAsync<AgentChatProviderException>(() => ReadUpdatesAsync(
            client,
            [new ChatMessage(ChatRole.User, "Continue.")]));

        Assert.Equal("anthropic-incomplete-response", exception.ErrorCode);
    }

    [Fact]
    public async Task ProviderCancellationWithoutCallerCancellation_IsTimeoutFailure()
    {
        var translator = new AnthropicResponseTranslator(
            new AgentChatClientContext("anthropic", ModelId));

        var exception = await Assert.ThrowsAsync<AgentChatProviderException>(async () =>
        {
            await foreach (var _ in translator.TranslateStreamingResponseAsync(
                               ProviderCanceledStream,
                               static _ => default,
                               ModelId,
                               CancellationToken.None))
            {
            }
        });

        Assert.Equal("anthropic-timeout", exception.ErrorCode);
    }

    private static AnthropicChatClient CreateChatClient(
        HttpMessageHandler handler,
        IAgentProviderEventSink? eventSink = null)
    {
        var context = new AgentChatClientContext("anthropic", ModelId, eventSink);
        return new AnthropicChatClient(
            context,
            new ProviderCredentialAccessor(
                new ProviderTestSecrets(new Dictionary<string, string>
                {
                    [AnthropicProviderConfiguration.ApiKeySecretKey] = "test-key",
                }),
                AnthropicProviderConfiguration.ApiKeySecretKey),
            (_, useFastMode, transportContext) =>
            {
                IAnthropicClient sdkClient = new AnthropicClient
                {
                    ApiKey = "test-key",
                    HttpClient = new HttpClient(handler, disposeHandler: false),
                    MaxRetries = 0,
                };
                var responseTranslator = new AnthropicResponseTranslator(transportContext);
                return useFastMode
                    ? new AnthropicFastTransport(sdkClient, responseTranslator)
                    : new AnthropicStandardTransport(sdkClient, responseTranslator);
            });
    }

    private static AIFunctionDeclaration CreateTool()
    {
        using var schema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "path": { "type": "string" }
              },
              "required": ["path"]
            }
            """);
        return AIFunctionFactory.CreateDeclaration(
            "read_file",
            "Read a file.",
            schema.RootElement.Clone(),
            returnJsonSchema: null);
    }

    private static async Task<IReadOnlyList<ChatResponseUpdate>> ReadUpdatesAsync(
        IChatClient client,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            updates.Add(update);
        }

        return updates;
    }

    private static HttpResponseMessage JsonResponse(string content)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage SseResponse(string content)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(NormalizeLines(content), Encoding.UTF8, "text/event-stream"),
        };

    private static string TextResponse(string text)
        => $$"""
        {
          "id": "msg-1",
          "type": "message",
          "role": "assistant",
          "content": [{"type":"text","text":{{JsonSerializer.Serialize(text)}}}],
          "model": "claude-opus-4-8",
          "stop_reason": "end_turn",
          "stop_sequence": null,
          "usage": {"input_tokens":1,"output_tokens":1}
        }
        """;

    private static string ToolResponse(string malformedArguments)
        => $$"""
        {
          "id": "msg-1",
          "type": "message",
          "role": "assistant",
          "content": [{"type":"tool_use","id":"call-1","name":"read_file","input":{{JsonSerializer.Serialize(malformedArguments)}}}],
          "model": "claude-opus-4-8",
          "stop_reason": "tool_use",
          "stop_sequence": null,
          "usage": {"input_tokens":1,"output_tokens":1}
        }
        """;

    private static string MultipleToolResponse()
        => """
        {
          "id": "msg-1",
          "type": "message",
          "role": "assistant",
          "content": [
            {"type":"tool_use","id":"call-1","name":"read_file","input":{"path":"one.txt"}},
            {"type":"tool_use","id":"call-2","name":"read_file","input":{"path":"two.txt"}}
          ],
          "model": "claude-opus-4-8",
          "stop_reason": "tool_use",
          "stop_sequence": null,
          "usage": {"input_tokens":1,"output_tokens":1}
        }
        """;

    private static string SingleToolResponse()
        => """
        {
          "id": "msg-1",
          "type": "message",
          "role": "assistant",
          "content": [{"type":"tool_use","id":"call-1","name":"read_file","input":{"path":"README.md"}}],
          "model": "claude-opus-4-8",
          "stop_reason": "tool_use",
          "stop_sequence": null,
          "usage": {"input_tokens":1,"output_tokens":1}
        }
        """;

    private static string MixedToolResponse()
        => """
        {
          "id": "msg-1",
          "type": "message",
          "role": "assistant",
          "content": [
            {"type":"text","text":"Before"},
            {"type":"tool_use","id":"call-1","name":"read_file","input":{"path":"README.md"}},
            {"type":"text","text":"After"}
          ],
          "model": "claude-opus-4-8",
          "stop_reason": "tool_use",
          "stop_sequence": null,
          "usage": {"input_tokens":1,"output_tokens":1}
        }
        """;

    private static string NormalizeLines(string content)
        => string.Join("\n", content.Split('\n').Select(line => line.TrimStart())) + "\n";

    private static async IAsyncEnumerable<int> ProviderCanceledStream()
    {
        await Task.Yield();
        throw new OperationCanceledException("provider timeout");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> createResponse) : HttpMessageHandler
    {
        internal List<CapturedRequest> Requests { get; } = [];

        internal List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(body);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.Select(header => new CapturedHeader(header.Key, string.Join(",", header.Value))).ToArray()));
            return createResponse(request);
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        internal TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class RecordingSink : IAgentProviderEventSink
    {
        internal List<string> EventNames { get; } = [];

        public ValueTask WriteAsync(
            AgentLogLevel level,
            string eventName,
            string message,
            IReadOnlyDictionary<string, object?>? attributes = null,
            Exception? exception = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EventNames.Add(eventName);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingSink : IAgentProviderEventSink
    {
        public ValueTask WriteAsync(
            AgentLogLevel level,
            string eventName,
            string message,
            IReadOnlyDictionary<string, object?>? attributes = null,
            Exception? exception = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Logging failed.");
    }

    private sealed class CancelingSink : IAgentProviderEventSink
    {
        public ValueTask WriteAsync(
            AgentLogLevel level,
            string eventName,
            string message,
            IReadOnlyDictionary<string, object?>? attributes = null,
            Exception? exception = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri? RequestUri,
        IReadOnlyList<CapturedHeader> Headers);

    private sealed record CapturedHeader(string Key, string Value);
}
