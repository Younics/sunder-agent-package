using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.TestSupport;
using Xunit;

namespace Sunder.Package.Agent.Provider.LMStudio.Tests;

public sealed class LMStudioTranslationTests
{
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
            "sunder.package.agent.provider.lmstudio",
            new Dictionary<string, string>
            {
                [LMStudioProviderConfiguration.BaseUrlKey] = "http://lmstudio.test/v1",
            });
        using var connection = new LMStudioConnection(context, handler);
        using var client = new LMStudioChatClient(
            new AgentChatClientContext("lmstudio", "lmstudio/model"),
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
            new ChatMessage(ChatRole.User, "Continue."),
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
        Assert.Null(request.Authorization);
        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal(321, root.GetProperty("max_completion_tokens").GetInt32());
        var wireMessages = root.GetProperty("messages");
        Assert.Equal(new[] { "system", "assistant", "tool", "user" },
            wireMessages.EnumerateArray().Select(message => message.GetProperty("role").GetString()).ToArray());
        Assert.Equal("read_file", wireMessages[1].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("call-1", wireMessages[2].GetProperty("tool_call_id").GetString());
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
    public void StreamTranslator_RejectsOversizedAccumulatedToolArguments()
    {
        var translator = new LMStudioOpenAIStreamTranslator(allowMultipleToolCalls: false);

        var exception = Assert.Throws<AgentChatProviderException>(() =>
            translator.ApplyToolCallDelta(
                0,
                "call-1",
                "read_file",
                new string('x', AgentPayloadLimits.MaxStreamedToolArgumentBytes + 1)));

        Assert.Equal("lmstudio-malformed-tool-call", exception.ErrorCode);
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
            "sunder.package.agent.provider.lmstudio",
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
            "sunder.package.agent.provider.lmstudio",
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
            "sunder.package.agent.provider.lmstudio",
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
    }
}
