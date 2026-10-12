using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static async Task VerifyAgentCleanupConsumers(Assembly owner, Assembly memory, Assembly probes)
    {
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var peer = new CleanupObserver();
        var cleaned = new ConcurrentQueue<Guid>();
        var cleanerHandler = AddCleanupCleaner(state, "fixture.cleaner", cleaned.Enqueue);
        var lifecycleHandler = AddCleanupLifecycleProvider(state, peer);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        var observed = new FilesRpcObserver(rpc, "handle");
        var primary = await SelectAsync(observed);
        await using var primaryScope = primary.Scope;
        var cleaner = primary.Cleaner;
        var observer = primary.Observer;
        Assert.Equal("fixture.cleaner", (await cleaner.DescribeAsync()).Id);
        var identity = await observer.DescribeAsync();
        Assert.Equal(peer.ObserverId, identity.Id); Assert.Equal(peer.DisplayName, identity.DisplayName);
        var session = Guid.NewGuid(); await cleaner.DeleteSessionAsync(session); Assert.Equal(session, Assert.Single(cleaned));
        var envelope = MemoryEnvelope("cleanup.mapping", AgentLifecycleEventKind.TranscriptRolledBack, new()
        {
            SessionId = session, RootSessionId = session, WorkspaceId = "workspace.cleanup", WorkspaceIncarnationId = "incarnation.one",
            DeletedTurnIds = [Guid.NewGuid()], DeletedTurnCount = 1, DeletedChildSessionIds = [Guid.NewGuid()], DeletedChildSessionCount = 1,
            DeletionBatchId = "batch.one", DeletionChunkIndex = 0, DeletionChunkCount = 1,
            DeletionChunkReceipts = [new("receipt.one", new string('a', 64))],
        }) with { OriginalPayloadHash = new string('b', 64) };
        await observer.HandleAsync(envelope);
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
            var fresh = await SelectAsync(observed);
            try
            {
                Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => fresh.Cleaner.DeleteSessionAsync(session).AsTask())).Error.Kind);
                Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => fresh.Observer.HandleAsync(envelope).AsTask())).Error.Kind);
            }
            finally { await fresh.Scope.DisposeAsync(); }
            Assert.Single(cleaned); Assert.Single(peer.Events);
        }
        observed.Transform = null;
        observed.InvalidateAfterInvoke = true;
        var invalidated = await SelectAsync(observed);
        try
        {
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => invalidated.Observer.HandleAsync(envelope).AsTask())).Error.Kind);
        }
        finally { await invalidated.Scope.DisposeAsync(); }
        observed.Transform = null; observed.InvalidateAfterInvoke = false;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); var count = peer.Events.Count;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observer.HandleAsync(envelope, cancelled.Token).AsTask());
            Assert.Equal(count, peer.Events.Count);
        }
        lifecycleHandler.Gate = new();
        var pending = observer.HandleAsync(envelope).AsTask();
        await lifecycleHandler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RetireAsync(lifecycleHandler).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => pending)).Error.Kind);
        await lifecycleHandler.Gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var replacement = new CleanupObserver(); var replacementHandler = AddCleanupLifecycleProvider(state, replacement);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => observer.HandleAsync(envelope).AsTask())).Error.Kind);
        var next = await SelectAsync(observed);
        await using var nextScope = next.Scope;
        await next.Observer.HandleAsync(envelope); Assert.Single(replacement.Events);

        cleanerHandler.Gate = new();
        var delete = cleaner.DeleteSessionAsync(session).AsTask();
        await cleanerHandler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = primary.Scope.DisposeAsync().AsTask();
        Assert.Same(stop, primary.Scope.DisposeAsync().AsTask());
        AssertCleanupRetired((await Assert.ThrowsAsync<SunderRpcException>(() => delete)).Error);
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(cleaned);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => cleaner.DescribeAsync().AsTask());
        replacementHandler.Gate = new();
        var stopping = next.Observer.HandleAsync(envelope).AsTask();
        await replacementHandler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = next.Scope.DisposeAsync().AsTask();
        var retired = await Assert.ThrowsAsync<SunderRpcException>(() => stopping);
        AssertCleanupRetired(retired.Error);
        await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => next.Observer.DescribeAsync().AsTask());
        observed.AssertScopedOperations(); Assert.All(observed.Scopes, scope => Assert.True(scope.Disposed));
        await VerifyEnteredCleanupDrain();
    }

    private static async Task VerifyEnteredCleanupDrain()
    {
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var completed = 0;
        var handler = AddCleanupCleaner(state, "drained.cleaner", _ =>
        { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Cleanup release was not signalled."); Interlocked.Increment(ref completed); });
        var rpc = new FilesRpcObserver(new RegressionTestRpcClient(state, "sunder.package.agent"));
        var receiver = await SelectCleanerAsync(rpc);
        await using var lifetime = receiver.Scope;
        var cleaner = receiver.Cleaner;
        var call = Task.Run(() => cleaner.DeleteSessionAsync(Guid.NewGuid()).AsTask());
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var stop = receiver.Scope.DisposeAsync().AsTask();
            Assert.False(stop.IsCompleted); Assert.Equal(0, completed);
            // Retirement uses CancelAsync: observe cancellation at the entered
            // call before releasing its non-cooperative synchronous cleanup.
            await handler.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stop.IsCompleted); Assert.Equal(0, completed);
            release.Set();
            AssertCleanupRetired((await Assert.ThrowsAsync<SunderRpcException>(() => call)).Error);
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, completed); Assert.All(rpc.Scopes, scope => Assert.True(scope.Disposed));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => cleaner.DescribeAsync().AsTask());
        }
        finally { release.Set(); await call.ContinueWith(_ => { }, TaskScheduler.Default); }
    }

    private static void AssertCleanupRetired(SunderRpcError error)
    {
        Assert.Equal(SunderRpcErrorKind.Unavailable, error.Kind);
        Assert.Equal("rpc.binding.retired", error.Code);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, error.RetrySafety);
    }

    private static CleanupHandlerProbe AddCleanupCleaner(RegressionTestRpcState state, string cleanerId, Action<Guid> delete)
        => (CleanupHandlerProbe)AddGeneratedPeer(state, "sunder.package.agent.memory.semantic",
            RegressionTestBindings.SessionCleaners, new RegressionTestSessionCleanerProvider(new CleanupCleaner(cleanerId, delete)),
            cleanerId, inner => new CleanupHandlerProbe(inner));

    private static CleanupHandlerProbe AddCleanupLifecycleProvider(RegressionTestRpcState state, CleanupObserver observer)
        => (CleanupHandlerProbe)AddGeneratedPeer(state, "sunder.package.agent.memory.semantic",
            RegressionTestBindings.DurableLifecycleObservers, new RegressionTestDurableLifecycleProvider(observer),
            "semantic.memory.lifecycle", inner => new CleanupHandlerProbe(inner));

    private static async Task<(GeneratedServiceTestScope Scope, AgentServices.ISessionCleanerService Cleaner,
        AgentServices.IDurableLifecycleObserverService Observer)> SelectAsync(ISunderRpcClient rpc)
    {
        var scope = new GeneratedServiceTestScope(rpc);
        try
        {
            var cleaner = Assert.Single((await scope.Providers<AgentServices.ISessionCleanerService>().ListAsync()).Providers).Service;
            var observer = Assert.Single((await scope.Providers<AgentServices.IDurableLifecycleObserverService>().ListAsync()).Providers).Service;
            return (scope, cleaner, observer);
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    private static async Task<(GeneratedServiceTestScope Scope, AgentServices.ISessionCleanerService Cleaner)> SelectCleanerAsync(ISunderRpcClient rpc)
    {
        var scope = new GeneratedServiceTestScope(rpc);
        try
        {
            var cleaner = Assert.Single((await scope.Providers<AgentServices.ISessionCleanerService>().ListAsync()).Providers).Service;
            return (scope, cleaner);
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    private sealed class CleanupCleaner(string cleanerId, Action<Guid> delete) : IAgentSessionDataCleaner
    {
        public string CleanerId => cleanerId;
        public void DeleteSessionData(Guid sessionId) => delete(sessionId);
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
