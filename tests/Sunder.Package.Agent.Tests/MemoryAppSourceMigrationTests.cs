using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

extern alias MemoryApp;
extern alias MemoryWorker;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Runtime;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Services;
using MemoryAppGateway = MemoryApp::Sunder.Package.Agent.Memory.Semantic.Runtime.IMemoryInspectorGateway;
using MemoryAppServices = MemoryApp::Sunder.Generated.Packages.Younics_Ai_Agent_Memory_Semantic_Services;
using AppMemoryChanged = MemoryApp::Sunder.Package.Agent.Memory.Semantic.Runtime.MemoryChanged;
using AppMemoryChangeKind = MemoryApp::Sunder.Package.Agent.Memory.Semantic.Runtime.MemoryChangeKind;
using AppSemanticEmbeddingContext = MemoryApp::Sunder.Package.Agent.Memory.Semantic.Services.SemanticEmbeddingContext;

public sealed class MemoryAppSourceMigrationTests
{
    [Fact]
    public async Task Gateway_MalformedChangeRetriesResnapshotsAndDrainsOnDisposal()
    {
        var service = new RetryingMemoryInspectorService();
        var gatewayType = typeof(MemoryAppGateway).Assembly.GetType(
            "Sunder.Package.Agent.Memory.Semantic.MemoryAppRuntimeGateway", true)!;
        var instance = Activator.CreateInstance(gatewayType, BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [service], culture: null)!;
        var gateway = AgentRoleBoundary.As<MemoryAppGateway>(instance);
        var observation = gatewayType.GetProperty("ObservationTask", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var resetCount = 0;
        var resnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.ResetRequired += () =>
        {
            if (Interlocked.Increment(ref resetCount) == 2) resnapshot.TrySetResult();
        };

        await gateway.InitializeAsync();
        await service.SecondSubscription.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await resnapshot.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, service.SubscriptionCount);
        Assert.Equal(1, service.ClosedStreamCount);

        await ((IAsyncDisposable)gateway).DisposeAsync();

        Assert.Equal(2, service.ClosedStreamCount);
        Assert.True(((Task)observation.GetValue(instance)!).IsCompletedSuccessfully);
    }

    [AgentPublicSourceMigrationFact]
    public async Task PresentationGateway_UsesGeneratedTransportAndRetainsObservationLifecycle()
    {
        using var source = PrivateManagementSourceFixture.Load("Memory.Semantic", CurrentPrivateRpcDescriptors.Get("younics.ai.agent.memory.semantic.inspector"));
        await Verify(source.Worker, source.App);
    }

