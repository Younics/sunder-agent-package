using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

extern alias MemoryWorker;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Runtime;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Services;

public sealed class MemoryAppSourceMigrationTests
{
    [AgentPublicSourceMigrationFact]
    public async Task PresentationGateway_UsesGeneratedTransportAndRetainsObservationLifecycle()
    {
        using var source = PrivateManagementSourceFixture.Load("Memory.Semantic", RegressionTestRpcDescriptors.Get("sunder.agent.memory.semantic.inspector"));
        await Verify(source.Worker, source.App);
    }

    internal static async Task Verify(Assembly worker, Assembly app)
    {
        using var package = RegressionTestPackageScope.Create();
        await using var store = new MemoryLocalStore(package.Context);
        using var catalog = new RegressionTestExtensionCatalog();
        var settings = new MemorySemanticSettingsService(package.Context);
        using var resolver = new SemanticModelRuntimeResolver(catalog.Memory(), settings);
        var metrics = new SemanticMemoryMetricsService();
        var retrieval = new SemanticMemoryRetrievalBackend(store, resolver, settings);
        await using var indexing = new SemanticMemoryIndexingBackgroundService(store, resolver, settings, retrieval, metrics);
        using var inspector = new MemoryInspectorService(store, retrieval, indexing, resolver, metrics);
        var runtime = new MemoryRuntimeHandler(inspector);
        var operations = new InspectorOperations(runtime, failFirstStream: true);
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent.memory.semantic");
        await using var container = AgentSourceArtifactMigrationTests.CreateGeneratedApp(app, package.Context, rpc);
        var gatewayType = app.GetType("Sunder.Package.Agent.Memory.Semantic.MemoryAppRuntimeGateway", true)!;
        var instance = container.GetRequiredService(gatewayType);
        var gateway = AgentRoleBoundary.As<IMemoryInspectorGateway>(instance);
        var available = gatewayType.GetProperty("IsOwnerStreamAvailable")!;
        var observation = gatewayType.GetProperty("ObservationTask", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var transitions = new ConcurrentQueue<bool>();
        Action<bool> onAvailability = value => transitions.Enqueue(value);
        gatewayType.GetEvent("OwnerStreamAvailabilityChanged")!.AddEventHandler(instance, onAvailability);
        try
        {
            var missing = await Assert.ThrowsAsync<SunderRpcException>(() => gateway.ListSessionsAsync());
            Assert.Equal("semantic-memory.inspector-unavailable", missing.Error.Code);
            Assert.Equal(SunderRpcErrorKind.Unavailable, missing.Error.Kind);
            var foreign = new InspectorOperations(runtime, failFirstStream: false);
            _ = Add(foreign, "other.memory");
            var provider = Add(operations, "sunder.package.agent.memory.semantic");
            var sessionId = Guid.NewGuid();
            var memory = store.UpsertMemory(new(sessionId, "fact", "Original memory", "original memory", "Evidence", Guid.NewGuid(), false, 0.8f, 0.9f));
            var listed = Assert.Single(await gateway.ListMemoriesAsync(sessionId, "Original", includeInactive: true));
            Assert.Equal(memory.MemoryId, listed.MemoryId);
            Assert.Equal("Original", operations.LastQuery!.SearchText);
            Assert.True(operations.LastQuery.IncludeInactive);
            Assert.Equal(0, foreign.QueryCount);
            var updated = await gateway.UpdateMemoryAsync(memory.MemoryId, "preference", "Updated through generated App", "note");
            Assert.Equal("Updated through generated App", updated.Content);
            Assert.Equal(updated.Content, store.GetMemory(memory.MemoryId)!.Content);
            Assert.True((await gateway.SetPinnedAsync(memory.MemoryId, true)).IsPinned);
            Assert.NotEmpty(await gateway.ListEvidenceAsync(memory.MemoryId));
            Assert.Null(await gateway.GetSupersedingMemoryAsync(memory.MemoryId));
            Assert.Empty(await gateway.ListSupersededMemoriesAsync(memory.MemoryId));
            Assert.NotNull(await gateway.GetSemanticWorkerStatusAsync());
            Assert.NotNull(await gateway.GetMetricsSnapshotAsync());
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.SetPinnedAsync(memory.MemoryId, false, cancelled.Token));
                Assert.True(store.GetMemory(memory.MemoryId)!.IsPinned);
            }
            var declared = await Assert.ThrowsAsync<SunderRpcException>(() => gateway.UpdateMemoryAsync(Guid.NewGuid(), "fact", "missing memory", null));
            Assert.Equal("memory.semantic.operation-failed", declared.Error.Code);
            state.FailNextResponseContentRegistration(new IOException("private App content fault"));
            var fault = await Assert.ThrowsAsync<SunderRpcException>(() => gateway.ListMemoriesAsync(sessionId));
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, fault.Error.Kind);
            Assert.DoesNotContain("private App content fault", fault.Message, StringComparison.Ordinal);
            operations.Response = new(Memories: [memory with { Content = new string('m', 2 * 1024 * 1024) }]);
            Assert.Equal(2 * 1024 * 1024, Assert.Single(await gateway.ListMemoriesAsync(sessionId)).Content.Length);
            operations.Response = null;

