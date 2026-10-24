using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.OpenAI.Transport;
using Xunit;
using System.ClientModel;
using System.ClientModel.Primitives;
using OpenAI;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Provider.OpenAI.Tests;

#pragma warning disable OPENAI001, SCME0001

public sealed partial class OpenAiAgentProviderTests
{
    [Fact]
    public async Task ApiAdapter_SendsMaxUnchangedAndReportsOnlyTheReturnedProcessingTier()
    {
        var handler = new CapturingHttpMessageHandler(_ => new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"id":"resp_1","object":"response","created_at":1,"status":"completed","model":"gpt-6.1-sol",
                 "parallel_tool_calls":false,"instructions":null,"service_tier":"default",
                 "output":[{"id":"msg_1","type":"message","role":"assistant","status":"completed",
                 "content":[{"type":"output_text","text":"ok","annotations":[]}]}]}
                """),
        });
        using var http = new HttpClient(handler);
        var sdk = new OpenAIClient(new ApiKeyCredential("sk-not-sent-to-a-network"),
            new() { Transport = new HttpClientPipelineTransport(http) });
        var logger = new TierLogger();
        using var client = new OpenAiModelOptionsChatClient(sdk.GetResponsesClient().AsIChatClient("gpt-6.1-sol"),
            new("openai", "openai/gpt-6.1-sol", logger));
        var result = await client.GetResponseAsync([new(ChatRole.User, "Hello")], new()
        {
            AdditionalProperties = new()
            {
                [AgentChatModelOptionKeys.ReasoningVariantId] = "max",
                [AgentChatModelOptionKeys.SpeedOptionId] = "fast",
            },
        });
        Assert.Equal("ok", result.Text);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal("gpt-6.1-sol", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("max", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("priority", body.RootElement.GetProperty("service_tier").GetString());
        Assert.Equal(["default"], logger.Tiers);
    }

    // Protect the exact option boundary, independently of the older MEAI enum.
    [Theory]
    [InlineData(null, "normal", "default")]
    [InlineData("max", "normal", "default")]
    [InlineData("max", "fast", "priority")]
    public async Task ModelSelection_PreservesBaseIdExactEffortAndIndependentProcessing(
        string? effort, string speed, string tier)
    {
        var inner = new CapturingChatClient();
        using var client = new OpenAiModelOptionsChatClient(inner, new("openai", "openai/gpt-6.1-sol"));
        var options = new ChatOptions
        {
            ModelId = "gpt-6.1-sol",
            AdditionalProperties = new()
            {
                [AgentChatModelOptionKeys.ReasoningVariantId] = effort,
                [AgentChatModelOptionKeys.SpeedOptionId] = JsonSerializer.SerializeToElement(speed),
            },
        };

        await client.GetResponseAsync([new(ChatRole.User, "Hello")], options);

        var raw = Assert.IsType<CreateResponseOptions>(inner.CapturedOptions?.RawRepresentationFactory?.Invoke(inner));
        Assert.Equal("gpt-6.1-sol", inner.CapturedOptions?.ModelId);
        Assert.Equal(effort, raw.ReasoningOptions?.ReasoningEffortLevel?.ToString());
        Assert.Equal(tier, raw.ServiceTier?.ToString());
        Assert.Null(options.RawRepresentationFactory);
        var connected = CodexResponsesRequestNormalizer.Normalize(new("openai", "openai/gpt-6.1-sol"),
            [new(ChatRole.User, "Hello")], options, toolAware: false);
        Assert.Equal("gpt-6.1-sol", connected.Model);
        Assert.Equal(effort, connected.Reasoning?.Effort);
        Assert.Equal(tier, connected.ServiceTier);
    }

    [Theory]
    [InlineData("openai/gpt-6.1-sol", "none", "normal")]
    [InlineData("openai/gpt-6-astra", "none", "fast")]
    [InlineData("openai/gpt-4.1", "max", "normal")]
    [InlineData("openai/gpt-6.1-sol", "max", "ultrafast")]
    public async Task ModelSelection_RejectsUnsupportedCombinationsBeforeTransport(string model, string effort, string speed)
    {
        var inner = new CapturingChatClient();
        using var client = new OpenAiModelOptionsChatClient(inner, new("openai", model));
        var options = new ChatOptions { AdditionalProperties = new()
        {
            [AgentChatModelOptionKeys.ReasoningVariantId] = effort,
            [AgentChatModelOptionKeys.SpeedOptionId] = speed,
        } };

        var failure = await Assert.ThrowsAsync<AgentChatProviderException>(() => client.GetResponseAsync([], options));

        Assert.Equal("agent-model-option-unsupported", failure.ErrorCode);
        Assert.Null(inner.CapturedOptions);
    }

    [Fact]
    public void ModelSelection_RejectsConflictingLegacyHintAndConnectedOnlyRestrictions()
    {
        var options = new ChatOptions
        {
            Reasoning = new() { Effort = ReasoningEffort.High },
            AdditionalProperties = new() { [AgentChatModelOptionKeys.ReasoningVariantId] = "max" },
        };
        Assert.Throws<AgentChatProviderException>(() => CodexResponsesRequestNormalizer.Normalize(
            new("openai", "openai/gpt-6.1-sol"), [], options, toolAware: false));
        Assert.Throws<AgentChatProviderException>(() => CodexResponsesRequestNormalizer.Normalize(
            new("openai", "openai/gpt-4.1"), [], null, toolAware: false));
        Assert.DoesNotContain(OpenAiModelCatalog.CodexModels, model => model.ModelId == "openai/gpt-4.1");
        var api = OpenAiModelCatalog.Find("gpt-6.1-sol")!;
        var connected = OpenAiModelCatalog.Find("gpt-6.1-sol", connected: true)!;
        Assert.Equal(1050000, api.ContextWindow);
        Assert.Equal(628000, connected.ContextWindow);
        Assert.All(OpenAiModelCatalog.CodexModels, model => Assert.Null(model.ModeOptions));
    }

    private sealed class TierLogger : IPackageEventLogger
    {
        public List<string> Tiers { get; } = [];
        public ValueTask WriteAsync(PackageLogLevel level, string eventName, string message,
            IReadOnlyDictionary<string, object?>? attributes = null, Exception? exception = null, CancellationToken cancellationToken = default)
        {
            if (eventName == "provider.processing.tier") Tiers.Add((string)attributes!["processing.effective_tier"]!);
            return ValueTask.CompletedTask;
        }
    }
}
