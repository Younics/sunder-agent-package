using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifySelectableStreamEdges(Assembly owner, Assembly skills, Assembly probes)
    {
        foreach (var mode in new[] { "normal", "finite", "fault", "invalid", "cancel", "stop" })
        {
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            var peer = new SelectablePeer();
            var registered = AddSelectableProvider(skills, probes, state, peer, mode);
            var rpc = new FilesRpcObserver(new RegressionTestRpcClient(state, "sunder.package.agent"), "list");
            var snapshot = Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.SelectableCapabilityProvider)).Providers);
            var client = CreateAgentSelectableClient(owner, rpc, snapshot);
            await using var lifetime = (IAsyncDisposable)client;
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var notifications = 0;
            ((IAgentProfileSelectableCapabilityChangeNotifier)client).SelectableCapabilitiesChanged += () =>
            { Interlocked.Increment(ref notifications); changed.TrySetResult(); };
            await registered.Probe.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (mode is "finite" or "fault" or "invalid")
            {
                registered.Probe.Release.TrySetResult();
                await Observation(client).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(mode == "finite" ? 1 : 0, Volatile.Read(ref notifications));
                Assert.Equal(0, registered.Probe.ActiveStreams);
                Assert.All(rpc.Scopes, scope => Assert.True(scope.Disposed));
                continue;
            }
            Assert.Equal(1, peer.Subscribers);
            Assert.Equal(peer.ProviderId, client.ProviderId); Assert.Equal(peer.DisplayName, client.DisplayName);
            Assert.Equal(peer.Values, await client.ListCapabilitiesAsync(new(null)));
            if (mode == "normal")
            {
                peer.Notify(); await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var before = peer.ListCalls;
                rpc.Transform = value => value with { AbiSha256 = new string('a', 64) };
                Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.ListCapabilitiesAsync(new(null)).AsTask())).Error.Kind);
                Assert.Equal(before, peer.ListCalls);
                rpc.Transform = null;
                rpc.LookupFailure = SunderRpcErrorKind.PermissionDenied;
                Assert.Equal(SunderRpcErrorKind.PermissionDenied, (await Assert.ThrowsAsync<SunderRpcException>(() => client.ListCapabilitiesAsync(new(null)).AsTask())).Error.Kind);
                rpc.LookupFailure = null;
                rpc.InvalidateAfterInvoke = true;
                Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.ListCapabilitiesAsync(new(null)).AsTask())).Error.Kind);
                rpc.InvalidateAfterInvoke = false;
                var eventsBefore = Volatile.Read(ref notifications);
                // The same mismatch is now observed before forwarding a stream event.
                peer.Notify();
                await Observation(client).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(eventsBefore, Volatile.Read(ref notifications));
                Assert.Equal(0, peer.Subscribers);
            }
            else
            {
                peer.ListGate = new();
                using var cancellation = new CancellationTokenSource();
                var pending = client.ListCapabilitiesAsync(new(null), cancellation.Token).AsTask();
                await peer.ListGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (mode == "cancel")
                {
                    cancellation.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                    Assert.Equal(1, peer.Subscribers);
                    peer.Notify(); await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                else
                {
                    var stopping = lifetime.DisposeAsync().AsTask();
                    Assert.Same(stopping, lifetime.DisposeAsync().AsTask());
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                    await stopping.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            await lifetime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(Observation(client).IsCompletedSuccessfully);
            Assert.Equal(0, peer.Subscribers); Assert.Equal(0, registered.Probe.ActiveStreams);
            Assert.Equal(0, rpc.UnscopedLookups); Assert.All(rpc.Scopes, scope => Assert.True(scope.Disposed));
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, Assert.Throws<SunderRpcException>(() => client.ProviderId).Error.Kind);
        }
    }

    private sealed class SelectableHandlerProbe(ISunderRpcServiceHandler inner, string mode = "normal") : ISunderRpcServiceHandler, ISunderRpcDeclaredErrorMetadata
    {
        internal TaskCompletionSource Subscribed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ActiveStreams;
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> DeclaredErrors => ((ISunderRpcDeclaredErrorMetadata)inner).DeclaredErrors;
        public ValueTask<JsonElement> InvokeUnaryAsync(SunderRpcInvocationContext context, string serviceId, string methodId, JsonElement request, CancellationToken token = default)
            => inner.InvokeUnaryAsync(context, serviceId, methodId, request, token);
        public async IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(SunderRpcInvocationContext context, string serviceId, string methodId,
            JsonElement request, [EnumeratorCancellation] CancellationToken token = default)
        {
            Interlocked.Increment(ref ActiveStreams);
            try
            {
                if (mode is "finite" or "fault" or "invalid")
                {
                    Subscribed.TrySetResult();
                    await Release.Task.WaitAsync(token);
                    if (mode == "fault") throw new IOException("private-watch-failure");
                    yield return JsonSerializer.SerializeToElement(new { revision = mode == "invalid" ? -1 : 1 });
                    yield break;
                }
                await using var events = inner.InvokeServerStreamAsync(context, serviceId, methodId, request, token).GetAsyncEnumerator(token);
                while (true)
                {
                    var next = events.MoveNextAsync();
                    Subscribed.TrySetResult();
                    if (!await next.ConfigureAwait(false)) yield break;
                    yield return events.Current;
                }
            }
            finally { Interlocked.Decrement(ref ActiveStreams); }
        }
    }
}
