using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyAgentCleanupDispatchers(Assembly owner, Assembly memory, Assembly probes)
    {
        using var package = RegressionTestPackageScope.Create();
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var cleaned = new ConcurrentQueue<Guid>();
        var cleaner = AddCleanupCleaner(state, "durable.cleaner", cleaned.Enqueue);
        cleaner.FailuresRemaining = 1;
        var observer = new CleanupObserver();
        var lifecycle = AddCleanupLifecycleProvider(state, observer);
        lifecycle.FailuresRemaining = 1;
        var rpc = new RegressionTestRpcClient(state, "younics.ai.agent");
        await using var receiver = new GeneratedServiceTestScope(rpc);
        var cleaners = receiver.Providers(owner.GetType("Sunder.Generated.Packages.Younics_Ai_Agent_Services.ISessionCleanerService", true)!);
        var observers = receiver.Providers(owner.GetType("Sunder.Generated.Packages.Younics_Ai_Agent_Services.IDurableLifecycleObserverService", true)!);
        var storeType = owner.GetType("Sunder.Package.Agent.Storage.AgentLocalStore", true)!;
        var store = Activator.CreateInstance(storeType, package.Context)!;
        await using var initialStoreLifetime = (IAsyncDisposable)store;
        var sessions = AgentRoleBoundary.Create(owner.GetType("Sunder.Package.Agent.Services.AgentSessionService", true)!, store, cleaners);
        var workspaces = AgentRoleBoundary.Create(owner.GetType("Sunder.Package.Agent.Services.AgentWorkspaceService", true)!, store,
            cleaners, sessions);
        var sessionId = await CreateAndDelete("First cleanup");
        var job = Assert.Single(Values(Call(store, "ListSessionCleanupJobs")));
        Assert.Equal("Pending", Property<string>(job, "Status")); Assert.Equal(1, Property<int>(job, "AttemptCount"));
        Assert.DoesNotContain("private-cleanup-failure", Property<string>(job, "LastError"), StringComparison.Ordinal);
        Assert.Equal(sessionId, Assert.Single(cleaned));
        var firstEvent = Assert.Single(Values(Call(store, "ListLifecycleOutboxEvents")));
        var envelope = (AgentDurableLifecycleEventEnvelope)Call(firstEvent, "ToEnvelope")!;
        Assert.Equal(AgentLifecycleEventKind.WorkspaceDeleted, envelope.Kind);
        Assert.Equal(sessionId, Assert.Single(envelope.Payload.DeletedSessionIds));

        // A fresh store/dispatcher retries the same ids-only job after a committed
        // deletion and a provider side effect followed by failure.
        await ((IAsyncDisposable)store).DisposeAsync();
        store = Activator.CreateInstance(storeType, package.Context)!;
        await using var storeLifetime = (IAsyncDisposable)store;
        var cleanup = AgentRoleBoundary.Create(owner.GetType("Sunder.Package.Agent.Services.AgentSessionCleanupDispatcher", true)!, store,
            cleaners);
        await using var cleanupLifetime = (IAsyncDisposable)cleanup;
        await WaitUntilDue(job);
        await Flush(cleanup);
        job = Assert.Single(Values(Call(store, "ListSessionCleanupJobs")));
        Assert.Equal("Completed", Property<string>(job, "Status"));
        Assert.Equal(new[] { sessionId, sessionId }, cleaned.ToArray());

        var dispatcher = AgentRoleBoundary.Create(owner.GetType("Sunder.Package.Agent.Services.AgentLifecycleDispatcher", true)!, store,
            observers, null);
        await using var dispatcherLifetime = (IAsyncDisposable)dispatcher;
        var subscription = (string)storeType.GetMethod("BuildLifecycleSubscriptionId", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, ["younics.ai.agent.memory.semantic", observer.ObserverId, "Durable"])!;
        await Flush(dispatcher);
        var delivery = Call(store, "GetLifecycleDeliveryState", subscription, envelope.Sequence)!;
        Assert.Equal("Pending", Property<string>(delivery, "Status")); Assert.Equal(1, Property<int>(delivery, "AttemptCount"));
        Assert.DoesNotContain("private-cleanup-failure", Property<string>(delivery, "LastError"), StringComparison.Ordinal);
        await WaitUntilDue(delivery);
        await Flush(dispatcher);
        Assert.Equal("Delivered", Property<string>(Call(store, "GetLifecycleDeliveryState", subscription, envelope.Sequence)!, "Status"));
        Assert.Equal(2, observer.Events.Count);
        Assert.All(observer.Events, item => { Assert.Equal(envelope.EventId, item.EventId); Assert.Equal(envelope.PayloadHash, item.PayloadHash); Assert.Equal(envelope.PayloadJson, item.PayloadJson); });

        sessions = AgentRoleBoundary.Create(owner.GetType("Sunder.Package.Agent.Services.AgentSessionService", true)!, store,
            cleaners);
        workspaces = AgentRoleBoundary.Create(owner.GetType("Sunder.Package.Agent.Services.AgentWorkspaceService", true)!, store,
            cleaners, sessions);
        var secondSession = await CreateAndDelete("Cancellation cleanup");
        var second = Events().Single(item => item.Payload.DeletedSessionIds.Contains(secondSession));
        lifecycle.Gate = new();
        using (var cancelled = new CancellationTokenSource())
        {
            var pending = Flush(dispatcher, cancelled.Token);
            await lifecycle.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        delivery = Call(store, "GetLifecycleDeliveryState", subscription, second.Sequence)!;
        Assert.Equal("Pending", Property<string>(delivery, "Status")); Assert.Equal(0, Property<int>(delivery, "AttemptCount"));
        lifecycle.Gate = new();
        var retiring = Flush(dispatcher);
        await lifecycle.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RetireAsync(lifecycle).WaitAsync(TimeSpan.FromSeconds(5));
        await retiring.WaitAsync(TimeSpan.FromSeconds(5));
        delivery = Call(store, "GetLifecycleDeliveryState", subscription, second.Sequence)!;
        Assert.Equal("Pending", Property<string>(delivery, "Status")); Assert.Equal(0, Property<int>(delivery, "AttemptCount"));
        var replacement = new CleanupObserver(); AddCleanupLifecycleProvider(state, replacement);
        await Flush(dispatcher);
        Assert.Equal("Delivered", Property<string>(Call(store, "GetLifecycleDeliveryState", subscription, second.Sequence)!, "Status"));
        Assert.Equal(second.EventId, Assert.Single(replacement.Events).EventId);
        Assert.All(Values(Call(store, "ListSessionCleanupJobs")), item => Assert.Equal("Completed", Property<string>(item, "Status")));

        async Task<Guid> CreateAndDelete(string name)
        {
            var workspace = (AgentWorkspaceRecord)Call(workspaces, "CreateWorkspace", name)!;
            var session = (AgentSessionRecord)Call(sessions, "CreateSession", name, null, null, null, null, null, null, "profile.cleanup", null, null, workspace.WorkspaceId)!;
            await ((Task)Call(workspaces, "DeleteWorkspaceAsync", workspace.WorkspaceId, CancellationToken.None)!).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(Call(sessions, "GetSession", session.SessionId));
            return session.SessionId;
        }
        AgentDurableLifecycleEventEnvelope[] Events() => Values(Call(store, "ListLifecycleOutboxEvents"))
            .Select(item => (AgentDurableLifecycleEventEnvelope)Call(item, "ToEnvelope")!).ToArray();
        static Task Flush(object value, CancellationToken token = default) => ((Task)Call(value, "FlushAsync", token)!).WaitAsync(TimeSpan.FromSeconds(10));
        static object? Call(object value, string name, params object?[] args)
            => AgentRoleBoundary.AssertionValue(AgentRoleBoundary.Call(value, name, args));
        static object[] Values(object? value) => ((IEnumerable)value!).Cast<object>().ToArray();
        static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;
        static async Task WaitUntilDue(object value)
        {
            var delay = Property<DateTimeOffset>(value, "NextAttemptAtUtc") - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(20);
            Assert.True(delay < TimeSpan.FromSeconds(5));
            if (delay > TimeSpan.Zero) await Task.Delay(delay);
        }
    }
}
