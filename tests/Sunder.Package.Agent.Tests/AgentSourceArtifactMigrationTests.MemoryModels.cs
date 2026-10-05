using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

extern alias MemoryWorker;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Runtime;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static readonly string[] MemoryBusinessModelSources =
    [
        "src/Sunder.Package.Agent.Memory.Semantic/Models/SemanticEmbeddingContext.cs",
        "src/Sunder.Package.Agent.Memory.Semantic/Models/SemanticMemoryInspectorModels.cs",
        "src/Sunder.Package.Agent.Memory.Semantic/Models/SemanticMemoryServiceModels.cs",
        "src/Sunder.Package.Agent.Memory.Semantic/Models/MemoryInspectorModels.cs",
        "src/Sunder.Package.Agent.Memory.Semantic/Runtime/MemoryInspectorContracts.cs",
    ];

    private static async Task VerifyRoleLocalMemoryModels(Assembly worker, Assembly app, Assembly agentOwner)
    {
        AssertMemorySemanticOwnership(worker, app: false);
        AssertMemorySemanticOwnership(app, app: true);
        Assert.NotSame(worker.GetType("Sunder.Package.Agent.Contracts.Models.AgentSessionRecord"), app.GetType("Sunder.Package.Agent.Contracts.Models.AgentSessionRecord"));
        foreach (var assembly in new[] { worker, app })
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name == "Sunder.Package.Agent.Memory.Semantic.Protocol");
        foreach (var name in new[] { "Sunder.Package.Agent.Memory.Semantic.StoredMemoryRecord",
            "Sunder.Package.Agent.Memory.Semantic.Services.SemanticEmbeddingContext",
            "Sunder.Package.Agent.Memory.Semantic.Services.SemanticMemoryMetricsSnapshot",
            "Sunder.Package.Agent.Memory.Semantic.Runtime.MemoryProjection",
            "Sunder.Package.Agent.Memory.Semantic.Runtime.IMemoryInspectorGateway" })
        {
            Assert.Same(worker, worker.GetType(name, true)!.Assembly);
            Assert.Same(app, app.GetType(name, true)!.Assembly);
            Assert.NotSame(worker.GetType(name), app.GetType(name));
        }

        using var package = RegressionTestPackageScope.Create();
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent.memory.semantic");
        Assert.Null(worker.GetType("Sunder.Package.Agent.Memory.Semantic.Runtime.MemoryWorkerComposition"));
        Assert.Null(worker.GetType("Sunder.Package.Agent.Memory.Semantic.Runtime.MemoryWorkerBindings"));
        Assert.Null(worker.GetType("Sunder.Package.Agent.Memory.Semantic.Rpc.MemoryProviderCatalog"));
        var (options, workerLifetime, _) = CreateGeneratedWorker(worker, package.Context, rpc);
        await using var ownerLifetime = workerLifetime;
        Assert.IsType<SunderWorkerComposition>(options.Composition);
        Assert.Null(options.OnActivated); Assert.Null(options.OnShutdown);
        Assert.Same(worker.GetType("Sunder.Package.Agent.Memory.Semantic.MemorySemanticPackageConfiguration", true)!.GetProperty("Schema")!.GetValue(null), options.SettingsSchema);
        Assert.Null(options.Composition!.Services.GetService(worker.GetType("Sunder.Package.Agent.Protocol.AgentRpcCatalog", true)!));
        var storeType = worker.GetType("Sunder.Package.Agent.Memory.Semantic.MemoryLocalStore", true)!;
        var store = options.Composition!.Services.GetService(storeType)!;
        Assert.False((bool)storeType.GetProperty("IsInitialized", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!);
        await Stage("PrepareCandidateAsync", CancellationToken.None);
        var generation = (SunderWorkerGenerationContext)Activator.CreateInstance(typeof(SunderWorkerGenerationContext), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [Guid.NewGuid(), 1L], culture: null)!;
        await Stage("CommitGenerationAsync", generation, CancellationToken.None);
        Assert.False((bool)storeType.GetProperty("IsInitialized", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!);
        Assert.Empty(Directory.GetFiles(package.RootPath, "*", SearchOption.AllDirectories));
        Assert.Equal(0, state.DiscoveryCount);
        var indexingType = worker.GetType("Sunder.Package.Agent.Memory.Semantic.Services.SemanticMemoryIndexingBackgroundService", true)!;
        var indexing = options.Composition.Services.GetService(indexingType)!;
        Assert.False(IsIndexing());
        await Stage("ActivateAsync", RuntimeHelpers.GetUninitializedObject(typeof(SunderWorkerContext)), CancellationToken.None);
        Assert.True((bool)storeType.GetProperty("IsInitialized", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!);
        Assert.True(IsIndexing());
        await VerifyRoleLocalMemoryEmbedding(worker, options.Composition.Services, store, state);
        foreach (var registration in options.Providers)
            state.Add("sunder.package.agent.memory.semantic",
                registration.ProviderId == "semantic.memory.inspector" ? RegressionTestRpcDescriptors.Get("sunder.agent.memory.semantic.inspector") : RegressionTestRpcDescriptors.Get(registration.ContractId),
                registration.Handler, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");

        await VerifyAgentMemoryCleanup(agentOwner, store, state);

        var upsert = storeType.GetMethod("UpsertMemory")!;
        var session = Guid.NewGuid();
        var request = JsonSerializer.Deserialize(JsonSerializer.Serialize(new
        {
            sessionId = session, category = "fact", content = "Role-local source memory", normalizedContent = "role-local source memory",
            evidenceText = "Evidence", sourceTurnId = Guid.NewGuid(), isPinned = false, importance = 0.8f, confidence = 0.9f,
        }), upsert.GetParameters()[0].ParameterType, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var stored = upsert.Invoke(store, [request, null])!;
        var memoryId = (Guid)stored.GetType().GetProperty("MemoryId")!.GetValue(stored)!;
        await using var appServices = CreateGeneratedApp(app, package.Context, rpc);
        var gateway = appServices.GetRequiredService(app.GetType("Sunder.Package.Agent.Memory.Semantic.MemoryAppRuntimeGateway", true)!);
        var gatewayType = gateway.GetType();
        var listed = Assert.Single(((IEnumerable)(await Call("ListMemoriesAsync", session, "source", false, CancellationToken.None))!).Cast<object>());
        Assert.Same(app, listed.GetType().Assembly);
        Assert.Equal(memoryId, (Guid)listed.GetType().GetProperty("MemoryId")!.GetValue(listed)!);
        var updated = (await Call("UpdateMemoryAsync", memoryId, "preference", "Updated role-local memory", "note", CancellationToken.None))!;
        Assert.Same(app, updated.GetType().Assembly);
        Assert.Equal("Updated role-local memory", updated.GetType().GetProperty("Content")!.GetValue(updated));
        var persisted = storeType.GetMethod("GetMemory")!.Invoke(store, [memoryId])!;
        Assert.Same(worker, persisted.GetType().Assembly);
        Assert.Equal("Updated role-local memory", persisted.GetType().GetProperty("Content")!.GetValue(persisted));
        await Call("SetPinnedAsync", memoryId, true, CancellationToken.None);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call("SetPinnedAsync", memoryId, false, cancelled.Token));
        }
        persisted = storeType.GetMethod("GetMemory")!.Invoke(store, [memoryId])!;
        Assert.True((bool)persisted.GetType().GetProperty("IsPinned")!.GetValue(persisted)!);

        var reset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action onReset = () => reset.TrySetResult();
        gatewayType.GetEvent("ResetRequired")!.AddEventHandler(gateway, onReset);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action onChanged = () => changed.TrySetResult();
        gatewayType.GetEvent("SemanticWorkerStatusChanged")!.AddEventHandler(gateway, onChanged);
        await Call("InitializeAsync", CancellationToken.None);
        await reset.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((bool)indexingType.GetMethod("QueueMemoryIndex")!.Invoke(indexing, [memoryId, "profile.role-local"])!);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var metrics = (await Call("GetMetricsSnapshotAsync", CancellationToken.None))!;
        Assert.Same(app, metrics.GetType().Assembly);
        var inspector = options.Providers.Single(value => value.ProviderId == "semantic.memory.inspector");
        await state.RetireAsync(inspector.Handler).WaitAsync(TimeSpan.FromSeconds(5));
        var unavailable = await Assert.ThrowsAsync<SunderRpcException>(() => Call("ListSessionsAsync", CancellationToken.None));
        Assert.Equal("semantic-memory.inspector-unavailable", unavailable.Error.Code);
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gatewayType.GetEvent("ResetRequired")!.AddEventHandler(gateway, (Action)(() => reconnected.TrySetResult()));
        state.Add("sunder.package.agent.memory.semantic", RegressionTestRpcDescriptors.Get("sunder.agent.memory.semantic.inspector"), new object(), inspector.Handler, Noop.Instance, inspector.ProviderId, "2.0.0");
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(((IEnumerable)(await Call("ListMemoriesAsync", session, null, false, CancellationToken.None))!).Cast<object>());
        await ((IAsyncDisposable)gateway).DisposeAsync();
        Assert.True(((Task)gatewayType.GetProperty("ObservationTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gateway)!).IsCompletedSuccessfully);
        Assert.False((bool)gatewayType.GetProperty("IsOwnerStreamAvailable")!.GetValue(gateway)!);
        await ownerLifetime.DisposeAsync();
        Assert.False(IsIndexing());
        Assert.True(((Task)indexingType.GetProperty("Completion")!.GetValue(indexing)!).IsCompleted);

        Task Stage(string method, params object[] arguments)
            => ((Task)typeof(SunderWorkerComposition).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(options.Composition, arguments)!).WaitAsync(TimeSpan.FromSeconds(15));
        bool IsIndexing()
        {
            var status = indexingType.GetMethod("GetStatus")!.Invoke(indexing, null)!;
            return (bool)status.GetType().GetProperty("IsRunning")!.GetValue(status)!;
        }

        async Task<object?> Call(string method, params object?[] arguments)
        {
            var task = (Task)gatewayType.GetMethod(method)!.Invoke(gateway, arguments)!;
            await task.ConfigureAwait(false);
            return task.GetType().GetProperty("Result")?.GetValue(task);
        }
    }
}
