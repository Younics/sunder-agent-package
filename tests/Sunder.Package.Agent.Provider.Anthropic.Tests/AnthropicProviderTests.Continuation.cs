using Microsoft.Extensions.AI;
using Xunit;

namespace Sunder.Package.Agent.Provider.Anthropic.Tests;

public sealed partial class AnthropicProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputLimitPreservesPartialTextForContinuation(bool withTools)
    {
        const string stream = """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg-1","type":"message","role":"assistant","content":[],"model":"claude-opus-4-8","stop_reason":null,"usage":{"input_tokens":1,"output_tokens":0}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Partial"}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"max_tokens"},"usage":{"output_tokens":1}}

            event: message_stop
            data: {"type":"message_stop"}

            """;
        var handler = new CapturingHandler(_ => withTools
            ? JsonResponse(TextResponse("Partial").Replace("end_turn", "max_tokens", StringComparison.Ordinal))
            : SseResponse(stream));
        using var client = CreateChatClient(handler);
        var updates = await ReadUpdatesAsync(client, [new ChatMessage(ChatRole.User, "Continue.")],
            withTools ? new ChatOptions { Tools = [CreateTool()] } : null);
        Assert.Equal("Partial", string.Concat(updates.Select(update => update.Text)));
        Assert.Contains(updates, update => update.FinishReason == ChatFinishReason.Length);
        Assert.Empty(updates.SelectMany(update => update.Contents).OfType<FunctionCallContent>());
    }
}
