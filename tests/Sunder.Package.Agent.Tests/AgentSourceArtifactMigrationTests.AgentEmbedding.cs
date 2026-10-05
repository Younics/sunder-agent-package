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
    private static IAgentEmbeddingProvider CreateAgentEmbeddingClient(Assembly owner, ISunderRpcClient rpc, SunderRpcProviderSnapshot provider)
        => AgentRoleBoundary.As<IAgentEmbeddingProvider>(Activator.CreateInstance(owner.GetType("Sunder.Package.Agent.Runtime.Rpc.GeneratedEmbeddingProviderClient", true)!, rpc, provider)!);

    private static async Task VerifyAgentEmbeddingConsumer(Assembly owner, Assembly providerAssembly, Assembly probes)
    {
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var peer = new MemoryConsumerEmbedding("Generated Agent embeddings");
        var registration = Register(peer);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        var selected = Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.EmbeddingProvider)).Providers);
        var observer = new FilesRpcObserver(rpc, "generate");
        var client = CreateAgentEmbeddingClient(owner, observer, selected);
        await using var lifetime = (IAsyncDisposable)client;
        Assert.Equal("Generated Agent embeddings", client.Descriptor.DisplayName);
        Assert.Equal(3, Assert.Single(await client.GetAvailableModelsAsync()).Dimensions);
        Assert.Equal(AgentProviderReadinessStatus.Ready, (await client.GetReadinessAsync()).Status);
        Assert.Equal("stable-space", await ((IAgentEmbeddingSpaceIdentityProvider)client).GetEmbeddingSpaceIdentityAsync("embedding/model"));
        Assert.Null(await client.GenerateEmbeddingAsync("embedding/model", ""));
        var batch = await client.GenerateEmbeddingsAsync("embedding/model", ["first", "", "third"]);
        Assert.Equal(3, batch.Count); Assert.Null(batch[1]); Assert.Equal(new double[] { 1, 0, 0 }, batch[2]!.Values);
        Assert.Equal(0, observer.UnscopedLookups); Assert.All(observer.Scopes, scope => Assert.True(scope.Disposed));

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
            var failure = await Assert.ThrowsAsync<SunderRpcException>(() => client.GenerateEmbeddingAsync("embedding/model", "stale").AsTask());
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, failure.Error.Kind);
            Assert.Equal(before, peer.GenerationCount);
        }
        observer.Transform = null;
        observer.InvalidateAfterInvoke = true;
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() =>
            client.GenerateEmbeddingAsync("embedding/model", "stale result").AsTask())).Error.Kind);
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
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GenerateEmbeddingAsync("embedding/model", "cancelled", cancelled.Token).AsTask());
            Assert.Equal(before, peer.GenerationCount);
        }

        using (var catalog = AgentRpcCatalog.CreateDormant(rpc))
        {
            var historyType = owner.GetType("Sunder.Package.Agent.HistorySearch.HistoryEmbeddingProviderCatalog", true)!;
            var history = AgentRoleBoundary.Create(historyType, catalog);
            var resolve = historyType.GetMethod("ResolveSelectionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var pending = (Task)resolve.Invoke(history, [selected.PackageId, "embedding", "embedding/model", CancellationToken.None])!;
            await pending;
            var selection = pending.GetType().GetProperty("Result")!.GetValue(pending)!;
            Assert.Equal(64, ((string)selection.GetType().GetProperty("SpaceFingerprint")!.GetValue(selection)!).Length);
            var owned = selection.GetType().GetProperty("OwnedProvider")!.GetValue(selection)!;
            var reference = AgentRoleBoundary.As<AgentRpcReference<IAgentEmbeddingProvider>>(owned.GetType().GetProperty("Reference")!.GetValue(owned)!);
            Assert.True(reference.TryAcquire(out var lease));
            using (lease) Assert.Equal(AgentRoleBoundary.Unwrap(client).GetType(), AgentRoleBoundary.Unwrap(lease.Service).GetType());
            var generate = historyType.GetMethod("GenerateEmbeddingsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var values = AgentRoleBoundary.As<ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>>>(generate.Invoke(history, [selection, new[] { "history", "" }, CancellationToken.None])!);
            var results = await values; Assert.Equal(2, results.Count); Assert.Null(results[1]);
        }

        peer.Block = true;
        var active = client.GenerateEmbeddingAsync("embedding/model", "retire").AsTask();
        await peer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RetireAsync(registration.Handler).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => active)).Error.Kind);
        Assert.True(peer.Cancelled);
        var replacement = new MemoryConsumerEmbedding("Replacement Agent embeddings");
        Register(replacement);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.GetReadinessAsync().AsTask())).Error.Kind);
        var next = Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.EmbeddingProvider)).Providers);
        var current = CreateAgentEmbeddingClient(owner, observer, next);
        await using var currentLifetime = (IAsyncDisposable)current;
        Assert.Equal("Replacement Agent embeddings", current.Descriptor.DisplayName);
        replacement.Block = true;
        var stoppingCall = current.GenerateEmbeddingAsync("embedding/model", "shutdown").AsTask();
        await replacement.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = currentLifetime.DisposeAsync().AsTask();
        Assert.Same(stop, currentLifetime.DisposeAsync().AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stoppingCall);
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(replacement.Cancelled); Assert.All(observer.Scopes, scope => Assert.True(scope.Disposed));
        Assert.Equal(0, observer.UnscopedLookups);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, Assert.Throws<SunderRpcException>(() => current.Descriptor).Error.Kind);

        SunderWorkerProviderRegistration Register(MemoryConsumerEmbedding value)
        {
            var type = providerAssembly.GetType("Sunder.Package.Agent.Provider.Gemini.Rpc.EmbeddingProvider", true)!;
            Func<string, string, CancellationToken, ValueTask<string>> invoke = async (operation, json, token) =>
            {
                using var request = JsonDocument.Parse(json);
                var input = request.RootElement;
                object? result = operation switch
                {
                    "describe" => value.Descriptor,
                    "models" => await value.GetAvailableModelsAsync(token),
                    "readiness" => await value.GetReadinessAsync(token),
                    "space" => await value.GetEmbeddingSpaceIdentityAsync(input.GetProperty("modelId").GetString()!, token),
                    "generate" => await value.GenerateEmbeddingAsync(input.GetProperty("modelId").GetString()!, input.GetProperty("text").GetString()!, token),
                    "batch" => await value.GenerateEmbeddingsAsync(input.GetProperty("modelId").GetString()!, input.GetProperty("texts").EnumerateArray().Select(text => text.GetString()!).ToArray(), token),
                    _ => throw new InvalidOperationException("Unknown embedding probe operation."),
                };
                return JsonSerializer.Serialize(result, MemoryProviderJson);
            };
            var local = Activator.CreateInstance(probes.GetType("RoleLocalEmbeddingProbe", true)!, invoke)!;
            var provider = Activator.CreateInstance(type, local)!;
            var registered = (SunderWorkerProviderRegistration)type.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(provider, null)!;
            state.Add("example.embedding", RegressionTestRpcDescriptors.Get(AgentRpcContractIds.EmbeddingProvider), registered.Handler,
                registered.Handler, Noop.Instance, registered.ProviderId, "2.0.0");
            return registered;
        }
    }
}
