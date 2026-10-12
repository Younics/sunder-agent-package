using System.Collections;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Protocol;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyRoleLocalMemoryEmbedding(Assembly worker, IServiceProvider services, object store, RegressionTestRpcState state)
    {
        var runtime = new MemoryConsumerRuntime();
        var embedding = new MemoryConsumerEmbedding("Role-local embeddings");
        AddGeneratedPeer(state, "sunder.package.agent", RegressionTestBindings.RuntimeCatalog, runtime, "agent.runtime.catalog");
        AddGeneratedPeer(state, "example.embedding", RegressionTestBindings.EmbeddingProviders, embedding, "example.embedding.provider");
        var embeddingRetired = false;
        try
        {
            var resolver = Service("Services.SemanticModelRuntimeResolver");
            await ((Sunder.Sdk.Worker.ISunderWorkerLifecycleService)resolver).ActivateAsync(null!);
            var resolved = (await Call(resolver, "ResolveForProfileAsync", runtime.Profile.ProfileId, CancellationToken.None))!;
            Assert.Same(worker, resolved.GetType().Assembly);
            Assert.Equal("Role-local embeddings", resolved.GetType().GetProperty("ProviderDisplayName")!.GetValue(resolved));

            var upsert = store.GetType().GetMethod("UpsertMemory")!;
            var request = JsonSerializer.SerializeToElement(new
            {
                sessionId = runtime.Session.SessionId, category = "fact", content = "Role-local vector", normalizedContent = "role-local vector",
                evidenceText = "Evidence", sourceTurnId = Guid.NewGuid(), isPinned = false, importance = 0.8f, confidence = 0.9f,
            }).Deserialize(upsert.GetParameters()[0].ParameterType, MemoryProviderJson)!;
            var memory = upsert.Invoke(store, [request, null])!;
            var memories = Array.CreateInstance(memory.GetType(), 1); memories.SetValue(memory, 0);
            var backend = Service("Services.SemanticMemoryRetrievalBackend");
            Assert.Equal(1, await Call(backend, "ReindexSessionAsync", runtime.Session.SessionId, runtime.Profile.ProfileId, memories, CancellationToken.None));
            Assert.True(embedding.GenerationCount > 0);
            var persisted = store.GetType().GetMethod("ListEmbeddings")!.Invoke(store, [runtime.Session.SessionId, "embedding", "embedding/model"])!;
            var pair = Assert.Single(((IEnumerable)persisted).Cast<object>());
            var vector = pair.GetType().GetProperty("Value")!.GetValue(pair)!;
            Assert.Same(worker, vector.GetType().Assembly);
            Assert.Equal(new double[] { 1, 0, 0 }, (IReadOnlyList<double>)vector.GetType().GetProperty("Values")!.GetValue(vector)!);

            var changed = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<Guid> onChanged = id => changed.TrySetResult(id);
            Action onReset = () => reset.TrySetResult();
            using var subscription = (IDisposable)resolver.GetType().GetMethod("SubscribeToSessionChanges", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(resolver, [onChanged, onReset])!;
            await runtime.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            runtime.NotifySession(); Assert.Equal(runtime.Session.SessionId, await changed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            runtime.NotifyReset(); await reset.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await state.RetireAsync(embedding).WaitAsync(TimeSpan.FromSeconds(5));
            embeddingRetired = true;
            Assert.Null(await Call(resolver, "ResolveForProfileAsync", runtime.Profile.ProfileId, CancellationToken.None));
        }
        finally
        {
            if (!embeddingRetired) await state.RetireAsync(embedding).WaitAsync(TimeSpan.FromSeconds(5));
            await state.RetireAsync(runtime).WaitAsync(TimeSpan.FromSeconds(5));
        }

        object Service(string name) => services.GetRequiredService(worker.GetType("Sunder.Package.Agent.Memory.Semantic." + name, true)!);
    }
}
