using System.Collections;
using System.Reflection;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyAgentMemoryCleanup(Assembly owner, object store, RegressionTestRpcState state)
    {
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        var observer = CreateAgentLifecycle(owner, rpc, Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.DurableLifecycleObserver)).Providers));
        var cleaner = CreateAgentCleaner(owner, rpc, Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.SessionCleaner)).Providers));
        await using var observerLifetime = (IAsyncDisposable)observer;
        await using var cleanerLifetime = (IAsyncDisposable)cleaner;
        Assert.Equal("memory.semantic", observer.ObserverId); Assert.Equal("memory.semantic", cleaner.CleanerId);
        var session = Guid.NewGuid(); var other = Guid.NewGuid();
        var turnId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var turn = new AgentTurnRecord(turnId, session, AgentMessageRole.User, AgentTurnKind.Message,
            [new AgentTurnItemRecord(Guid.NewGuid(), turnId, 0, AgentTurnItemKind.Text, "Remember that I prefer cobalt summaries.", null, null, null, null, null, null, false, false, null, null)], now, now);
        var value = MemoryEnvelope("agent-consumer.memory", AgentLifecycleEventKind.UserTurnAdded, new()
        {
            Session = new(session, "profile.memory", "Memory profile", "Memory session", AgentSessionState.Active, null),
            Run = new(Guid.NewGuid(), 1, AgentRunStatus.Running, false, now), SessionId = session, RootSessionId = session, WorkspaceId = "workspace.memory",
            UserMessage = "Remember that I prefer cobalt summaries.", TriggerTurn = turn, Turns = [turn], RecentLiveBufferTurns = [turn],
        });
        await observer.HandleDurableLifecycleEventAsync(value);
        var memories = List(session); Assert.NotEmpty(memories);
        await observer.HandleDurableLifecycleEventAsync(value);
        Assert.Equal(memories.Length, List(session).Length);
        var receipt = new AgentMemoryConsistencyBarrier(value.EventId, value.PayloadHash);
        var hasReceipt = store.GetType().GetMethod("HasLifecycleInboxReceipt", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var localReceipt = System.Text.Json.JsonSerializer.SerializeToElement(receipt, MemoryProviderJson)
            .Deserialize(hasReceipt.GetParameters()[0].ParameterType, MemoryProviderJson);
        Assert.True((bool)hasReceipt.Invoke(store, [localReceipt])!);
        var upsert = store.GetType().GetMethod("UpsertMemory")!;
        var request = System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(new
        { sessionId = other, category = "fact", content = "Keep another session", normalizedContent = "keep another session", evidenceText = "evidence", isPinned = false, importance = 0.8f, confidence = 0.9f }),
            upsert.GetParameters()[0].ParameterType, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        upsert.Invoke(store, [request, null]);
        cleaner.DeleteSessionData(session); cleaner.DeleteSessionData(session);
        Assert.Empty(List(session)); Assert.Single(List(other));
        foreach (var memory in memories)
        {
            var id = memory.GetType().GetProperty("MemoryId")!.GetValue(memory);
            Assert.Empty((IEnumerable)store.GetType().GetMethod("ListEvidence")!.Invoke(store, [id])!);
        }

        object[] List(Guid id) => ((IEnumerable)store.GetType().GetMethod("ListMemories")!.Invoke(store, [id, null, true])!).Cast<object>().ToArray();
    }
}