            var resets = 0;
            var resnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gateway.ResetRequired += () => { if (Interlocked.Increment(ref resets) >= 2) resnapshot.TrySetResult(); };
            await gateway.InitializeAsync();
            await operations.SecondSubscription.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await resnapshot.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, operations.SubscriptionCount);
            Assert.Equal(1, operations.ClosedStreams);
            Assert.True((bool)available.GetValue(instance)!);
            Assert.Contains(false, transitions);
            var workerChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gateway.SemanticWorkerStatusChanged += () => workerChanged.TrySetResult();
            Assert.True(indexing.QueueMemoryIndex(memory.MemoryId, "profile.app"));
            await workerChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await state.RetireAsync(provider).WaitAsync(TimeSpan.FromSeconds(5));
            var unavailable = await Assert.ThrowsAsync<SunderRpcException>(() => gateway.ListSessionsAsync());
            Assert.Equal("semantic-memory.inspector-unavailable", unavailable.Error.Code);
            var replacement = new InspectorOperations(runtime, failFirstStream: false);
            _ = Add(replacement, "sunder.package.agent.memory.semantic");
            await replacement.FirstSubscription.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(await gateway.ListMemoriesAsync(sessionId));
            await ((IAsyncDisposable)gateway).DisposeAsync();
            Assert.True(((Task)observation.GetValue(instance)!).IsCompletedSuccessfully);
            Assert.False((bool)available.GetValue(instance)!);
            Assert.Equal(replacement.SubscriptionCount, replacement.ClosedStreams);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => gateway.InitializeAsync());
        }
        finally
        {
            gatewayType.GetEvent("OwnerStreamAvailabilityChanged")!.RemoveEventHandler(instance, onAvailability);
            await ((IAsyncDisposable)gateway).DisposeAsync();
        }

        object Add(InspectorOperations value, string packageId)
        {
            var type = worker.GetType("Sunder.Package.Agent.Memory.Semantic.Rpc.ManagementService", true)!;
            var service = AgentRoleBoundary.Create(type, value);
            var registration = Assert.IsType<SunderWorkerProviderRegistration>(type.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(service, null));
            state.Add(packageId, RegressionTestRpcDescriptors.Get("sunder.agent.memory.semantic.inspector"), service, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
            return service;
        }
    }

    private sealed class InspectorOperations(IMemoryInspectorRpcService inner, bool failFirstStream) : IMemoryInspectorRpcService
    {
        internal MemoryQuery? LastQuery { get; private set; }
        internal MemoryProjection? Response { get; set; }
        internal int QueryCount;
        internal int SubscriptionCount;
        internal int ClosedStreams;
        internal TaskCompletionSource FirstSubscription { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondSubscription { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<MemoryProjection> QueryAsync(SunderRpcInvocationContext context, MemoryQuery request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref QueryCount);
            LastQuery = request;
            return Response is { } response ? ValueTask.FromResult(response) : inner.QueryAsync(context, request, cancellationToken);
        }
        public ValueTask<MemoryProjection> CommandAsync(SunderRpcInvocationContext context, MemoryCommand request, CancellationToken cancellationToken = default)
            => inner.CommandAsync(context, request, cancellationToken);
        public async IAsyncEnumerable<MemoryChanged> ChangesAsync(SunderRpcInvocationContext context, MemoryChangeSubscription request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var number = Interlocked.Increment(ref SubscriptionCount);
            if (number == 1) FirstSubscription.TrySetResult();
            if (number == 2) SecondSubscription.TrySetResult();
            try
            {
                if (number == 1 && failFirstStream)
                {
                    yield return new(MemoryChangeKind.ResetRequired);
                    yield return new(MemoryChangeKind.Session); // Valid wire, malformed presentation event: no session ID.
                    yield break;
                }
                await foreach (var value in inner.ChangesAsync(context, request, cancellationToken).WithCancellation(cancellationToken)) yield return value;
            }
            finally { Interlocked.Increment(ref ClosedStreams); }
        }
    }

    private sealed class Noop : IDisposable { internal static Noop Instance { get; } = new(); public void Dispose() { } }
}
