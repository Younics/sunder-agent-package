using System.Reflection;
using System.Collections;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static object CreateAgentEmbeddingCatalog(Assembly owner, ISunderRpcClient rpc)
    {
        var service = owner.GetType(typeof(AgentServices.IEmbeddingProviderService).FullName!, true)!;
        var catalog = typeof(IServiceProviders<>).MakeGenericType(service);
        var implementation = owner.GetTypes().Single(type => type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters
            && catalog.IsAssignableFrom(type) && type.GetConstructor([typeof(ISunderRpcClient)]) is not null);
        return Activator.CreateInstance(implementation, rpc)!;
    }

    private static async Task<object> SingleAgentEmbeddingProvider(object catalog)
    {
        var snapshot = (await AgentRoleBoundary.Await(AgentRoleBoundary.Call(catalog, "ListAsync", CancellationToken.None)!))!;
        return Assert.Single(((IEnumerable)snapshot.GetType().GetProperty("Providers")!.GetValue(snapshot)!).Cast<object>());
    }

    private static AgentServices.IEmbeddingProviderService EmbeddingService(object provider)
        => AgentRoleBoundary.As<AgentServices.IEmbeddingProviderService>(provider.GetType().GetProperty("Service")!.GetValue(provider)!);

    private static async Task VerifyAgentEmbeddingConsumer(Assembly owner, Assembly providerAssembly, Assembly probes)
    {
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var peer = new MemoryConsumerEmbedding("Generated Agent embeddings");
        var registration = Register(peer);
        var rpc = new RegressionTestRpcClient(state, "younics.ai.agent");
        var selected = Assert.Single((await rpc.DiscoverAsync(RegressionTestBindings.EmbeddingProviders)).Providers);
        var observer = new FilesRpcObserver(rpc, "generate");
        var catalog = CreateAgentEmbeddingCatalog(owner, observer);
        await using var lifetime = (IAsyncDisposable)catalog;
        var client = EmbeddingService(await SingleAgentEmbeddingProvider(catalog));
        Assert.Null(owner.GetType("Sunder.Package.Agent.Runtime.Rpc.GeneratedEmbeddingProviderClient"));
        Assert.Null(owner.GetType("Sunder.Package.Agent.Runtime.Rpc.AgentEmbeddingBindings"));
        Assert.Equal("Generated Agent embeddings", (await client.DescribeAsync()).DisplayName);
        Assert.Equal(3, Assert.Single(await client.ListModelsAsync()).Dimensions);
        Assert.Equal(AgentProviderReadinessStatus.Ready, (await client.GetReadinessAsync()).Status);
        Assert.Equal("stable-space", await client.SpaceIdentityAsync("embedding/model"));
        Assert.Null(await client.GenerateAsync("embedding/model", ""));
        var batch = await client.GenerateBatchAsync("embedding/model", ["first", "", "third"]);
        Assert.Equal(3, batch.Count); Assert.Null(batch[1]); Assert.Equal(new double[] { 1, 0, 0 }, batch[2]!.Values);
        observer.AssertScopedOperations(); Assert.All(observer.Scopes, scope => Assert.True(scope.Disposed));

        foreach (var change in new Func<SunderRpcProviderSnapshot, SunderRpcProviderSnapshot>[]
        {
            value => value with { PackageId = "different.package" }, value => value with { PackageVersion = "2.0.1" },
            value => value with { ProviderId = "different.provider" }, value => value with { ContractId = "different.contract" },
            value => value with { ContractVersion = "1.0.1" }, value => value with { DescriptorSha256 = new string('a', 64) },
            value => value with { AbiSha256 = new string('b', 64) }, value => value with { ActivationId = Guid.NewGuid() },
            value => value with { ActivationEpoch = value.ActivationEpoch + 1 }, value => value with { SessionGeneration = value.SessionGeneration + 1 },
            value => value with { Endpoint = new("different-endpoint") }, value => value with { State = SunderRpcProviderState.Inactive },
        })
        {
            var before = peer.GenerationCount;
            observer.Transform = change;
            var failure = await Assert.ThrowsAsync<SunderRpcException>(() => client.GenerateAsync("embedding/model", "stale").AsTask());
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, failure.Error.Kind);
            Assert.Equal(before, peer.GenerationCount);
        }
        observer.Transform = null;
        observer.InvalidateAfterInvoke = true;
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() =>
            client.GenerateAsync("embedding/model", "stale result").AsTask())).Error.Kind);
        observer.InvalidateAfterInvoke = false; observer.Transform = null;
        foreach (var kind in new[] { SunderRpcErrorKind.PermissionDenied, SunderRpcErrorKind.Unavailable })
        {
            observer.LookupFailure = kind;
            Assert.Equal(kind, (await Assert.ThrowsAsync<SunderRpcException>(() => client.GetReadinessAsync().AsTask())).Error.Kind);
        }
        observer.LookupFailure = null;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); var before = peer.GenerationCount;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GenerateAsync("embedding/model", "cancelled", cancelled.Token).AsTask());
            Assert.Equal(before, peer.GenerationCount);
        }

        {
            var historyType = owner.GetType("Sunder.Package.Agent.HistorySearch.HistoryEmbeddingProviderCatalog", true)!;
            var history = AgentRoleBoundary.Create(historyType, catalog);
            var resolve = historyType.GetMethod("ResolveSelectionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var pending = (Task)resolve.Invoke(history, [selected.PackageId, "embedding", "embedding/model", CancellationToken.None])!;
            await pending;
            var selection = pending.GetType().GetProperty("Result")!.GetValue(pending)!;
            Assert.Equal(64, ((string)selection.GetType().GetProperty("SpaceFingerprint")!.GetValue(selection)!).Length);
            var owned = selection.GetType().GetProperty("OwnedProvider")!.GetValue(selection)!;
            var historyService = EmbeddingService(owned.GetType().GetProperty("Provider")!.GetValue(owned)!);
            Assert.Equal(AgentRoleBoundary.Unwrap(client).GetType(), AgentRoleBoundary.Unwrap(historyService).GetType());
            var results = await historyService.GenerateBatchAsync("embedding/model", ["history", ""]);
            Assert.Equal(2, results.Count); Assert.Null(results[1]);
        }

        peer.Block = true;
        var active = client.GenerateAsync("embedding/model", "retire").AsTask();
        await peer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RetireAsync(registration.Handler).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => active)).Error.Kind);
        Assert.True(peer.Cancelled);
        var replacement = new MemoryConsumerEmbedding("Replacement Agent embeddings");
        Register(replacement);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.GetReadinessAsync().AsTask())).Error.Kind);
        var currentCatalog = CreateAgentEmbeddingCatalog(owner, observer);
        await using var currentLifetime = (IAsyncDisposable)currentCatalog;
        var current = EmbeddingService(await SingleAgentEmbeddingProvider(currentCatalog));
        Assert.Equal("Replacement Agent embeddings", (await current.DescribeAsync()).DisplayName);
        replacement.Block = true;
        var stoppingCall = current.GenerateAsync("embedding/model", "shutdown").AsTask();
        await replacement.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = currentLifetime.DisposeAsync().AsTask();
        Assert.Same(stop, currentLifetime.DisposeAsync().AsTask());
        var ended = await Assert.ThrowsAsync<SunderRpcException>(() => stoppingCall);
        Assert.Equal(SunderRpcErrorKind.Unavailable, ended.Error.Kind);
        Assert.Equal("rpc.binding.retired", ended.Error.Code);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, ended.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, ended.Error.RetrySafety);
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(replacement.Cancelled); Assert.All(observer.Scopes, scope => Assert.True(scope.Disposed));
        observer.AssertScopedOperations();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => current.DescribeAsync().AsTask());

        SunderWorkerProviderRegistration Register(MemoryConsumerEmbedding value)
        {
            var receiver = new GeneratedServiceTestScope(new RegressionTestRpcClient(state, "example.embedding"));
            try
            {
                using var providers = new RegressionTestSourceProviders();
                var registered = providers.Create(RegressionTestBindings.EmbeddingProviders, value, receiver);
                state.AddGenerated("example.embedding", registered.Handler, registered,
                    new GeneratedPeerLifetime(receiver), registered.ProviderId);
                return registered;
            }
            catch { receiver.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
        }
    }
}
