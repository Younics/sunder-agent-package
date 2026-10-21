using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.TestSupport;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

extern alias MemoryWorker;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Runtime;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Services;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static readonly string[] MemoryProviderSources = new[]
    {
        "ProfileCapabilityProvider.cs", "DurableLifecycleProvider.cs", "PromptContextProvider.cs", "SemanticProviderValues.cs", "MemoryProviderCatalog.cs",
    }.Select(file => "src/Sunder.Package.Agent.Memory.Semantic/Rpc/" + file).ToArray();

    private static readonly JsonSerializerOptions MemoryProviderJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new SunderRpcJsonConverterFactory(), new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) },
    };

    private static async Task VerifyMemoryProviders(string root, string consumer)
    {
        var directory = Path.Combine(root, "MemoryWorker");
        var selected = File.ReadAllBytes(Path.Combine(directory, "sunder-contracts.lock.json"));
        var baseline = CanonicalPackageRoles.ReadOwnedBaseline(directory);
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(CanonicalPackageRoles.Analysis(directory), "authoring-receipt.v1.json")));
        Assert.Equal(19, receipt.RootElement.GetProperty("bundles").GetArrayLength());
        Assert.Equal(5, receipt.RootElement.GetProperty("providers").GetArrayLength());
        Assert.Equal(18, receipt.RootElement.GetProperty("uses").GetArrayLength());
        Assert.Equal(4, receipt.RootElement.GetProperty("providerImports").GetArrayLength());
        Assert.Empty(receipt.RootElement.GetProperty("platformImports").EnumerateArray());
        var required = Assert.Single(receipt.RootElement.GetProperty("uses").EnumerateArray(), use => use.GetProperty("required").GetBoolean());
        Assert.Equal("younics.ai.agent.runtime.catalog", required.GetProperty("contractKey").GetString());
        var descriptors = receipt.RootElement.GetProperty("contracts").EnumerateArray().ToDictionary(
            contract => contract.GetProperty("contractId").GetString()!,
            contract => SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(contract.GetProperty("canonicalDescriptor").GetString()!)));
        Assert.Equal(CurrentPrivateRpcDescriptors.Get("younics.ai.agent.memory.semantic.inspector").GetCanonicalUtf8(), descriptors["younics.ai.agent.memory.semantic.inspector"].GetCanonicalUtf8());
        using var roles = new CanonicalPackageRoles();
        var providers = roles.Load("Memory.Semantic", "worker", directory);
        var app = roles.Load("Memory.Semantic", "app", directory);
        var load = new AssemblyLoadContext("memory-artifact-consumer", isCollectible: true);
        try
        {
            using var consumerStream = File.OpenRead(Path.Combine(consumer, "bin/Debug/net10.0/AgentConsumer.dll"));
            var client = load.LoadFromStream(consumerStream).GetType("Consumer", true)!;
            VerifyMemoryProviderApp(directory, app);
            var probes = await BuildRoleProbes(directory, providers, fixture: null,
                extraReferences: Directory.GetFiles(SingleProjectRoleOutput(directory, "worker"), "*.dll"),
                assemblyName: "Sunder.Package.Agent.Tests", output: SingleProjectRoleOutput(directory, "worker"), controlledComposition: true);
            await ExerciseMemoryProviders(providers, probes, client, descriptors, app);
            Assert.Equal(selected, File.ReadAllBytes(Path.Combine(directory, "sunder-contracts.lock.json")));
            Assert.Equal(baseline, CanonicalPackageRoles.ReadOwnedBaseline(directory));
        }
        finally { load.Unload(); }
    }

    private static async Task ExerciseMemoryProviders(Assembly assembly, Assembly probes, Type consumer, IReadOnlyDictionary<string, SunderRpcContractDescriptor> descriptors, Assembly app)
    {
        var inputJson = new JsonSerializerOptions(MemoryProviderJson) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };
        using var package = RegressionTestPackageScope.Create();
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var context = new TestPackageContext("younics.ai.agent.memory.semantic", package.RootPath, version: "2.0.0", storage: package.Context.Storage);
        var ownerRpc = new RegressionTestRpcClient(state, context.PackageId);
        Type Type(string name) => assembly.GetType("Sunder.Package.Agent.Memory.Semantic." + name, true)!;
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Memory.Semantic.Rpc.MemoryProviderCatalog"));
        var generated = CreateControlledGeneratedWorker(probes, context, ownerRpc, _ => { });
        await using var lifetime = generated.Lifetime;
        await ActivateGeneratedComposition(generated.Options);
        var feature = generated.Services.GetRequiredService(Type("MemorySemanticFeature"));
        var store = new MemoryProviderStore(generated.Services.GetRequiredService(Type("MemoryLocalStore")));
        var registrations = generated.Options.Providers;
        Assert.Equal(new[] { "semantic.memory.inspector", "semantic.memory.lifecycle", "semantic.memory.profile.capabilities", "semantic.memory.prompt.context", "semantic.memory.session.cleaner" },
            registrations.Select(value => value.ProviderId).Order(StringComparer.Ordinal));
        var transports = registrations.ToDictionary(registration => registration.ProviderId,
            registration => new SurfaceHandler(registration.Handler), StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            var transport = transports[registration.ProviderId];
            state.Add("younics.ai.agent.memory.semantic", descriptors[registration.ContractId], transport, transport, Noop.Instance, registration.ProviderId, "2.0.0");
        }
        var rpc = new RegressionTestRpcClient(state, "example.agent.consumer");
        await using var appServices = CreateGeneratedApp(app, context, ownerRpc);
        var appGateway = appServices.GetRequiredService(app.GetType("Sunder.Package.Agent.Memory.Semantic.MemoryAppRuntimeGateway", true)!);
        async Task<IReadOnlyList<StoredMemoryRecord>> AppMemories(Guid sessionId, bool includeInactive = false)
            => AgentRoleBoundary.As<IReadOnlyList<StoredMemoryRecord>>((await AgentRoleBoundary.Await(
                AgentRoleBoundary.Call(appGateway, "ListMemoriesAsync", sessionId, null, includeInactive, CancellationToken.None)!))!);
        var capabilities = consumer.GetMethod("DescribeCapabilitiesAsync")!.CreateDelegate<Func<ISunderRpcClient, CancellationToken, Task<string>>>();
        var handle = consumer.GetMethod("HandleLifecycleAsync")!.CreateDelegate<Func<ISunderRpcClient, string, CancellationToken, Task>>();
        var contribute = consumer.GetMethod("ContributeAsync")!.CreateDelegate<Func<ISunderRpcClient, string, CancellationToken, Task<string>>>();
        var acknowledge = consumer.GetMethod("AcknowledgeAsync")!.CreateDelegate<Func<ISunderRpcClient, string, CancellationToken, Task>>();
        using (var snapshot = JsonDocument.Parse(await capabilities(rpc, default)))
        {
            Assert.Equal(feature.GetType().GetProperty("ConsumerId")!.GetValue(feature), snapshot.RootElement.GetProperty("consumerId").GetString());
            Assert.Equal(AgentModelCapabilityKinds.Embedding, Assert.Single(snapshot.RootElement.GetProperty("capabilities").EnumerateArray()).GetProperty("capabilityKind").GetString());
        }

        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var session = new AgentSessionContextRecord(sessionId, "profile.memory", "Memory profile", "Memory session", AgentSessionState.Active, null);
        var run = new AgentRunContextRecord(Guid.NewGuid(), 1, AgentRunStatus.Running, false, now);
        var turn = new AgentTurnRecord(turnId, sessionId, AgentMessageRole.User, AgentTurnKind.Message,
            [new AgentTurnItemRecord(Guid.NewGuid(), turnId, 0, AgentTurnItemKind.Text, "Remember that I prefer cobalt summaries.", null, null, null, null, null, null, false, false, null, null)], now, now);
        var delivery = MemoryEnvelope("artifact-memory-user", AgentLifecycleEventKind.UserTurnAdded, new()
        {
            Session = session, Run = run, SessionId = sessionId, RootSessionId = sessionId, WorkspaceId = "workspace.memory",
            UserMessage = "Remember that I prefer cobalt summaries.", TriggerTurn = turn, Turns = [turn], RecentLiveBufferTurns = [turn],
        });
        var json = JsonSerializer.Serialize(delivery, inputJson);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handle(rpc, json, cancelled.Token));
            Assert.Empty(store.ListMemories(sessionId));
        }
        await handle(rpc, json, default);
        Assert.NotEmpty(store.ListMemories(sessionId));
        var lifecycleRequest = transports["semantic.memory.lifecycle"].Calls.Last(call => call.Method == "handle").Request;
        Assert.Equal(delivery.PayloadJson, lifecycleRequest.GetProperty("payloadJson").GetString());
        Assert.Equal(delivery.PayloadHash, lifecycleRequest.GetProperty("payloadHash").GetString());
        Assert.Equal(delivery.Payload.TriggerTurn!.Items[0].TextContent, lifecycleRequest.GetProperty("payload").GetProperty("triggerTurn")
            .GetProperty("items")[0].GetProperty("textContent").GetString());
        var count = store.ListMemories(sessionId).Count;
        Assert.Equal(count, (await AppMemories(sessionId)).Count);
        await handle(rpc, json, default);
        Assert.Equal(count, store.ListMemories(sessionId).Count);
        Assert.All(store.ListMemories(sessionId), memory => Assert.Single(store.ListEvidence(memory.MemoryId)));

        var prompt = JsonSerializer.SerializeToNode(new
        {
            session, run, turn = new AgentTurnContextRecord(session, run, "cobalt summaries", null), turns = new[] { turn }, recentLiveBufferTurns = Array.Empty<AgentTurnRecord>(),
            contextPlan = new { intent = "Preference", queryText = "cobalt summaries", reason = (string?)null, preferredCategories = (string[]?)null, maxEntryCount = 2, maxChars = 1000, shouldContribute = true },
            profile = (object?)null, workspace = (object?)null, executionBinding = (object?)null, executionTarget = (object?)null,
            availableTools = Array.Empty<AgentToolDescriptor>(), transcriptEpoch = 3,
            memoryConsistencyBarrier = new AgentMemoryConsistencyBarrier(delivery.EventId, delivery.PayloadHash),
        }, inputJson)!;
        using (var response = JsonDocument.Parse(await contribute(rpc, prompt.ToJsonString(), default)))
        {
            var block = Assert.Single(response.RootElement.GetProperty("blocks").EnumerateArray());
            Assert.Contains("cobalt", block.GetProperty("content").GetString()!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("durable-memory", block.GetProperty("provenance").GetString());
            Assert.Equal("untrusted", block.GetProperty("trust").GetString());
        }
        var promptRequest = transports["semantic.memory.prompt.context"].Calls.Last(call => call.Method == "contribute").Request;
        Assert.Equal(3, promptRequest.GetProperty("transcriptEpoch").GetInt32());
        Assert.Equal(delivery.EventId, promptRequest.GetProperty("memoryConsistencyBarrier").GetProperty("eventId").GetString());
        // No execution target was selected for this memory-only request, so the
        // generated boundary must preserve the absence of transient authority.
        Assert.Equal(JsonValueKind.Null, promptRequest.GetProperty("executionTarget").ValueKind);
        prompt["memoryConsistencyBarrier"]!["eventId"] = "missing-receipt";
        Assert.Equal("null", await contribute(rpc, prompt.ToJsonString(), default));
        prompt["memoryConsistencyBarrier"]!["eventId"] = delivery.EventId;
        prompt["contextPlan"]!["intent"] = "none";
        prompt["contextPlan"]!["shouldContribute"] = true;
        Assert.Equal("null", await contribute(rpc, prompt.ToJsonString(), default));
        promptRequest = transports["semantic.memory.prompt.context"].Calls.Last(call => call.Method == "contribute").Request;
        Assert.Equal("none", promptRequest.GetProperty("contextPlan").GetProperty("intent").GetString());
        await acknowledge(rpc, JsonSerializer.Serialize(new { sessionId, runId = run.RunId, transcriptEpoch = 3, blocks = Array.Empty<object>() }), default);

        var mismatch = delivery with { PayloadHash = new string('a', 64) };
        var fault = await Assert.ThrowsAsync<SunderRpcException>(() => handle(rpc, JsonSerializer.Serialize(mismatch, inputJson), default));
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, fault.Error.Kind);
        Assert.Equal(count, store.ListMemories(sessionId).Count);
        var rollback = MemoryEnvelope("artifact-memory-rollback", AgentLifecycleEventKind.TranscriptRolledBack,
            new() { SessionId = sessionId, RootSessionId = sessionId, WorkspaceId = "workspace.memory", DeletedTurnIds = [turnId], DeletedTurnCount = 1 });
        await handle(rpc, JsonSerializer.Serialize(rollback, inputJson), default);
        var pinned = Assert.Single(store.ListMemories(sessionId, includeInactive: true));
        Assert.True(pinned.IsPinned);
        Assert.Null(pinned.SourceTurnId);
        Assert.Empty(store.ListEvidence(pinned.MemoryId));
        var erased = MemoryEnvelope(delivery.EventId, delivery.Kind, new() { ContentErased = true }) with
        { OriginalPayloadHash = delivery.PayloadHash, SourceKey = delivery.SourceKey, OrderingKey = delivery.OrderingKey, Sequence = delivery.Sequence };
        await handle(rpc, JsonSerializer.Serialize(erased, inputJson), default);
        Assert.Equal(pinned.MemoryId, Assert.Single(store.ListMemories(sessionId, includeInactive: true)).MemoryId);
        var deletion = MemoryEnvelope("artifact-memory-deletion", AgentLifecycleEventKind.SessionDeleted,
            new() { SessionId = sessionId, RootSessionId = sessionId, WorkspaceId = "workspace.memory", DeletedSessionIds = [sessionId], DeletedSessionCount = 1 });
        await handle(rpc, JsonSerializer.Serialize(deletion, inputJson), default);
        Assert.Empty(store.ListMemories(sessionId, includeInactive: true));
        Assert.Empty(await AppMemories(sessionId, includeInactive: true));
        await state.RetireAsync(transports["semantic.memory.lifecycle"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handle(rpc, json, default));
    }

    private static AgentDurableLifecycleEventEnvelope MemoryEnvelope(string id, AgentLifecycleEventKind kind, AgentDurableLifecycleEventPayload payload)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new()
        {
            EventId = id, Kind = kind, Payload = payload, PayloadJson = json,
            PayloadHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))),
            Sequence = 1, SourceKey = "source:" + id, OrderingKey = "workspace:workspace.memory:root", OccurredAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private sealed class MemoryProviderStore(object store)
    {
        internal IReadOnlyList<StoredMemoryRecord> ListMemories(Guid sessionId, bool includeInactive = false)
            => AgentRoleBoundary.As<IReadOnlyList<StoredMemoryRecord>>(AgentRoleBoundary.Call(store, "ListMemories", sessionId, null, includeInactive)!);
        internal IReadOnlyList<StoredMemoryEvidenceRecord> ListEvidence(Guid memoryId)
            => AgentRoleBoundary.As<IReadOnlyList<StoredMemoryEvidenceRecord>>(AgentRoleBoundary.Call(store, "ListEvidence", memoryId)!);
    }

}
