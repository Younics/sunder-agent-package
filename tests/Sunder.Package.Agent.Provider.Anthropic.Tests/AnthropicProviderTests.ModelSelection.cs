using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Xunit;

namespace Sunder.Package.Agent.Provider.Anthropic.Tests;

public sealed partial class AnthropicProviderTests
{
    [Theory]
    [InlineData("normal", false)]
    [InlineData("fast", true)]
    public async Task Opus55_MaxRemainsExactWithIndependentStableProcessing(string speed, bool fast)
    {
        var handler = new CapturingHandler(_ => JsonResponse(TextResponse("Done").Replace("\"output_tokens\":1", "\"output_tokens\":1,\"speed\":\"standard\"", StringComparison.Ordinal)));
        var logger = new RecordingLogger();
        using var client = CreateChatClient(handler, logger, modelId: "anthropic/claude-opus-5-5");
        await ReadUpdatesAsync(client, [new(ChatRole.User, "Think")], new()
        {
            Tools = [CreateTool()],
            AdditionalProperties = new()
            {
                [AgentChatModelOptionKeys.ReasoningVariantId] = "max",
                [AgentChatModelOptionKeys.SpeedOptionId] = JsonSerializer.SerializeToElement(speed),
            },
        });

        using var body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var wire = body.RootElement;
        Assert.Equal("claude-opus-5-5", wire.GetProperty("model").GetString());
        Assert.Equal("max", wire.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("adaptive", wire.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(128000, wire.GetProperty("max_tokens").GetInt64());
        Assert.Equal(fast, wire.TryGetProperty("speed", out var actualSpeed));
        if (fast) Assert.Equal("fast", actualSpeed.GetString());
        Assert.DoesNotContain(Assert.Single(handler.Requests).Headers,
            header => string.Equals(header.Key, "anthropic-beta", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("processing.effective_tier=standard", logger.TelemetryText, StringComparison.Ordinal);
    }

    [Fact]
    public void AdaptiveDefaults_OmitEffortAndPreserveExplicitOutputCap()
    {
        var defaults = AnthropicOptionsTranslator.Translate([], null, "claude-opus-5-5");
        Assert.Null(defaults.Parameters.OutputConfig);
        Assert.Null(defaults.Parameters.Thinking);
        var maximum = AnthropicOptionsTranslator.Translate([], new()
        {
            MaxOutputTokens = 4096,
            AdditionalProperties = new() { [AgentChatModelOptionKeys.ReasoningVariantId] = "max" },
        }, "claude-opus-5-5");
        Assert.Equal(4096, maximum.Parameters.MaxTokens);
        Assert.Null(maximum.TokenBudget.ThinkingTokens);
    }

    [Theory]
    [InlineData("claude-opus-5-5", "none", "normal")]
    [InlineData("claude-sonnet-5-5", "high", "fast")]
    [InlineData("claude-sonnet-4-6", "xhigh", "normal")]
    public void UnsupportedModelOptions_AreRejectedBeforeTransport(string model, string effort, string speed)
        => Assert.Throws<AgentChatProviderException>(() => AnthropicOptionsTranslator.Translate([], new()
        {
            AdditionalProperties = new()
            {
                [AgentChatModelOptionKeys.ReasoningVariantId] = effort,
                [AgentChatModelOptionKeys.SpeedOptionId] = speed,
            },
        }, model));
}
