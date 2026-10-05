using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    private static async Task VerifyToolLifecycleAsync(string root, AssemblyLoadContext loadContext, Assembly assembly,
        IReadOnlyDictionary<string, SunderRpcContractDescriptor> descriptors)
    {
        var invocation = JsonSerializer.SerializeToElement(new
        {
            context = new
            {
                approvedResourceCapabilities = Array.Empty<string>(),
                context = new
                {
                    allowOutsideConfiguredScope = false, approvedResourceClaims = Array.Empty<object>(),
                    approvedResourceReferences = Array.Empty<string>(), transcriptEpoch = 0,
                },
            },
            request = new { argumentsJson = "{unfinished", toolId = "tool.one" },
        });
        const string toolResult = """
            {"isError":false,"requiresPromptContextRefresh":false,"summary":"Done 漢字","toolId":"tool.one","wasTruncated":false,"structuredPayloadJson":"{opaque"}
            """;
        await using (var fixture = await CreateExtension(root, loadContext, assembly, "ToolSource", descriptors["sunder.agent.tool.source"]))
        {
            fixture.Probe.Replies["DescribeAsync"] = "{\"displayName\":\"Tools\",\"sourceId\":\"tools\",\"sourceKind\":\"local\"}";
            Assert.Equal("local", (await fixture.Call("DescribeAsync", EmptyRequest())).GetProperty("sourceKind").GetString());
            fixture.Probe.Replies["ListToolsAsync"] = """
                [{"concurrencyMode":"parallel-safe","description":"Read","displayName":"Read","isReadOnly":true,"priority":"high","requiresNetwork":false,"selectionScope":"group","toolId":"tool.one","argumentsJsonSchema":"{opaque"}]
                """;
            var list = await fixture.Call("ListToolsAsync", JsonSerializer.SerializeToElement(new { context = new { } }));
            Assert.Equal("parallel-safe", list[0].GetProperty("concurrencyMode").GetString());
            Assert.Equal("{opaque", list[0].GetProperty("argumentsJsonSchema").GetString());
            fixture.Probe.Replies["GetReadinessAsync"] = "null";
            Assert.Equal(JsonValueKind.Null, (await fixture.Call("GetReadinessAsync",
                JsonSerializer.SerializeToElement(new { context = new { context = new { } }, toolId = "tool.one" }))).ValueKind);
            foreach (var method in new[] { "PreflightAsync", "PermissionAsync" })
            {
                fixture.Probe.Replies[method] = "null";
                Assert.Equal(JsonValueKind.Null, (await fixture.Call(method, invocation)).ValueKind);
            }
            fixture.Probe.Replies["ExecuteAsync"] = toolResult;
            var result = await fixture.Call("ExecuteAsync", invocation);
            Assert.Equal("{opaque", result.GetProperty("structuredPayloadJson").GetString());
            Assert.False(result.GetProperty("requiresPromptContextRefresh").GetBoolean());
            Assert.Equal("{unfinished", fixture.Probe.LastRequest.GetProperty("request").GetProperty("argumentsJson").GetString());
            Assert.False(fixture.Probe.LastRequest.GetProperty("context").TryGetProperty("executionTarget", out _));
            fixture.Probe.Replies["PresentationAsync"] = "null";
            Assert.Equal(JsonValueKind.Null, (await fixture.Call("PresentationAsync",
                JsonSerializer.SerializeToElement(new { argumentsJson = "{broken", isError = false, toolId = "tool.one" }))).ValueKind);
        }

        await using (var fixture = await CreateExtension(root, loadContext, assembly, "ToolExecutionObserved", descriptors["sunder.agent.tool.execution.observed"]))
        {
            fixture.Probe.Replies["DescribeAsync"] = "{\"sourceId\":\"tools\"}";
            Assert.Equal("tools", (await fixture.Call("DescribeAsync", EmptyRequest())).GetProperty("sourceId").GetString());
            fixture.Probe.Streams["ExecuteAsync"] = ["{\"kind\":\"dispatched\"}", "{\"kind\":\"result\",\"result\":" + toolResult + "}"];
            var events = await ReadGeneratedStream(fixture.Client, CancellationToken.None, null, "ExecuteAsync", invocation);
            Assert.Equal(["dispatched", "result"], events.Select(value => value.GetProperty("kind").GetString()!).ToArray());
            Assert.False(events[0].TryGetProperty("result", out _));
            Assert.Equal("Done 漢字", events[1].GetProperty("result").GetProperty("summary").GetString());
            fixture.Probe.Streams["ExecuteAsync"] = ["{\"kind\":\"unknown\"}"];
            var invalid = await Assert.ThrowsAsync<SunderRpcException>(() => ReadGeneratedStream(fixture.Client, CancellationToken.None, null, "ExecuteAsync", invocation));
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, invalid.Error.Kind);
            fixture.Probe.Streams["ExecuteAsync"] = ["{\"kind\":\"dispatched\"}"];
            fixture.Probe.WaitAfterEvent = true;
            using var cancellation = new CancellationTokenSource();
            Assert.Single(await ReadGeneratedStream(fixture.Client, cancellation.Token, cancellation, "ExecuteAsync", invocation));
        }

        const string timestamp = "2026-09-18T00:00:00Z";
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var turn = new
        {
            contentRevision = 1, createdAtUtc = timestamp, isStreaming = false,
            items = new[] { new { argumentsJson = "{opaque", isError = false, isToolHeaderProjection = false,
                itemId = Guid.NewGuid(), kind = "tool-result", sequenceNumber = 0, toolDetailRevision = 0, turnId, wasTruncated = false } },
            kind = "message", role = "assistant", sessionId, turnId, updatedAtUtc = timestamp,
        };
        await using (var fixture = await CreateExtension(root, loadContext, assembly, "DurableLifecycleObserver", descriptors["sunder.agent.durable.lifecycle.observer"]))
        {
            var request = JsonSerializer.SerializeToElement(new
            {
                eventId = "event.one", kind = "tool-result-recorded", occurredAtUtc = timestamp, orderingKey = "session.one",
                payload = new
                {
                    contentErased = false, deletedChildSessionCount = 0, deletedChildSessionIds = Array.Empty<Guid>(),
                    deletedSessionCount = 0, deletedSessionIds = Array.Empty<Guid>(), deletedTurnCount = 0, deletedTurnIds = Array.Empty<Guid>(),
                    deletionChunkReceipts = Array.Empty<object>(), recentLiveBufferTurns = Array.Empty<object>(), turns = new[] { turn },
                },
                payloadHash = new string('a', 64), payloadJson = "{opaque", sequence = 1, sourceKey = "source.one",
            });
            fixture.Probe.Replies["HandleAsync"] = "{}";
            await fixture.Call("HandleAsync", request);
            var received = fixture.Probe.LastRequest;
            Assert.Equal("{opaque", received.GetProperty("payloadJson").GetString());
            Assert.False(received.TryGetProperty("originalPayloadHash", out _));
            var payload = received.GetProperty("payload");
            Assert.Empty(payload.GetProperty("deletedSessionIds").EnumerateArray());
            Assert.Equal("{opaque", payload.GetProperty("turns")[0].GetProperty("items")[0].GetProperty("argumentsJson").GetString());
            var invalid = JsonNode.Parse(request.GetRawText())!;
            invalid["payloadHash"] = "short";
            var calls = fixture.Probe.Calls;
            var rejected = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Call("HandleAsync", JsonSerializer.SerializeToElement(invalid)));
            Assert.Equal(SunderRpcErrorKind.Validation, rejected.Error.Kind);
            Assert.Equal(calls, fixture.Probe.Calls);
        }

        await using (var fixture = await CreateExtension(root, loadContext, assembly, "SystemPromptContributor", descriptors["sunder.agent.system.prompt.contributor"]))
        {
            var request = JsonSerializer.SerializeToElement(new
            {
                availableTools = Array.Empty<object>(), modelId = "model.one",
                profile = new { createdAtUtc = timestamp, displayName = "Profile", isInternal = false, profileId = "profile.one", updatedAtUtc = timestamp },
                providerId = "provider.one",
                runCapabilities = new { summary = "Capabilities", supportsAudioInput = false, supportsImageInput = false,
                    supportsMultipleToolCalls = false, supportsNativeToolCalling = false, supportsPdfInput = false,
                    supportsStreamingToolCalls = false, supportsVideoInput = false },
                runId = Guid.NewGuid(), runRevision = 1, runStartedAtUtc = timestamp,
                session = new { createdAtUtc = timestamp, sessionId, state = "active", title = "Session", updatedAtUtc = timestamp },
                turns = new[] { turn }, userMessage = "Hello 漢字",
            });
            fixture.Probe.Replies["ContributeAsync"] = """
                [{"blockId":"context","content":"Context 漢字","metadata":{"origin":"test"},"priority":0,"required":false,"title":"Context"}]
                """;
            var result = await fixture.Call("ContributeAsync", request);
            Assert.False(result[0].GetProperty("required").GetBoolean());
            Assert.Equal("test", result[0].GetProperty("metadata").GetProperty("origin").GetString());
            Assert.False(result[0].TryGetProperty("maxChars", out _));
            Assert.False(fixture.Probe.LastRequest.TryGetProperty("workspace", out _));
            Assert.False(fixture.Probe.LastRequest.GetProperty("runCapabilities").GetProperty("supportsNativeToolCalling").GetBoolean());
        }
    }
}
