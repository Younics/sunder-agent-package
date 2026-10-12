using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    private static async Task VerifyChatAsync(string root, AssemblyLoadContext loadContext, Assembly assembly,
        SunderRpcContractDescriptor descriptor)
    {
        await using var fixture = await CreateExtension(root, loadContext, assembly, "ChatProvider", descriptor);
        fixture.Probe.Replies["DescribeAsync"] = """
            {"providerId":"chat.one","displayName":"Chat","packageId":null,"supportedAuthModes":["api-key","codex-connected"],"supportsStreaming":true,"supportsInterruptibleRuns":false}
            """;
        var description = await fixture.Call("DescribeAsync", EmptyRequest());
        Assert.False(description.GetProperty("supportsInterruptibleRuns").GetBoolean());
        Assert.False(description.TryGetProperty("packageId", out _));
        fixture.Probe.Replies["ListModelsAsync"] = """
            [{"modelId":"model.one","displayName":"Model","contextWindow":8192,"maxOutputTokens":1024,"isRecommended":false,"variants":[{"variantId":"deep","displayName":"Deep","description":null,"reasoningEffort":"extra-high"}],"modeOptions":[{"modeOptionId":"fast","displayName":"Fast","description":null,"disablesReasoning":false}],"speedOptions":null,"releaseDate":"2026-09-18"}]
            """;
        var models = await fixture.Call("ListModelsAsync", EmptyRequest());
        Assert.Equal("2026-09-18", models[0].GetProperty("releaseDate").GetString());
        Assert.Equal("extra-high", models[0].GetProperty("variants")[0].GetProperty("reasoningEffort").GetString());
        fixture.Probe.Replies["GetReadinessAsync"] = "{\"providerId\":\"chat.one\",\"status\":\"needs-configuration\",\"message\":\"Configure\"}";
        Assert.Equal("needs-configuration", (await fixture.Call("GetReadinessAsync", EmptyRequest())).GetProperty("status").GetString());
        fixture.Probe.Replies["GetCapabilitiesAsync"] = """
            {"supportsNativeToolCalling":true,"supportsStreamingToolCalls":false,"supportsMultipleToolCalls":false,"summary":"Capabilities","supportsImageInput":false,"supportsPdfInput":false,"supportsAudioInput":false,"supportsVideoInput":false,"contextWindowTokens":null,"maxOutputTokens":null}
            """;
        var capabilities = await fixture.Call("GetCapabilitiesAsync", EmptyRequest());
        Assert.False(capabilities.GetProperty("supportsStreamingToolCalls").GetBoolean());
        Assert.Equal(JsonValueKind.Null, fixture.Probe.LastRequest.ValueKind);
        fixture.Probe.Replies["ResolveUtilityModelAsync"] = "null";
        Assert.Equal(JsonValueKind.Null, (await fixture.Call("ResolveUtilityModelAsync", EmptyRequest())).ValueKind);
        fixture.Probe.Replies["ResolveUtilityModelAsync"] = "\"utility.one\"";
        Assert.Equal("utility.one", (await fixture.Call("ResolveUtilityModelAsync", EmptyRequest())).GetString());

        using var requestDocument = JsonDocument.Parse("""
            {"providerId":"chat.one","modelId":"model.one","messages":[{"role":"user","contents":[{"kind":"text","text":"Hello 漢字","informationalOnly":false},{"kind":"tool-result","json":[1,null,{"flag":false}],"informationalOnly":false}]}],"options":{"temperature":0.5,"seed":0,"allowMultipleToolCalls":false,"reasoningEffort":"extra-high","tools":[{"name":"lookup","jsonSchema":{"type":"object","properties":{"q":{"type":"string"}},"required":["q"],"additionalProperties":false}}],"additionalProperties":{"nested":{"values":[1,null,false]},"unset":null},"continuationHandle":"AAEC"}}
            """);
        var request = new AgentChatStreamRequest("chat.one", "model.one",
            [new("user", null, null, [new("text", Text: "Hello 漢字"),
                new("tool-result", Json: new(requestDocument.RootElement.GetProperty("messages")[0].GetProperty("contents")[1].GetProperty("json").Clone()))])],
            requestDocument.RootElement.GetProperty("options").Deserialize<AgentChatOptions>(ExecutionJson)!);
        var update = new AgentChatUpdateEvent(Role: "assistant", CreatedAt: new SunderRpcOffsetDateTime("2026-09-18T00:00:00Z"),
            ContentItems: [new("text", Text: "Hello 漢字"), new("tool-call", CallId: "call.one", Name: "lookup", Json: new(RunJson(new { q = "value" })))],
            ContinuationHandle: [0, 1, 2]);
        var error = new AgentChatErrorEvent("context-limit", "Too much context", "Bounded error", AgentChatProviderFailureKind.ContextWindowExceeded);
        fixture.Probe.TypedStreams["StreamChatAsync"] = [update, error];
        var client = Assert.IsAssignableFrom<AgentServices.IChatProviderService>(fixture.Client);
        var events = await ReadStream<AgentChatStreamEvent>(client.StreamChatAsync(request), default, null);
        var validGeneratedRequest = fixture.State.LastStreamInvocationRequest;
        Assert.Equal(["update", "error"], events.Select(value => value.GetProperty("kind").GetString()!).ToArray());
        Assert.Equal("Hello 漢字", events[0].GetProperty("contentItems")[0].GetProperty("text").GetString());
        Assert.Equal("context-window-exceeded", events[1].GetProperty("errorFailureKind").GetString());
        Assert.Equal("AAEC", events[0].GetProperty("continuationHandle").GetString());
        var received = fixture.Probe.LastRequest;
        Assert.Equal(JsonValueKind.Array, received.GetProperty("messages")[0].GetProperty("contents")[1].GetProperty("json").ValueKind);
        Assert.Equal(JsonValueKind.Object, received.GetProperty("options").GetProperty("tools")[0].GetProperty("jsonSchema").ValueKind);
        Assert.Equal(JsonValueKind.Null, received.GetProperty("options").GetProperty("additionalProperties").GetProperty("unset").ValueKind);
        Assert.Equal(0.5, received.GetProperty("options").GetProperty("temperature").GetDouble());
        Assert.False(received.GetProperty("options").GetProperty("allowMultipleToolCalls").GetBoolean());

        var invalidRequest = request with { Options = request.Options with
        { Tools = [request.Options.Tools[0] with { JsonSchema = JsonSerializer.SerializeToElement(new[] { 1, 2 }) }] } };
        var calls = fixture.Probe.Calls;
        var peerCalls = fixture.State.StreamInvocationAttemptCount;
        await Assert.ThrowsAsync<JsonException>(() => ReadStream<AgentChatStreamEvent>(client.StreamChatAsync(invalidRequest), default, null));
        Assert.Equal(peerCalls, fixture.State.StreamInvocationAttemptCount);
        Assert.Equal(calls, fixture.Probe.Calls);
        var invalidWire = JsonNode.Parse(validGeneratedRequest.GetRawText())!;
        invalidWire["options"]!["tools"]![0]!["jsonSchema"] = new JsonArray(1, 2);
        var endpoint = Assert.Single(fixture.State.Discover(descriptor.ContractId).Providers).Endpoint;
        var rejected = await Assert.ThrowsAsync<SunderRpcException>(async () =>
        {
            await foreach (var _ in fixture.Rpc.SubscribeAsync(endpoint, "chat-provider", "stream-chat", JsonSerializer.SerializeToElement(invalidWire))) { }
        });
        Assert.Equal(SunderRpcErrorKind.Validation, rejected.Error.Kind);
        Assert.Equal(peerCalls + 1, fixture.State.StreamInvocationAttemptCount);
        Assert.Equal(calls, fixture.Probe.Calls);
        fixture.Probe.TypedStreams.Remove("StreamChatAsync");
        fixture.Probe.Streams["StreamChatAsync"] = ["{\"kind\":\"unknown\"}"];
        var invalidVariant = await Assert.ThrowsAsync<SunderRpcException>(() => ReadStream<AgentChatStreamEvent>(client.StreamChatAsync(request), default, null));
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, invalidVariant.Error.Kind);
        fixture.Probe.Streams.Remove("StreamChatAsync");
        fixture.Probe.TypedStreams["StreamChatAsync"] = [update with { ContentItems = [update.ContentItems![0] with { Kind = "unknown" }] }];
        var invalidContent = await Assert.ThrowsAsync<SunderRpcException>(() => ReadStream<AgentChatStreamEvent>(client.StreamChatAsync(request), default, null));
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, invalidContent.Error.Kind);
        fixture.Probe.TypedStreams["StreamChatAsync"] = [update];
        fixture.Probe.WaitAfterEvent = true;
        using var cancellation = new CancellationTokenSource();
        Assert.Single(await ReadStream<AgentChatStreamEvent>(client.StreamChatAsync(request, cancellation.Token), cancellation.Token, cancellation));
    }
}
