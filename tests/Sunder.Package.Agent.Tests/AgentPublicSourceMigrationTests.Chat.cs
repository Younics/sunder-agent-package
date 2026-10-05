using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    private static async Task VerifyChatAsync(string root, AssemblyLoadContext loadContext, Assembly assembly,
        SunderRpcContractDescriptor descriptor)
    {
        await using var fixture = await CreateExtension(root, loadContext, assembly, "ChatProvider", descriptor);
        fixture.Probe.Replies["DescribeAsync"] = """
            {"providerId":"chat.one","displayName":"Chat","supportedAuthModes":["api-key","codex-connected"],"supportsStreaming":true,"supportsInterruptibleRuns":false}
            """;
        var description = await fixture.Call("DescribeAsync", EmptyRequest());
        Assert.False(description.GetProperty("supportsInterruptibleRuns").GetBoolean());
        Assert.False(description.TryGetProperty("packageId", out _));
        fixture.Probe.Replies["ListModelsAsync"] = """
            [{"modelId":"model.one","displayName":"Model","contextWindow":8192,"maxOutputTokens":1024,"isRecommended":false,"variants":[{"variantId":"deep","displayName":"Deep","reasoningEffort":"extra-high"}],"modeOptions":[{"modeOptionId":"fast","displayName":"Fast","disablesReasoning":false}],"releaseDate":"2026-09-18"}]
            """;
        var models = await fixture.Call("ListModelsAsync", EmptyRequest());
        Assert.Equal("2026-09-18", models[0].GetProperty("releaseDate").GetString());
        Assert.Equal("extra-high", models[0].GetProperty("variants")[0].GetProperty("reasoningEffort").GetString());
        fixture.Probe.Replies["GetReadinessAsync"] = "{\"providerId\":\"chat.one\",\"status\":\"needs-configuration\",\"message\":\"Configure\"}";
        Assert.Equal("needs-configuration", (await fixture.Call("GetReadinessAsync", EmptyRequest())).GetProperty("status").GetString());
        fixture.Probe.Replies["GetCapabilitiesAsync"] = """
            {"supportsNativeToolCalling":true,"supportsStreamingToolCalls":false,"supportsMultipleToolCalls":false,"summary":"Capabilities","supportsImageInput":false,"supportsPdfInput":false,"supportsAudioInput":false,"supportsVideoInput":false}
            """;
        var capabilities = await fixture.Call("GetCapabilitiesAsync", EmptyRequest());
        Assert.False(capabilities.GetProperty("supportsStreamingToolCalls").GetBoolean());
        Assert.False(fixture.Probe.LastRequest.TryGetProperty("modelId", out _));
        fixture.Probe.Replies["ResolveUtilityModelAsync"] = "null";
        Assert.Equal(JsonValueKind.Null, (await fixture.Call("ResolveUtilityModelAsync", EmptyRequest())).ValueKind);
        fixture.Probe.Replies["ResolveUtilityModelAsync"] = "\"utility.one\"";
        Assert.Equal("utility.one", (await fixture.Call("ResolveUtilityModelAsync", EmptyRequest())).GetString());

        using var requestDocument = JsonDocument.Parse("""
            {"providerId":"chat.one","modelId":"model.one","messages":[{"role":"user","contents":[{"kind":"text","text":"Hello 漢字","informationalOnly":false},{"kind":"tool-result","json":[1,null,{"flag":false}],"informationalOnly":false}]}],"options":{"temperature":0.5,"seed":0,"allowMultipleToolCalls":false,"reasoningEffort":"extra-high","tools":[{"name":"lookup","jsonSchema":{"type":"object","properties":{"q":{"type":"string"}},"required":["q"],"additionalProperties":false}}],"additionalProperties":{"nested":{"values":[1,null,false]},"unset":null},"continuationHandle":"AAEC"}}
            """);
        var request = requestDocument.RootElement;
        const string update = """
            {"kind":"update","role":"assistant","createdAt":"2026-09-18T00:00:00Z","contentItems":[{"kind":"text","text":"Hello 漢字","informationalOnly":false},{"kind":"tool-call","callId":"call.one","name":"lookup","json":{"q":"value"},"informationalOnly":false}],"continuationHandle":"AAEC"}
            """;
        const string error = """
            {"kind":"error","errorCode":"context-limit","errorMessage":"Too much context","errorContent":"Bounded error","errorFailureKind":"context-window-exceeded"}
            """;
        fixture.Probe.Streams["StreamChatAsync"] = [update, error];
        var events = await ReadGeneratedStream(fixture.Client, CancellationToken.None, null, "StreamChatAsync", request);
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

        var invalidRequest = JsonNode.Parse(request.GetRawText())!;
        invalidRequest["options"]!["tools"]![0]!["jsonSchema"] = new JsonArray(1, 2);
        var calls = fixture.Probe.Calls;
        var rejected = await Assert.ThrowsAsync<SunderRpcException>(() => ReadGeneratedStream(fixture.Client,
            CancellationToken.None, null, "StreamChatAsync", JsonSerializer.SerializeToElement(invalidRequest)));
        Assert.Equal(SunderRpcErrorKind.Validation, rejected.Error.Kind);
        Assert.Equal(calls, fixture.Probe.Calls);
        foreach (var invalidEvent in new[] { "{\"kind\":\"unknown\"}", update.Replace("\"kind\":\"text\"", "\"kind\":\"unknown\"", StringComparison.Ordinal) })
        {
            fixture.Probe.Streams["StreamChatAsync"] = [invalidEvent];
            var failure = await Assert.ThrowsAsync<SunderRpcException>(() => ReadGeneratedStream(fixture.Client, CancellationToken.None, null, "StreamChatAsync", request));
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, failure.Error.Kind);
        }
        fixture.Probe.Streams["StreamChatAsync"] = [update];
        fixture.Probe.WaitAfterEvent = true;
        using var cancellation = new CancellationTokenSource();
        Assert.Single(await ReadGeneratedStream(fixture.Client, cancellation.Token, cancellation, "StreamChatAsync", request));
    }
}
