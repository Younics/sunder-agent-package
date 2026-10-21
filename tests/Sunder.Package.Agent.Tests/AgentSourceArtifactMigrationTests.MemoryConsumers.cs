using System.Collections;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static readonly string[] MemoryConsumerSources =
    [
        "src/Sunder.Package.Agent.Memory.Semantic/Runtime/ISemanticMemoryRpcCatalog.cs",
        "src/Sunder.Package.Agent.Memory.Semantic/Runtime/LegacySemanticMemoryRpcCatalog.cs",
        "src/Sunder.Package.Agent.Memory.Semantic/Services/SemanticModelRuntimeResolver.Legacy.cs",
        "src/Sunder.Package.Agent.Memory.Semantic/Rpc/GeneratedEmbeddingClient.cs",
        "src/Sunder.Package.Agent.Memory.Semantic/Rpc/GeneratedRuntimeCatalogClient.cs",
        "src/Sunder.Package.Agent.Memory.Semantic/Rpc/SemanticMemoryRpcCatalog.cs",
    ];

    private static async Task VerifyMemoryConsumers(string root)
    {
        var directory = Path.Combine(root, "MemoryWorker");
        var selected = File.ReadAllBytes(Path.Combine(directory, "sunder-contracts.lock.json"));
        var baseline = CanonicalPackageRoles.ReadOwnedBaseline(directory);
        using var roles = new CanonicalPackageRoles();
        await ExerciseMemoryConsumers(roles.Load("Memory.Semantic", "worker", directory));
        Assert.Equal(selected, File.ReadAllBytes(Path.Combine(directory, "sunder-contracts.lock.json")));
        Assert.Equal(baseline, CanonicalPackageRoles.ReadOwnedBaseline(directory));
    }

    internal static async Task ExerciseMemoryConsumers(Assembly worker)
    {
        using var package = RegressionTestPackageScope.Create();
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var runtime = new MemoryConsumerRuntime();
        var embedding = new MemoryConsumerEmbedding("Original embeddings");
        AddRuntime(runtime);
        AddEmbedding(embedding);
        var client = new MemoryConsumerRpc(new RegressionTestRpcClient(state, "younics.ai.agent.memory.semantic"));
        var context = new TestPackageContext("younics.ai.agent.memory.semantic", package.RootPath, version: "2.0.0", storage: package.Context.Storage);
        var graph = CreateGeneratedWorker(worker, context, client);
        await using var graphLifetime = graph.Lifetime;
        var resolver = Service("Services.SemanticModelRuntimeResolver");
        var lifetime = (IAsyncDisposable)resolver;
        await ((ISunderWorkerLifecycleService)resolver).PrepareCandidateAsync();
        Assert.Equal(0, client.DiscoveryCount);
        await ((ISunderWorkerLifecycleService)resolver).ActivateAsync(null!);
        var resolved = await Current();
        Assert.Equal("Original embeddings", Property(resolved, "ProviderDisplayName"));
        Assert.Equal("example.embedding", Property(resolved, "OwnerPackageId"));
        var fingerprint = Property(resolved, "ConfigurationFingerprint");
        var result = (await Generate(resolved, "one"))!;
        Assert.Same(worker, result.GetType().Assembly);
        Assert.Equal(new double[] { 1, 0, 0 }, Assert.IsAssignableFrom<IReadOnlyList<double>>(Property(result, "Values")));
        var batch = ((System.Collections.IEnumerable)(await Call(resolved,
            "GenerateEmbeddingsAsync", new[] { "first", "", "third" }, CancellationToken.None))!).Cast<object?>().ToArray();
        Assert.Equal(3, batch.Length);
        Assert.Null(batch[1]);
        Assert.Equal("embedding/model", Property(batch[2]!, "ModelId"));
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var calls = embedding.GenerationCount;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Generate(resolved, "cancelled", cancelled.Token));
            Assert.Equal(calls, embedding.GenerationCount);
        }

        var store = Service("MemoryLocalStore");
        await ((ISunderWorkerActivationService)store).ActivateAsync(null!);
        var upsert = store.GetType().GetMethod("UpsertMemory")!;
        var request = JsonSerializer.SerializeToElement(new
        {
            sessionId = runtime.Session.SessionId, category = "fact", content = "Generated consumer vectors", normalizedContent = "generated consumer vectors",
            evidenceText = "evidence", sourceTurnId = Guid.NewGuid(), isPinned = false, importance = 0.8f, confidence = 0.9f,
        }).Deserialize(upsert.GetParameters()[0].ParameterType, MemoryProviderJson)!;
        var memory = upsert.Invoke(store, [request, null])!;
        Assert.Same(worker, memory.GetType().Assembly);
        var memories = Array.CreateInstance(memory.GetType(), 1); memories.SetValue(memory, 0);
        Assert.Equal(1, await Call(Service("Services.SemanticMemoryRetrievalBackend"), "ReindexSessionAsync",
            runtime.Session.SessionId, runtime.Profile.ProfileId, memories, CancellationToken.None));
        Assert.Single(((IEnumerable)AgentRoleBoundary.Call(store, "ListEmbeddings", runtime.Session.SessionId, "embedding", "embedding/model")!).Cast<object>());

        var sessionChanged = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = (IDisposable)AgentRoleBoundary.Call(resolver, "SubscribeToSessionChanges",
            (Action<Guid>)(id => sessionChanged.TrySetResult(id)), (Action)(() => reset.TrySetResult()))!;
        await runtime.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runtime.NotifySession();
        Assert.Equal(runtime.Session.SessionId, await sessionChanged.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        runtime.NotifyReset();
        await reset.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var returnedSession = InvokeRuntimeCatalog(async (value, token) =>
            await Call(value, "GetSessionAsync", runtime.Session.SessionId, token));
        await using var assertionReceiver = new GeneratedServiceTestScope(client);
        var projection = RegressionTestGeneratedProviderProjection.FromCandidate(
            Environment.GetEnvironmentVariable("SUNDER_AGENT_PUBLIC_FIXTURE_CANDIDATE")!, RegressionTestBindings.RuntimeCatalog);
        Assert.Equal(runtime.Session, await projection.ProjectAsync(returnedSession, typeof(AgentSessionRecord), assertionReceiver, default));
        Assert.Null(InvokeRuntimeCatalog(async (value, token) => await Call(value, "GetProfileAsync", "missing", token)));

        client.LookupFailure = SunderRpcErrorKind.Unavailable;
        Assert.Null(await Resolve());
        client.LookupFailure = SunderRpcErrorKind.PermissionDenied;
        var denied = await Assert.ThrowsAsync<SunderRpcException>(Resolve);
        Assert.True(denied.Error.Kind == SunderRpcErrorKind.PermissionDenied, denied.ToString());
        client.LookupFailure = null;
        Assert.NotNull(await Resolve());
        client.ChangeEmbeddingGeneration = true;
        var beforeStale = embedding.GenerationCount;
        await Assert.ThrowsAsync(Type("Services.EmbeddingProviderRetiredException"), () => Generate(resolved, "stale"));
        Assert.Equal(beforeStale, embedding.GenerationCount);
        Assert.Equal(false, AgentRoleBoundary.Call(resolved, "IsSameActivation", resolved));
        client.ChangeEmbeddingGeneration = false;
        await Assert.ThrowsAsync(Type("Services.EmbeddingProviderRetiredException"), () => Generate(resolved, "still retired"));
        Assert.Equal(beforeStale, embedding.GenerationCount);
        await state.RetireAsync(embedding).WaitAsync(TimeSpan.FromSeconds(5));
        AddEmbedding(embedding);
        var retiredSelection = resolved;

        embedding.Block = true;
        resolved = await Current();
        Assert.Equal(false, AgentRoleBoundary.Call(retiredSelection, "IsSameActivation", resolved));
        await Assert.ThrowsAsync(Type("Services.EmbeddingProviderRetiredException"), () => Generate(retiredSelection, "replacement must not run"));
        Assert.Equal(beforeStale, embedding.GenerationCount);
        var inFlight = Generate(resolved, "retire in flight");
        await embedding.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RetireAsync(embedding).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync(Type("Services.EmbeddingProviderRetiredException"), () => inFlight);
        Assert.True(embedding.Cancelled);
        Assert.Null(await Resolve());
        var replacement = new MemoryConsumerEmbedding("Replacement embeddings");
        AddEmbedding(replacement);
        var current = await Current();
        Assert.Equal("Replacement embeddings", Property(current, "ProviderDisplayName"));
        Assert.Equal(fingerprint, Property(current, "ConfigurationFingerprint"));
        Assert.Equal(false, AgentRoleBoundary.Call(resolved, "IsSameActivation", current));
        await Assert.ThrowsAsync(Type("Services.EmbeddingProviderRetiredException"), () => Generate(resolved, "old reference"));
        Assert.NotNull(await Generate(current, "replacement"));

        var stopping = lifetime.DisposeAsync().AsTask();
        var repeatedStop = lifetime.DisposeAsync().AsTask();
        Assert.Same(stopping, repeatedStop);
        await stopping;
        Assert.True(((ISunderWorkerLifecycleService)resolver).Completion.IsCompletedSuccessfully);
        Assert.Equal(0, runtime.SessionSubscriberCount);
        await assertionReceiver.DisposeAsync();
        await graphLifetime.DisposeAsync();
        Assert.Equal(0, client.ActiveWatches);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Call(resolver, "SubscribeToSessionChanges", (Action<Guid>)(_ => { }), (Action)(() => { })));

        void AddRuntime(MemoryConsumerRuntime value) => AddGeneratedPeer(state, "younics.ai.agent", RegressionTestBindings.RuntimeCatalog, value, "agent.runtime.catalog");
        void AddEmbedding(MemoryConsumerEmbedding value) => AddGeneratedPeer(state, "example.embedding", RegressionTestBindings.EmbeddingProviders, value, "example.embedding.provider");
        Type Type(string name) => worker.GetType("Sunder.Package.Agent.Memory.Semantic." + name, true)!;
        object Service(string name) => graph.Services.GetRequiredService(Type(name));
        static object? Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);
        Task<object?> Resolve() => Call(resolver, "ResolveForProfileAsync", runtime.Profile.ProfileId, CancellationToken.None);
        async Task<object> Current()
        {
            var value = await Resolve();
            Assert.IsType(Type("Services.ResolvedEmbeddingProvider"), value);
            return value!;
        }
        static Task<object?> Generate(object value, string text, CancellationToken token = default)
            => Call(value, "GenerateEmbeddingAsync", text, token);
        object? InvokeRuntimeCatalog(Func<object, CancellationToken, ValueTask<object?>> callback)
        {
            var method = resolver.GetType().GetMethod("InvokeRuntimeCatalog", BindingFlags.Instance | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(object));
            return method.Invoke(resolver, [AgentRoleBoundary.Convert(callback, method.GetParameters()[0].ParameterType), null]);
        }
    }
}