    internal static async Task Verify(Assembly worker, Assembly app)
    {
        using var package = RegressionTestPackageScope.Create();
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "younics.ai.agent.memory.semantic");
        var (options, workerLifetime, workerServices) = AgentSourceArtifactMigrationTests.CreateGeneratedWorker(worker, package.Context, rpc);
        await using var workerOwner = workerLifetime;
        await AgentSourceArtifactMigrationTests.ActivateGeneratedComposition(options);
        var registration = Assert.Single(options.Providers, value => value.ProviderId == "semantic.memory.inspector");
        await using var container = AgentSourceArtifactMigrationTests.CreateGeneratedApp(app, package.Context, rpc);
        var gatewayType = app.GetType("Sunder.Package.Agent.Memory.Semantic.MemoryAppRuntimeGateway", true)!;
        var instance = container.GetRequiredService(gatewayType);
        var gateway = AgentRoleBoundary.As<MemoryAppGateway>(instance);
        var available = gatewayType.GetProperty("IsOwnerStreamAvailable")!;
        var observation = gatewayType.GetProperty("ObservationTask", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var transitions = new ConcurrentQueue<bool>();
        var unavailableTransition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<bool> onAvailability = transitions.Enqueue;
        Action<bool> onUnavailable = available =>
        {
            if (!available) unavailableTransition.TrySetResult();
        };
        gatewayType.GetEvent("OwnerStreamAvailabilityChanged")!.AddEventHandler(instance, onAvailability);
        gatewayType.GetEvent("OwnerStreamAvailabilityChanged")!.AddEventHandler(instance, onUnavailable);
        try
        {
            var missing = await Assert.ThrowsAsync<SunderRpcException>(() => gateway.ListSessionsAsync());
            Assert.Equal("semantic-memory.inspector-unavailable", missing.Error.Code);
            Assert.Equal(SunderRpcErrorKind.Unavailable, missing.Error.Kind);
            using var foreignPackage = RegressionTestPackageScope.Create();
            var foreignContext = new TestPackageContext("other.memory", foreignPackage.RootPath, storage: foreignPackage.Context.Storage);
            var (foreignOptions, foreignLifetime, _) = AgentSourceArtifactMigrationTests.CreateGeneratedWorker(worker, foreignContext, rpc);
            await using var foreignOwner = foreignLifetime;
            var foreign = Assert.Single(foreignOptions.Providers, value => value.ProviderId == "semantic.memory.inspector");
            state.Add("other.memory", CurrentPrivateRpcDescriptors.Get("younics.ai.agent.memory.semantic.inspector"),
                foreign.Handler, foreign.Handler, Noop.Instance, foreign.ProviderId, "2.0.0");
            state.Add("younics.ai.agent.memory.semantic", CurrentPrivateRpcDescriptors.Get("younics.ai.agent.memory.semantic.inspector"),
                registration.Handler, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
            await using var store = new MemoryLocalStore(package.Context);
            var sessionId = Guid.NewGuid();
            var memory = store.UpsertMemory(new(sessionId, "fact", "Original memory", "original memory", "Evidence", Guid.NewGuid(), false, 0.8f, 0.9f));
            var listed = Assert.Single(await gateway.ListMemoriesAsync(sessionId, "Original", includeInactive: true));
            Assert.Equal(memory.MemoryId, listed.MemoryId);
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
            var largeContent = "00000:" + new string('m', 4_090);
            var largeMemory = store.UpsertMemory(new(sessionId, "fact", largeContent, largeContent, "Evidence", Guid.NewGuid(), false, 0.8f, 0.9f));
            for (var index = 1; index < 512; index++)
            {
                var content = index.ToString("D5") + ":" + new string('m', 4_090);
                store.UpsertMemory(new(sessionId, "fact", content, content, "Evidence", Guid.NewGuid(), false, 0.8f, 0.9f));
            }
            var acceptedProjection = await gateway.ListMemoriesAsync(sessionId, includeInactive: true);
            Assert.True(acceptedProjection.Sum(value => value.Content.Length) >= 2 * 1024 * 1024);
            Assert.Equal(largeContent.Length, acceptedProjection.Single(value => value.MemoryId == largeMemory.MemoryId).Content.Length);

            var resets = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gateway.ResetRequired += () => resets.TrySetResult();
            await gateway.InitializeAsync();
            await resets.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True((bool)available.GetValue(instance)!);
            Assert.Contains(true, transitions);
            var workerChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gateway.SemanticWorkerStatusChanged += () => workerChanged.TrySetResult();
            var indexing = workerServices.GetRequiredService(worker.GetType(
                "Sunder.Package.Agent.Memory.Semantic.Services.SemanticMemoryIndexingBackgroundService", true)!);
            Assert.True((bool)AgentRoleBoundary.Call(indexing, "QueueMemoryIndex", memory.MemoryId, "profile.app")!);
            await workerChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await state.RetireAsync(registration.Handler).WaitAsync(TimeSpan.FromSeconds(5));
            var unavailable = await Assert.ThrowsAsync<SunderRpcException>(() => gateway.ListSessionsAsync());
            Assert.Equal("semantic-memory.inspector-unavailable", unavailable.Error.Code);
            await unavailableTransition.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(false, transitions);
            var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gateway.ResetRequired += () => reconnected.TrySetResult();
            state.Add("younics.ai.agent.memory.semantic", CurrentPrivateRpcDescriptors.Get("younics.ai.agent.memory.semantic.inspector"),
                registration.Handler, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
            await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(acceptedProjection.Count, (await gateway.ListMemoriesAsync(sessionId, includeInactive: true)).Count);
            await ((IAsyncDisposable)gateway).DisposeAsync();
            Assert.True(((Task)observation.GetValue(instance)!).IsCompletedSuccessfully);
            Assert.False((bool)available.GetValue(instance)!);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => gateway.InitializeAsync());
        }
        finally
        {
            gatewayType.GetEvent("OwnerStreamAvailabilityChanged")!.RemoveEventHandler(instance, onAvailability);
            gatewayType.GetEvent("OwnerStreamAvailabilityChanged")!.RemoveEventHandler(instance, onUnavailable);
            await ((IAsyncDisposable)gateway).DisposeAsync();
        }
    }

    private sealed class Noop : IDisposable { internal static Noop Instance { get; } = new(); public void Dispose() { } }

    private sealed class RetryingMemoryInspectorService : MemoryAppServices.IMemoryInspectorService
    {
        private int _subscriptionCount;
        private int _closedStreamCount;

        internal int SubscriptionCount => Volatile.Read(ref _subscriptionCount);
        internal int ClosedStreamCount => Volatile.Read(ref _closedStreamCount);
        internal TaskCompletionSource SecondSubscription { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IAsyncEnumerable<AppMemoryChanged> ChangesAsync(CancellationToken token = default)
            => Changes(token);

        public ValueTask<SunderContent> SessionsAsync(CancellationToken token = default) => Unsupported();
        public ValueTask<SunderContent> SessionCheckpointAsync(Guid sessionId, CancellationToken token = default) => Unsupported();
        public ValueTask<SunderContent> WorkingSummaryAsync(Guid sessionId, CancellationToken token = default) => Unsupported();
        public ValueTask<SunderContent> MemoriesAsync(Guid sessionId, string? searchText, bool includeInactive, CancellationToken token = default) => Unsupported();
        public ValueTask<SunderContent> GraphAsync(Guid sessionId, string? searchText, bool includeInactive, int limit, CancellationToken token = default) => Unsupported();
        public ValueTask<SunderContent> DetailsAsync(Guid memoryId, CancellationToken token = default) => Unsupported();
        public ValueTask<SunderContent> SemanticStateAsync(Guid sessionId, string? profileId, CancellationToken token = default) => Unsupported();
        public ValueTask<SunderContent> IndexStatusAsync(Guid memoryId, AppSemanticEmbeddingContext? context, CancellationToken token = default) => Unsupported();
        public ValueTask<SunderContent> WorkerStatusAsync(CancellationToken token = default) => Unsupported();
        public ValueTask<SunderContent> MetricsAsync(CancellationToken token = default) => Unsupported();
        public ValueTask<SunderContent> UpdateAsync(Guid memoryId, string category, string content, string? note, CancellationToken token = default) => Unsupported();
        public ValueTask<SunderContent> PinAsync(Guid memoryId, bool isPinned, CancellationToken token = default) => Unsupported();
        public ValueTask<SunderContent> ContestAsync(Guid memoryId, CancellationToken token = default) => Unsupported();
        public ValueTask<SunderContent> ForgetAsync(Guid memoryId, CancellationToken token = default) => Unsupported();
        public ValueTask<SunderContent> SupersedeAsync(Guid memoryId, CancellationToken token = default) => Unsupported();
        public ValueTask<SunderContent> CorrectAsync(Guid memoryId, string category, string content, CancellationToken token = default) => Unsupported();
        public ValueTask<SunderContent> ReindexAsync(Guid sessionId, string? profileId, CancellationToken token = default) => Unsupported();

        private async IAsyncEnumerable<AppMemoryChanged> Changes([EnumeratorCancellation] CancellationToken token)
        {
            var subscription = Interlocked.Increment(ref _subscriptionCount);
            try
            {
                yield return new(AppMemoryChangeKind.ResetRequired);
                if (subscription == 1)
                {
                    yield return new(AppMemoryChangeKind.Session);
                    yield break;
                }

                SecondSubscription.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                Interlocked.Increment(ref _closedStreamCount);
            }
        }

        private static ValueTask<SunderContent> Unsupported()
            => ValueTask.FromException<SunderContent>(new NotSupportedException());
    }
}
