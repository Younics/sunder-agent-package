using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

extern alias MemoryWorker;
extern alias MemoryApp;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Runtime;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Services;
using MemoryServices = MemoryApp::Sunder.Generated.Packages.Younics_Ai_Agent_Memory_Semantic_Services;
using AppMemoryChangeKind = MemoryApp::Sunder.Package.Agent.Memory.Semantic.Runtime.MemoryChangeKind;
using AppSemanticEmbeddingContext = MemoryApp::Sunder.Package.Agent.Memory.Semantic.Services.SemanticEmbeddingContext;

public sealed class MemoryInspectorSourceMigrationTests
{
    // Authenticated migration input inventory. The historical reader resolves
    // physically retired paths from captured data, never compiled legacy code.
    internal static readonly string[] SourcePaths = new[]
    {
        "ManagementService.cs", "ManagementClient.cs", "ManagementModels.cs", "MemoryValueMapper.cs", "MemoryValueJson.cs", "ProjectionContent.cs",
    }.Select(file => "src/Sunder.Package.Agent.Memory.Semantic/Rpc/" + file).ToArray();

    [AgentPublicSourceMigrationFact]
    public async Task InspectorSource_PreservesContractStoreMutationsStreamsAndBoundedContentAcrossIsolatedApp()
    {
        using var source = PrivateManagementSourceFixture.Load("Memory.Semantic", CurrentPrivateRpcDescriptors.Get("younics.ai.agent.memory.semantic.inspector"));
        using var package = RegressionTestPackageScope.Create();
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        await using var agentRuntime = RegressionTestRuntime.Create(package.Context,
            new RegressionTestRpcClient(state, "younics.ai.agent"));
        var runtimeCatalog = agentRuntime.GetRequiredService<Sunder.Package.Agent.Services.AgentRuntimeCatalog>();
        state.AddGenerated("younics.ai.agent", runtimeCatalog,
            runtimeCatalog.CreateSunderRpcProviderRegistration(), Noop.Instance, "agent.runtime.catalog");
        var rpc = new RegressionTestRpcClient(state, "younics.ai.agent.memory.semantic");
        var (options, workerLifetime, workerServices) = AgentSourceArtifactMigrationTests.CreateGeneratedWorker(source.Worker, package.Context, rpc);
        await using var workerOwner = workerLifetime;
        await AgentSourceArtifactMigrationTests.ActivateGeneratedComposition(options);
        var registration = Assert.Single(options.Providers, value => value.ProviderId == "semantic.memory.inspector");
        state.Add("younics.ai.agent.memory.semantic", source.Descriptor, registration.Handler, registration.Handler,
            Noop.Instance, registration.ProviderId, "2.0.0");
        await using var receiver = new GeneratedServiceTestScope(rpc);
        var entry = Assert.Single((await receiver.Providers<MemoryServices.IMemoryInspectorService>().ListAsync()).Providers);
        var service = entry.Service;
        Assert.Null(source.App.GetType("Sunder.Package.Agent.Memory.Semantic.Runtime.MemoryInspectorSourceClient"));
        Assert.NotNull(source.App.GetType(typeof(MemoryServices.IMemoryInspectorService).FullName!, true));

        await using var store = new MemoryLocalStore(package.Context);
        var sessionId = Guid.NewGuid();
        var memory = store.UpsertMemory(new(sessionId, "preference", "Initial content", "initial content", "Evidence", Guid.NewGuid(), false, 0.8f, 0.9f));

        Assert.Equal(memory.MemoryId, Assert.Single((await ReadAsync(service.MemoriesAsync(sessionId, "Initial", includeInactive: true))).Memories!).MemoryId);
        Assert.Empty((await ReadAsync(service.SessionsAsync())).Sessions!);
        Assert.Null((await ReadAsync(service.SessionCheckpointAsync(sessionId))).Checkpoint);
        Assert.Null((await ReadAsync(service.WorkingSummaryAsync(sessionId))).WorkingSummary);
        Assert.NotNull((await ReadAsync(service.DetailsAsync(memory.MemoryId))).Evidence);
        Assert.NotNull((await ReadAsync(service.SemanticStateAsync(sessionId, profileId: null))).SemanticState);
        Assert.NotNull((await ReadAsync(service.WorkerStatusAsync())).WorkerStatus);
        Assert.NotNull((await ReadAsync(service.MetricsAsync())).Metrics);
        var context = new AppSemanticEmbeddingContext("Unavailable", "No provider selected", false, null, null, null, null);
        Assert.NotNull((await ReadAsync(service.IndexStatusAsync(memory.MemoryId, context))).IndexStatus);

        // Establish the real runtime-catalog subscription before observing worker
        // changes. An absent catalog legitimately emits resets, which can coalesce
        // a worker notification; that is not a worker-event ordering guarantee.
        var inspector = workerServices.GetRequiredService(source.Worker.GetType(
            "Sunder.Package.Agent.Memory.Semantic.MemoryInspectorService", true)!);
        var resetEvent = inspector.GetType().GetEvent("ResetRequired")!;
        var catalogReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action initialReset = () => catalogReady.TrySetResult();
        resetEvent.AddEventHandler(inspector, initialReset);
        using var lifetime = new CancellationTokenSource();
        await using var changes = service.ChangesAsync(lifetime.Token).GetAsyncEnumerator();
        try
        {
            await catalogReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await changes.MoveNextAsync());
        }
        finally { resetEvent.RemoveEventHandler(inspector, initialReset); }
        Assert.Equal(AppMemoryChangeKind.ResetRequired, changes.Current.Kind);
        var workerChange = changes.MoveNextAsync().AsTask();
        var updated = await ReadAsync(service.UpdateAsync(memory.MemoryId, "preference", "Updated content", "A note"));
        Assert.Equal("Updated content", updated.Memory!.Content);
        Assert.Equal("Updated content", store.GetMemory(memory.MemoryId)!.Content);
        var indexing = workerServices.GetRequiredService(source.Worker.GetType(
            "Sunder.Package.Agent.Memory.Semantic.Services.SemanticMemoryIndexingBackgroundService", true)!);
        Assert.True((bool)AgentRoleBoundary.Call(indexing, "QueueMemoryIndex", memory.MemoryId, "source-profile")!);
        Assert.True(await workerChange.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(AppMemoryChangeKind.Worker, changes.Current.Kind);
        Assert.Null(changes.Current.SessionId);

        Assert.True((await ReadAsync(service.PinAsync(memory.MemoryId, true))).Memory!.IsPinned);
        Assert.Equal(MemoryLocalStore.ContestedState, (await ReadAsync(service.ContestAsync(memory.MemoryId))).Memory!.State);
        var correction = (await ReadAsync(service.CorrectAsync(memory.MemoryId, "preference", "Corrected content"))).Correction!;
        Assert.Equal(correction.CorrectedMemory.MemoryId, store.GetMemory(memory.MemoryId)!.SupersededByMemoryId);
        Assert.Equal(MemoryLocalStore.ForgottenState, (await ReadAsync(service.ForgetAsync(correction.CorrectedMemory.MemoryId))).Memory!.State);
        Assert.Equal(MemoryLocalStore.SupersededState, (await ReadAsync(service.SupersedeAsync(memory.MemoryId))).Memory!.State);
        Assert.NotNull((await ReadAsync(service.ReindexAsync(sessionId, profileId: null))).ReindexResult);
        var missingId = await Assert.ThrowsAsync<SunderRpcException>(() => service.UpdateAsync(Guid.NewGuid(), "preference", "Missing memory", null).AsTask());
        Assert.Equal("memory.semantic.operation-failed", missingId.Error.Code);
        var endpoint = Assert.Single((await rpc.DiscoverAsync(source.Descriptor.ContractId)).Providers).Endpoint;
        var absentId = await Assert.ThrowsAsync<SunderRpcException>(() => rpc.InvokeAsync(endpoint, "memory-inspector", "memories",
            JsonSerializer.SerializeToElement(new { searchText = (string?)null, includeInactive = false })).AsTask());
        Assert.Equal(SunderRpcErrorKind.Validation, absentId.Error.Kind);

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PinAsync(memory.MemoryId, false, cancelled.Token).AsTask());
            Assert.True(store.GetMemory(memory.MemoryId)!.IsPinned);
        }

        state.FailNextResponseContentRegistration(new IOException("private memory content failure"));
        var contentFault = await Assert.ThrowsAsync<SunderRpcException>(() => ReadAsync(service.MemoriesAsync(sessionId, null, false)).AsTask());
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, contentFault.Error.Kind);
        Assert.DoesNotContain("private memory content failure", contentFault.Message, StringComparison.Ordinal);
        var large = "00000:" + new string('m', 4_090);
        var largeMemory = store.UpsertMemory(new(sessionId, "preference", large, large, "Evidence", Guid.NewGuid(), false, 0.8f, 0.9f));
        for (var index = 1; index < 512; index++)
        {
            var content = index.ToString("D5") + ":" + new string('m', 4_090);
            store.UpsertMemory(new(sessionId, "preference", content, content, "Evidence", Guid.NewGuid(), false, 0.8f, 0.9f));
        }
        var acceptedProjection = (await ReadAsync(service.MemoriesAsync(sessionId, null, includeInactive: true))).Memories!;
        Assert.True(acceptedProjection.Sum(value => value.Content.Length) >= 2 * 1024 * 1024);
        Assert.Equal(large.Length, acceptedProjection.Single(value => value.MemoryId == largeMemory.MemoryId).Content.Length);
        for (var index = 512; index < 4_096; index++)
        {
            var content = index.ToString("D5") + ":" + new string('m', 4_090);
            store.UpsertMemory(new(sessionId, "preference", content, content, "Evidence", Guid.NewGuid(), false, 0.8f, 0.9f));
        }
        var oversized = await Assert.ThrowsAsync<SunderRpcException>(() => service.MemoriesAsync(sessionId, null, includeInactive: true).AsTask());
        Assert.Equal("memory.semantic.operation-failed", oversized.Error.Code);
        var failure = (SunderRpcException)typeof(MemoryInspectorService)
            .GetMethod("Failure", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [new MemoryInspectorOperationException(new string('f', 3000))])!;
        Assert.Equal("memory.semantic.operation-failed", failure.Error.Code);
        Assert.Equal(2048, failure.Error.Message.Length);
        await lifetime.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => changes.MoveNextAsync().AsTask());
        await state.RetireAsync(registration.Handler);
        var stale = await Assert.ThrowsAsync<SunderRpcException>(() => entry.EnsureCurrentAsync().AsTask());
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, stale.Error.Kind);
        var retired = await Assert.ThrowsAsync<SunderRpcException>(() => service.SessionsAsync().AsTask());
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, retired.Error.Kind);
        Assert.Equal("rpc.stale-endpoint", retired.Error.Code);
    }

    private static ValueTask<MemoryProjection> ReadAsync(ValueTask<SunderContent> content, CancellationToken token = default)
        => ProjectionContent.Read(content, token);

    private sealed class Noop : IDisposable { internal static Noop Instance { get; } = new(); public void Dispose() { } }
}
