using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.TestSupport;
using Sunder.Sdk.Logging;
using Xunit;

namespace Sunder.Package.Agent.Provider.LMStudio.Tests;

public sealed class LMStudioTranslationTests
{
    [Fact]
    public async Task ToolImageIsNativeImageContentNotAStringifiedToolResult()
    {
        const string stream = "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        var handler = new LMStudioTestHttpHandler((_, _, _) => Task.FromResult(new HttpResponseMessage
            { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") }));
        var context = new ProviderTestPackageContext("younics.ai.agent.provider.lmstudio", new Dictionary<string, string>
            { [LMStudioProviderConfiguration.BaseUrlKey] = "http://lmstudio.test/v1" });
        using var connection = new LMStudioConnection(context, handler);
        using var client = new LMStudioChatClient(new AgentChatClientContext("lmstudio", "lmstudio/model"), connection);
        await foreach (var _ in client.GetStreamingResponseAsync(
        [
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("capture-1", "maker_screenshot", new Dictionary<string, object?>())]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("capture-1", new List<AIContent>
                { new TextContent("Untrusted package pixels"), new DataContent(new byte[] { 1, 2, 3 }, "image/png") })]),
        ])) { }
        using var json = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        var result = json.RootElement.GetProperty("messages")[1];
        Assert.Equal("tool", result.GetProperty("role").GetString());
        Assert.Equal("capture-1", result.GetProperty("tool_call_id").GetString());
        var parts = result.GetProperty("content");
        Assert.Equal(JsonValueKind.Array, parts.ValueKind);
        Assert.Equal("image_url", parts[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64,AQID", parts[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    public void OptionsTranslator_MapsExactSupportedEffortsWithoutInventingSpeedOrOnOffLevels(string effort)
    {
        var options = new ChatOptions { AdditionalProperties = new() { [AgentChatModelOptionKeys.ReasoningVariantId] = effort } };
#pragma warning disable OPENAI001
        Assert.Equal(effort, LMStudioOpenAIOptionsTranslator.Translate(options).ReasoningEffortLevel?.ToString());
#pragma warning restore OPENAI001
        options.AdditionalProperties[AgentChatModelOptionKeys.ReasoningVariantId] = "max";
        Assert.Throws<AgentChatProviderException>(() => LMStudioOpenAIOptionsTranslator.Translate(options));
        options.AdditionalProperties[AgentChatModelOptionKeys.ReasoningVariantId] = null;
        options.AdditionalProperties[AgentChatModelOptionKeys.SpeedOptionId] = "fast";
        Assert.Throws<AgentChatProviderException>(() => LMStudioOpenAIOptionsTranslator.Translate(options));
    }

    [Fact]
    public async Task ChatClient_TranslatesRolesToolsAndMaxOutputOnWire()
    {
        const string stream = """
            data: {"id":"chatcmpl-1","object":"chat.completion.chunk","created":0,"model":"model","choices":[{"index":0,"delta":{"role":"assistant","content":"ok"},"finish_reason":null}]}

            data: {"id":"chatcmpl-1","object":"chat.completion.chunk","created":0,"model":"model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":7,"total_tokens":19,"prompt_tokens_details":{"cached_tokens":4},"completion_tokens_details":{"reasoning_tokens":3}}}

            data: [DONE]

            """;
        var handler = new LMStudioTestHttpHandler((_, _, _) => Task.FromResult(new HttpResponseMessage
        {
            Content = new StringContent(stream, Encoding.UTF8, "text/event-stream"),
        }));
        var context = new ProviderTestPackageContext(
            "younics.ai.agent.provider.lmstudio",
            new Dictionary<string, string>
            {
                [LMStudioProviderConfiguration.BaseUrlKey] = "http://127.0.0.1:1234/v1",
            },
            secretValues: new Dictionary<string, string>
            {
                [LMStudioProviderConfiguration.ApiKeyKey] = ProviderFailureInputs.SecretCanary,
            });
        var logger = new RecordingEventLogger();
        using var connection = new LMStudioConnection(context, handler);
        using var client = new LMStudioChatClient(
            new AgentChatClientContext("lmstudio", "lmstudio/model", logger),
            connection);
        var messages = new[]
        {
            new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?>
                {
                    ["path"] = "README.md",
                }),
            ]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "contents")]),
            new ChatMessage(ChatRole.User, ProviderFailureInputs.RequestCanary),
        };
        var updates = new List<ChatResponseUpdate>();

        await foreach (var update in client.GetStreamingResponseAsync(messages, new ChatOptions
        {
            Instructions = "Use tools safely.",
            MaxOutputTokens = 321,
        }))
        {
            updates.Add(update);
        }

        Assert.Equal("ok", Assert.Single(updates, update => !string.IsNullOrEmpty(update.Text)).Text);
        var usage = Assert.Single(updates.SelectMany(update => update.Contents).OfType<UsageContent>()).Details;
        Assert.Equal(12, usage.InputTokenCount);
        Assert.Equal(7, usage.OutputTokenCount);
        Assert.Equal(19, usage.TotalTokenCount);
        Assert.Equal(4, usage.CachedInputTokenCount);
        Assert.Equal(3, usage.ReasoningTokenCount);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", request.Authorization?.Scheme);
        Assert.Equal(ProviderFailureInputs.SecretCanary, request.Authorization?.Parameter);
        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal(321, root.GetProperty("max_completion_tokens").GetInt32());
        var wireMessages = root.GetProperty("messages");
        Assert.Equal(new[] { "system", "assistant", "tool", "user" },
            wireMessages.EnumerateArray().Select(message => message.GetProperty("role").GetString()).ToArray());
        Assert.Equal("read_file", wireMessages[1].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("call-1", wireMessages[2].GetProperty("tool_call_id").GetString());
        ProviderConformanceHarness.VerifyTelemetryAndRedaction(
            "LM Studio",
            logger.EventNames,
            logger.TelemetryText,
            ProviderFailureInputs.SecretCanary,
            ProviderFailureInputs.RequestCanary);
    }

    [Fact]
    public async Task ChatClient_PreservesCommonMixedTextAndToolOrdering()
    {
        const string stream = """
            data: {"id":"chatcmpl-1","object":"chat.completion.chunk","created":0,"model":"model","choices":[{"index":0,"delta":{"content":"Before"},"finish_reason":null}]}

            data: {"id":"chatcmpl-1","object":"chat.completion.chunk","created":0,"model":"model","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call-1","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"README.md\"}"}}]},"finish_reason":null}]}

            data: {"id":"chatcmpl-1","object":"chat.completion.chunk","created":0,"model":"model","choices":[{"index":0,"delta":{"content":"After"},"finish_reason":null}]}

            data: {"id":"chatcmpl-1","object":"chat.completion.chunk","created":0,"model":"model","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;
        var handler = new LMStudioTestHttpHandler((_, _, _) => Task.FromResult(new HttpResponseMessage
        {
            Content = new StringContent(stream, Encoding.UTF8, "text/event-stream"),
        }));
        var context = new ProviderTestPackageContext(
            "younics.ai.agent.provider.lmstudio",
            new Dictionary<string, string>
            {
                [LMStudioProviderConfiguration.BaseUrlKey] = "http://lmstudio.test/v1",
            });
        using var connection = new LMStudioConnection(context, handler);
        using var client = new LMStudioChatClient(
            new AgentChatClientContext("lmstudio", "lmstudio/model"),
            connection);
        var updates = new List<ChatResponseUpdate>();

        await foreach (var update in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "Use the tool.")],
                           new ChatOptions { AllowMultipleToolCalls = true }))
        {
            updates.Add(update);
        }

        ProviderConformanceHarness.VerifyToolResultOrder(
            "LM Studio",
            updates.Select(static update => update.Contents.Any(static content => content is FunctionCallContent)
                ? "tool"
                : "text"));
    }

    [Fact]
    public void OptionsTranslator_MapsMaxOutputTokens()
    {
        var translated = LMStudioOpenAIOptionsTranslator.Translate(new ChatOptions
        {
            MaxOutputTokens = 777,
        });

        Assert.Equal(777, translated.MaxOutputTokenCount);
    }

    [Fact]
    public void StreamTranslator_RejectsMalformedToolArguments()
    {
        var translator = new LMStudioOpenAIStreamTranslator(allowMultipleToolCalls: false);
        translator.ApplyToolCallDelta(0, "call-1", "read_file", "{not-json");

        var exception = Assert.Throws<AgentChatProviderException>(() =>
            translator.Complete("response", "message", "lmstudio/model"));

        Assert.Equal("lmstudio-malformed-tool-call", exception.ErrorCode);
    }

    [Fact]
    public void StreamTranslator_RejectsConflictingToolCallIdentity()
    {
        var translator = new LMStudioOpenAIStreamTranslator(allowMultipleToolCalls: true);
        translator.ApplyToolCallDelta(0, "call-1", "read_file", "{");

        var exception = Assert.Throws<AgentChatProviderException>(() =>
            translator.ApplyToolCallDelta(0, "call-2", "read_file", "}"));

        Assert.Equal("lmstudio-malformed-tool-call", exception.ErrorCode);
    }

    [Fact]
    public void StreamTranslator_DoesNotApplyFormerGenericToolArgumentByteCap()
    {
        const int formerMaximumBytes = 1024 * 1024;
        var translator = new LMStudioOpenAIStreamTranslator(allowMultipleToolCalls: false);
        var arguments = "{\"value\":\"" + new string('x', formerMaximumBytes) + "\"}";

        translator.ApplyToolCallDelta(0, "call-1", "read_file", arguments);
        var update = translator.Complete("response", "message", "lmstudio/model");

        Assert.NotNull(update);
        Assert.IsType<FunctionCallContent>(Assert.Single(update.Contents));
    }

    [Fact]
    public async Task ChatClient_PrematureEofIsFailure()
    {
        const string stream = """
            data: {"id":"chatcmpl-1","object":"chat.completion.chunk","created":0,"model":"model","choices":[{"index":0,"delta":{"role":"assistant","content":"partial"},"finish_reason":null}]}

            """;
        var handler = new LMStudioTestHttpHandler((_, _, _) => Task.FromResult(new HttpResponseMessage
        {
            Content = new StringContent(stream, Encoding.UTF8, "text/event-stream"),
        }));
        var context = new ProviderTestPackageContext(
            "younics.ai.agent.provider.lmstudio",
            new Dictionary<string, string>
            {
                [LMStudioProviderConfiguration.BaseUrlKey] = "http://lmstudio.test/v1",
            });
        using var connection = new LMStudioConnection(context, handler);
        using var client = new LMStudioChatClient(
            new AgentChatClientContext("lmstudio", "lmstudio/model"),
            connection);

        var exception = await Assert.ThrowsAsync<AgentChatProviderException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Continue.")]))
            {
            }
        });

        Assert.Equal("lmstudio-incomplete-response", exception.ErrorCode);
        ProviderConformanceHarness.VerifyPrematureEof("LM Studio", exception.ErrorCode);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("content_filter")]
    public async Task ChatClient_IncompleteFinishReasonsAreFailures(string finishReason)
    {
        var stream = $"data: {{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"model\",\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"{finishReason}\"}}]}}\n\n";
        var handler = new LMStudioTestHttpHandler((_, _, _) => Task.FromResult(new HttpResponseMessage
        {
            Content = new StringContent(stream, Encoding.UTF8, "text/event-stream"),
        }));
        var context = new ProviderTestPackageContext(
            "younics.ai.agent.provider.lmstudio",
            new Dictionary<string, string>
            {
                [LMStudioProviderConfiguration.BaseUrlKey] = "http://lmstudio.test/v1",
            });
        using var connection = new LMStudioConnection(context, handler);
        using var client = new LMStudioChatClient(
            new AgentChatClientContext("lmstudio", "lmstudio/model"),
            connection);

        var exception = await Assert.ThrowsAsync<AgentChatProviderException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Continue.")]))
            {
            }
        });

        Assert.Equal("lmstudio-incomplete-response", exception.ErrorCode);
    }

    [Fact]
    public async Task ChatClient_ProviderTimeoutIsNotReportedAsCallerCancellation()
    {
        var handler = new LMStudioTestHttpHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        var context = new ProviderTestPackageContext(
            "younics.ai.agent.provider.lmstudio",
            new Dictionary<string, string>
            {
                [LMStudioProviderConfiguration.BaseUrlKey] = "http://lmstudio.test/v1",
            });
        using var connection = new LMStudioConnection(context, handler, TimeSpan.FromMilliseconds(50));
        using var client = new LMStudioChatClient(
            new AgentChatClientContext("lmstudio", "lmstudio/model"),
            connection);

        var exception = await Assert.ThrowsAsync<AgentChatProviderException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Wait.")]))
            {
            }
        });

        Assert.Equal("lmstudio-timeout", exception.ErrorCode);
        ProviderConformanceHarness.VerifyProviderTimeout("LM Studio", exception.ErrorCode);
    }

    [Fact]
    public async Task ChatClient_ContextWindowFailureIsNormalized()
    {
        var handler = new LMStudioTestHttpHandler((_, _, _) => LMStudioTestHttpHandler.Json(
            "{\"error\":{\"message\":\"This model's maximum context length is 4096 tokens. However, your messages resulted in 5000 tokens.\",\"type\":\"invalid_request_error\",\"param\":\"messages\",\"code\":\"context_length_exceeded\"}}",
            HttpStatusCode.BadRequest));
        var context = new ProviderTestPackageContext(
            "younics.ai.agent.provider.lmstudio",
            new Dictionary<string, string>
            {
                [LMStudioProviderConfiguration.BaseUrlKey] = "http://lmstudio.test/v1",
            });
        using var connection = new LMStudioConnection(context, handler);
        using var client = new LMStudioChatClient(
            new AgentChatClientContext("lmstudio", "lmstudio/model"),
            connection);

        var exception = await Assert.ThrowsAsync<AgentChatProviderException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Continue.")]))
            {
            }
        });

        Assert.Equal(AgentChatProviderFailureKind.ContextWindowExceeded, exception.FailureKind);
    }

    [Fact]
    public void ExceptionMapper_SatisfiesCommonFailureConformance()
        => ProviderConformanceHarness.VerifyFailureClassification(new ProviderFailureConformanceAdapter(
            "LM Studio",
            exception => LMStudioExceptionMapper.Map(exception).FailureKind
                == AgentChatProviderFailureKind.ContextWindowExceeded
                    ? ProviderFailureClassification.ContextWindowExceeded
                    : ProviderFailureClassification.Unknown));

    [Fact]
    public async Task RealProviderAdapter_SatisfiesUnifiedConformanceContract()
        => await ProviderConformanceHarness.VerifyAsync(new ProviderConformanceAdapter(
            "LM Studio",
            () =>
            {
                ExceptionMapper_SatisfiesCommonFailureConformance();
                return Task.CompletedTask;
            },
            ChatClient_PrematureEofIsFailure,
            () => new LMStudioProviderCancellationTests().CatalogAndReadiness_PropagateCancellation(
                embeddings: false,
                readiness: false),
            ChatClient_ProviderTimeoutIsNotReportedAsCallerCancellation,
            () => new LMStudioModelCatalogServiceTests().Readiness_SatisfiesCommonLifecycleConformance(),
            ChatClient_PreservesCommonMixedTextAndToolOrdering,
            ChatClient_TranslatesRolesToolsAndMaxOutputOnWire));

    [Theory]
    [InlineData("LM Studio returned HTTP 400: bad request")]
    [InlineData("max_output_tokens exceeds the supported value")]
    [InlineData("The output length limit was reached")]
    [InlineData("Insufficient quota")]
    [InlineData("Request too large")]
    [InlineData("{\"error\":{\"message\":\"backend failed\"},\"output\":\"context_length_exceeded was mentioned by the user\"}")]
    [InlineData("\"{\\\"error\\\":{\\\"message\\\":\\\"backend failed\\\"},\\\"output\\\":\\\"context_length_exceeded was mentioned by the user\\\"}\"")]
    public void ExceptionMapper_DoesNotClassifyAmbiguousFailures(string message)
    {
        var exception = LMStudioExceptionMapper.Map(new InvalidOperationException(message));

        Assert.Equal(AgentChatProviderFailureKind.Unknown, exception.FailureKind);
    }

    private sealed class RecordingEventLogger : IPackageEventLogger
    {
        public List<(string EventName, string Text)> Entries { get; } = [];

        public IEnumerable<string> EventNames => Entries.Select(static entry => entry.EventName);

        public string TelemetryText => string.Join('\n', Entries.Select(static entry => entry.Text));

        public ValueTask WriteAsync(
            PackageLogLevel level,
            string eventName,
            string message,
            IReadOnlyDictionary<string, object?>? attributes = null,
            Exception? exception = null,
            CancellationToken cancellationToken = default)
        {
            Entries.Add((
                eventName,
                string.Join(
                    "|",
                    eventName,
                    message,
                    string.Join(",", (attributes ?? new Dictionary<string, object?>())
                        .Select(static attribute => $"{attribute.Key}={attribute.Value}")))));
            return ValueTask.CompletedTask;
        }
    }
}
