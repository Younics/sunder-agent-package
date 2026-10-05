using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static IAgentSessionDataCleaner CreateAgentCleaner(Assembly owner, ISunderRpcClient rpc, SunderRpcProviderSnapshot provider)
        => AgentRoleBoundary.As<IAgentSessionDataCleaner>(Activator.CreateInstance(owner.GetType("Sunder.Package.Agent.Runtime.Rpc.GeneratedSessionCleanerClient", true)!, rpc, provider)!);

    private static IAgentDurableLifecycleObserver CreateAgentLifecycle(Assembly owner, ISunderRpcClient rpc, SunderRpcProviderSnapshot provider)
        => AgentRoleBoundary.As<IAgentDurableLifecycleObserver>(Activator.CreateInstance(owner.GetType("Sunder.Package.Agent.Runtime.Rpc.GeneratedDurableLifecycleClient", true)!, rpc, provider)!);

    internal static async Task VerifyAgentCleanupConsumers(Assembly owner, Assembly memory, Assembly probes)
    {
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var peer = new CleanupObserver();
        var cleaned = new ConcurrentQueue<Guid>();
        var cleanerHandler = AddCleanupProvider(memory, state, "SessionCleanerProvider", ["fixture.cleaner", (Action<Guid>)cleaned.Enqueue]);
        var lifecycleHandler = AddCleanupLifecycleProvider(memory, probes, state, peer);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        var cleanerSnapshot = Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.SessionCleaner)).Providers);
        var lifecycleSnapshot = Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.DurableLifecycleObserver)).Providers);
        var observed = new FilesRpcObserver(rpc, "handle");
        var cleaner = CreateAgentCleaner(owner, observed, cleanerSnapshot);
        var observer = CreateAgentLifecycle(owner, observed, lifecycleSnapshot);
        await using var cleanerLifetime = (IAsyncDisposable)cleaner;
        await using var observerLifetime = (IAsyncDisposable)observer;
        Assert.Equal("fixture.cleaner", cleaner.CleanerId);
        Assert.Equal(peer.ObserverId, observer.ObserverId); Assert.Equal(peer.DisplayName, observer.DisplayName);
        var session = Guid.NewGuid(); cleaner.DeleteSessionData(session); Assert.Equal(session, Assert.Single(cleaned));
        var envelope = MemoryEnvelope("cleanup.mapping", AgentLifecycleEventKind.TranscriptRolledBack, new()
        {
            SessionId = session, RootSessionId = session, WorkspaceId = "workspace.cleanup", WorkspaceIncarnationId = "incarnation.one",
            DeletedTurnIds = [Guid.NewGuid()], DeletedTurnCount = 1, DeletedChildSessionIds = [Guid.NewGuid()], DeletedChildSessionCount = 1,
            DeletionBatchId = "batch.one", DeletionChunkIndex = 0, DeletionChunkCount = 1,
            DeletionChunkReceipts = [new("receipt.one", new string('a', 64))],
        }) with { OriginalPayloadHash = new string('b', 64) };
        await observer.HandleDurableLifecycleEventAsync(envelope);
        var received = Assert.Single(peer.Events);
        Assert.Equal(envelope.PayloadJson, received.PayloadJson); Assert.Equal(envelope.PayloadHash, received.PayloadHash);
        Assert.Equal(envelope.OriginalPayloadHash, received.OriginalPayloadHash); Assert.Equal(envelope.Kind, received.Kind);
        Assert.Equal(envelope.OccurredAtUtc, received.OccurredAtUtc);
        Assert.Equal(envelope.Payload.DeletedTurnIds, received.Payload.DeletedTurnIds);
        Assert.Equal(envelope.Payload.DeletionChunkReceipts, received.Payload.DeletionChunkReceipts);
        foreach (var change in new Func<SunderRpcProviderSnapshot, SunderRpcProviderSnapshot>[]
        {
            value => value with { PackageId = "other.package" }, value => value with { PackageVersion = "2.0.1" },
            value => value with { ProviderId = "other.provider" }, value => value with { ContractId = "other.contract" },
            value => value with { ContractVersion = "1.0.1" }, value => value with { DescriptorSha256 = new string('c', 64) },
            value => value with { AbiSha256 = new string('d', 64) }, value => value with { ActivationId = Guid.NewGuid() },
            value => value with { ActivationEpoch = value.ActivationEpoch + 1 }, value => value with { SessionGeneration = value.SessionGeneration + 1 },
            value => value with { Endpoint = new("other-endpoint") }, value => value with { State = SunderRpcProviderState.Inactive },
        })
        {
            observed.Transform = change;
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, Assert.Throws<SunderRpcException>(() => cleaner.DeleteSessionData(session)).Error.Kind);
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => observer.HandleDurableLifecycleEventAsync(envelope).AsTask())).Error.Kind);
            Assert.Single(cleaned); Assert.Single(peer.Events);
        }
        observed.Transform = null;
        observed.InvalidateAfterInvoke = true;
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => observer.HandleDurableLifecycleEventAsync(envelope).AsTask())).Error.Kind);
        observed.Transform = null; observed.InvalidateAfterInvoke = false;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); var count = peer.Events.Count;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observer.HandleDurableLifecycleEventAsync(envelope, cancelled.Token).AsTask());
            Assert.Equal(count, peer.Events.Count);
        }
        lifecycleHandler.Gate = new();
        var pending = observer.HandleDurableLifecycleEventAsync(envelope).AsTask();
        await lifecycleHandler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RetireAsync(lifecycleHandler).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => pending)).Error.Kind);
        await lifecycleHandler.Gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var replacement = new CleanupObserver(); AddCleanupLifecycleProvider(memory, probes, state, replacement);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => observer.HandleDurableLifecycleEventAsync(envelope).AsTask())).Error.Kind);
        var next = CreateAgentLifecycle(owner, observed, Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.DurableLifecycleObserver)).Providers));
        await using var nextLifetime = (IAsyncDisposable)next;
        await next.HandleDurableLifecycleEventAsync(envelope); Assert.Single(replacement.Events);

        cleanerHandler.Gate = new();
        var delete = Task.Run(() => cleaner.DeleteSessionData(session));
        await cleanerHandler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = cleanerLifetime.DisposeAsync().AsTask();
        Assert.Same(stop, cleanerLifetime.DisposeAsync().AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delete);
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(cleaned);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, Assert.Throws<SunderRpcException>(() => cleaner.CleanerId).Error.Kind);
        Assert.Equal(0, observed.UnscopedLookups); Assert.All(observed.Scopes, scope => Assert.True(scope.Disposed));
        await VerifyEnteredCleanupDrain(owner, memory);
    }

    private static async Task VerifyEnteredCleanupDrain(Assembly owner, Assembly memory)
    {
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var completed = 0;
        var handler = AddCleanupProvider(memory, state, "SessionCleanerProvider", ["drained.cleaner", (Action<Guid>)(_ =>
        { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Cleanup release was not signalled."); Interlocked.Increment(ref completed); })]);
        var rpc = new FilesRpcObserver(new RegressionTestRpcClient(state, "sunder.package.agent"));
        var cleaner = CreateAgentCleaner(owner, rpc, Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.SessionCleaner)).Providers));
        await using var lifetime = (IAsyncDisposable)cleaner;
        var call = Task.Run(() => cleaner.DeleteSessionData(Guid.NewGuid()));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var stop = lifetime.DisposeAsync().AsTask();
            Assert.False(stop.IsCompleted); Assert.Equal(0, completed);
            // Retirement uses CancelAsync: observe cancellation at the entered
            // call before releasing its non-cooperative synchronous cleanup.
            await handler.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stop.IsCompleted); Assert.Equal(0, completed);
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, completed); Assert.All(rpc.Scopes, scope => Assert.True(scope.Disposed));
        }
        finally { release.Set(); await call.ContinueWith(_ => { }, TaskScheduler.Default); }
    }

    private static CleanupHandlerProbe AddCleanupProvider(Assembly memory, RegressionTestRpcState state, string name, object[] arguments)
    {
        var type = memory.GetType("Sunder.Package.Agent.Memory.Semantic.Rpc." + name, true)!;
        var service = Activator.CreateInstance(type, arguments)!;
        var registration = (SunderWorkerProviderRegistration)type.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(service, null)!;
        var probe = new CleanupHandlerProbe(registration.Handler);
        state.Add("sunder.package.agent.memory.semantic", RegressionTestRpcDescriptors.Get(registration.ContractId), probe, probe, Noop.Instance, registration.ProviderId, "2.0.0");
        return probe;
    }

    private static CleanupHandlerProbe AddCleanupLifecycleProvider(Assembly memory, Assembly probes, RegressionTestRpcState state, CleanupObserver observer)
    {
        Func<string, CancellationToken, ValueTask> handle = (value, token) => observer.HandleDurableLifecycleEventAsync(
            JsonSerializer.Deserialize<AgentDurableLifecycleEventEnvelope>(value, MemoryProviderJson)!, token);
        var local = Activator.CreateInstance(probes.GetType("RoleLocalLifecycleProbe", true)!, observer.ObserverId, observer.DisplayName, handle)!;
        return AddCleanupProvider(memory, state, "DurableLifecycleProvider", [local]);
    }

    private sealed class CleanupObserver : IAgentDurableLifecycleObserver
    {
        public string ObserverId => "fixture.lifecycle";
        public string DisplayName => "Fixture Lifecycle";
        internal ConcurrentQueue<AgentDurableLifecycleEventEnvelope> Events { get; } = new();
        public ValueTask HandleDurableLifecycleEventAsync(AgentDurableLifecycleEventEnvelope value, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Events.Enqueue(value); return ValueTask.CompletedTask; }
    }

    private sealed class CleanupHandlerProbe(ISunderRpcServiceHandler inner) : ISunderRpcServiceHandler, ISunderRpcDeclaredErrorMetadata
    {
        internal int FailuresRemaining;
        internal FilesCallGate? Gate;
        internal TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> DeclaredErrors => ((ISunderRpcDeclaredErrorMetadata)inner).DeclaredErrors;
        public async ValueTask<JsonElement> InvokeUnaryAsync(SunderRpcInvocationContext context, string serviceId, string methodId, JsonElement request, CancellationToken token = default)
        {
            using var cancellation = token.Register(() => CancellationObserved.TrySetResult());
            if (methodId != "describe" && Gate is { } gate)
            {
                gate.Started.TrySetResult();
                try { await gate.Release.Task.WaitAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { gate.Cancelled.TrySetResult(); throw; }
            }
            var value = await inner.InvokeUnaryAsync(context, serviceId, methodId, request, token);
            if (methodId != "describe" && FailuresRemaining-- > 0) throw new InvalidOperationException("private-cleanup-failure");
            return value;
        }
        public IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(SunderRpcInvocationContext context, string serviceId, string methodId, JsonElement request, CancellationToken token = default)
            => inner.InvokeServerStreamAsync(context, serviceId, methodId, request, token);
    }
}
