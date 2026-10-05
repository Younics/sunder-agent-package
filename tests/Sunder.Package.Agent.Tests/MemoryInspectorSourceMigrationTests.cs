using System.Runtime.CompilerServices;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

extern alias MemoryWorker;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Runtime;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Services;

public sealed class MemoryInspectorSourceMigrationTests
{
    internal static readonly string[] SourcePaths = new[]
    {
        "ManagementService.cs", "ManagementClient.cs", "ManagementModels.cs", "MemoryValueMapper.cs", "MemoryValueJson.cs", "ProjectionContent.cs",
    }.Select(file => "src/Sunder.Package.Agent.Memory.Semantic/Rpc/" + file).ToArray();

    [AgentPublicSourceMigrationFact]
    public async Task InspectorSource_PreservesContractStoreMutationsStreamsAndBoundedContentAcrossIsolatedApp()
    {
        using var source = PrivateManagementSourceFixture.Load("Memory.Semantic", RegressionTestRpcDescriptors.Get("sunder.agent.memory.semantic.inspector"));
        using var package = RegressionTestPackageScope.Create();
        await using var store = new MemoryLocalStore(package.Context);
        using var catalog = new RegressionTestExtensionCatalog();
        var settings = new MemorySemanticSettingsService(package.Context);
        using var resolver = new SemanticModelRuntimeResolver(catalog.Memory(), settings);
        var metrics = new SemanticMemoryMetricsService();
        var retrieval = new SemanticMemoryRetrievalBackend(store, resolver, settings);
        await using var indexing = new SemanticMemoryIndexingBackgroundService(store, resolver, settings, retrieval, metrics);
        using var inspector = new MemoryInspectorService(store, retrieval, indexing, resolver, metrics);
        var operations = new Operations(new MemoryRuntimeHandler(inspector));
        var type = source.Worker.GetType("Sunder.Package.Agent.Memory.Semantic.Rpc.ManagementService", true)!;
        var service = AgentRoleBoundary.Create(type, operations);
        var registration = Assert.IsType<SunderWorkerProviderRegistration>(type.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(service, null));
        Assert.Equal("semantic.memory.inspector", registration.ProviderId);
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        state.Add("sunder.package.agent.memory.semantic", source.Descriptor, service, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
        var clientType = source.App.GetType("Sunder.Package.Agent.Memory.Semantic.Runtime.MemoryInspectorSourceClient", true)!;
        var client = AgentRoleBoundary.Create(clientType, new RegressionTestRpcClient(state, "sunder.package.agent.memory.semantic"));
        ValueTask<MemoryProjection> query(MemoryQuery request, CancellationToken token)
            => AgentRoleBoundary.As<ValueTask<MemoryProjection>>(AgentRoleBoundary.Call(client, "QueryAsync", request, token)!);
        ValueTask<MemoryProjection> command(MemoryCommand request, CancellationToken token)
            => AgentRoleBoundary.As<ValueTask<MemoryProjection>>(AgentRoleBoundary.Call(client, "CommandAsync", request, token)!);
        IAsyncEnumerable<MemoryChanged> subscribe(MemoryChangeSubscription request, CancellationToken token)
            => AgentRoleBoundary.As<IAsyncEnumerable<MemoryChanged>>(AgentRoleBoundary.Call(client, "ChangesAsync", request, token)!);
        var sessionId = Guid.NewGuid();
        var memory = store.UpsertMemory(new(sessionId, "preference", "Initial λ content", "initial λ content", "Evidence", Guid.NewGuid(), false, 0.8f, 0.9f));
        Assert.Equal(memory.MemoryId, Assert.Single((await query(new(MemoryQueryKind.Memories, sessionId), default)).Memories!).MemoryId);
        Assert.Empty((await query(new(MemoryQueryKind.Sessions), default)).Sessions!);
        Assert.Null((await query(new(MemoryQueryKind.SessionCheckpoint, sessionId), default)).Checkpoint);
        Assert.Null((await query(new(MemoryQueryKind.WorkingSummary, sessionId), default)).WorkingSummary);
        Assert.NotNull((await query(new(MemoryQueryKind.Details, MemoryId: memory.MemoryId), default)).Evidence);
        Assert.NotNull((await query(new(MemoryQueryKind.SemanticState, sessionId), default)).SemanticState);
        Assert.NotNull((await query(new(MemoryQueryKind.WorkerStatus), default)).WorkerStatus);
        Assert.NotNull((await query(new(MemoryQueryKind.Metrics), default)).Metrics);
        var context = new SemanticEmbeddingContext("Unavailable", "No provider selected", false, null, null, null, null);
        Assert.NotNull((await query(new(MemoryQueryKind.SemanticIndexStatus, Memory: memory, SemanticContext: context), default)).IndexStatus);
        Assert.Equal(memory, operations.LastQuery!.Memory);
        Assert.Equal(context, operations.LastQuery.SemanticContext);

        using var lifetime = new CancellationTokenSource();
        await using var changes = subscribe(new(), lifetime.Token).GetAsyncEnumerator();
        Assert.True(await changes.MoveNextAsync());
        Assert.Equal(MemoryChangeKind.ResetRequired, changes.Current.Kind);
        var next = changes.MoveNextAsync().AsTask();
        var updated = await command(new(MemoryCommandKind.Update, memory.MemoryId, "preference", "Updated content", "A note"), default);
        Assert.Equal("Updated content", updated.Memory!.Content);
        Assert.True(indexing.QueueMemoryIndex(memory.MemoryId, "source-profile"));
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(MemoryChangeKind.Worker, changes.Current.Kind);
        Assert.Null(changes.Current.SessionId);
        Assert.True((await command(new(MemoryCommandKind.Pin, memory.MemoryId, IsPinned: true), default)).Memory!.IsPinned);
        Assert.Equal(MemoryLocalStore.ContestedState, (await command(new(MemoryCommandKind.Contest, memory.MemoryId), default)).Memory!.State);
        var correction = (await command(new(MemoryCommandKind.Correct, memory.MemoryId, "preference", "Corrected content"), default)).Correction!;
        Assert.Equal(correction.CorrectedMemory.MemoryId, store.GetMemory(memory.MemoryId)!.SupersededByMemoryId);
        Assert.Equal(MemoryLocalStore.ForgottenState, (await command(new(MemoryCommandKind.Forget, correction.CorrectedMemory.MemoryId), default)).Memory!.State);
        Assert.Equal(MemoryLocalStore.SupersededState, (await command(new(MemoryCommandKind.Supersede, memory.MemoryId), default)).Memory!.State);
        Assert.NotNull((await command(new(MemoryCommandKind.Reindex, sessionId), default)).ReindexResult);
        var missingId = await Assert.ThrowsAsync<SunderRpcException>(() => query(new(MemoryQueryKind.Memories), default).AsTask());
        Assert.Equal("memory.semantic.operation-failed", missingId.Error.Code);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command(new(MemoryCommandKind.Pin, memory.MemoryId, IsPinned: false), cancelled.Token).AsTask());
            Assert.True(store.GetMemory(memory.MemoryId)!.IsPinned);
        }
        await lifetime.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => changes.MoveNextAsync().AsTask());

        operations.Response = new(Memory: memory with { Content = new string('m', 2 * 1024 * 1024) });
        Assert.Equal(operations.Response.Memory, (await query(new(MemoryQueryKind.Memories, sessionId), default)).Memory);
        state.FailNextResponseContentRegistration(new IOException("private memory content failure"));
        var contentFault = await Assert.ThrowsAsync<SunderRpcException>(() => query(new(MemoryQueryKind.Memories, sessionId), default).AsTask());
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, contentFault.Error.Kind);
        Assert.DoesNotContain("private memory content failure", contentFault.Message, StringComparison.Ordinal);
        operations.Response = new(Memory: memory with { Content = new string('m', 16 * 1024 * 1024) });
        var oversized = await Assert.ThrowsAsync<SunderRpcException>(() => query(new(MemoryQueryKind.Memories, sessionId), default).AsTask());
        Assert.Equal("memory.semantic.operation-failed", oversized.Error.Code);
        operations.StreamFailure = new MemoryInspectorOperationException(new string('f', 3000));
        await using (var failed = subscribe(new(), default).GetAsyncEnumerator())
        {
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => failed.MoveNextAsync().AsTask());
            Assert.Equal("memory.semantic.operation-failed", error.Error.Code);
            Assert.Equal(2048, error.Error.Message.Length);
        }
        await state.RetireAsync(service);
        var unavailable = await Assert.ThrowsAsync<SunderRpcException>(() => query(new(MemoryQueryKind.Sessions), default).AsTask());
        Assert.Equal("rpc.provider-not-found", unavailable.Error.Code);
    }

    private sealed class Operations(IMemoryInspectorRpcService inner) : IMemoryInspectorRpcService
    {
        internal MemoryQuery? LastQuery { get; private set; }
        internal MemoryProjection? Response { get; set; }
        internal Exception? StreamFailure { get; set; }
        public ValueTask<MemoryProjection> QueryAsync(SunderRpcInvocationContext context, MemoryQuery request, CancellationToken cancellationToken = default)
        {
            LastQuery = request;
            return Response is { } response ? ValueTask.FromResult(response) : inner.QueryAsync(context, request, cancellationToken);
        }
        public ValueTask<MemoryProjection> CommandAsync(SunderRpcInvocationContext context, MemoryCommand request, CancellationToken cancellationToken = default)
            => inner.CommandAsync(context, request, cancellationToken);
        public async IAsyncEnumerable<MemoryChanged> ChangesAsync(SunderRpcInvocationContext context, MemoryChangeSubscription request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (StreamFailure is { } failure) throw failure;
            await foreach (var item in inner.ChangesAsync(context, request, cancellationToken).WithCancellation(cancellationToken)) yield return item;
        }
    }

    private sealed class Noop : IDisposable { internal static Noop Instance { get; } = new(); public void Dispose() { } }
}
