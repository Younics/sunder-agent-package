using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifySelectableStreamEdges(Assembly owner)
    {
        foreach (var mode in new[] { "normal", "finite", "fault", "invalid", "cancel", "stop" })
        {
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            var peer = new SelectablePeer();
            var registered = AddSelectableProvider(state, peer, mode);
            var rpc = new FilesRpcObserver(new RegressionTestRpcClient(state, "younics.ai.agent"), "list");
            await using var receiver = new GeneratedServiceTestScope(rpc);
            var selected = Assert.Single(await ProjectedProviders(receiver, owner, "ISelectableCapabilityProviderService", imported: false));
            using var client = selected.Service;
            await using var watch = new ProjectedArtifactWatch(client.Value);
            Task<AgentProfileSelectableCapabilityDescriptor[]> List(CancellationToken token = default)
                => client.InvokeAsync<AgentProfileSelectableCapabilityDescriptor[]>("ListAsync", null, token).AsTask();
            var next = watch.Next();
            await registered.Probe.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (mode is "finite" or "fault" or "invalid")
            {
                registered.Probe.Release.TrySetResult();
                if (mode == "finite")
                {
                    Assert.True(await next);
                    Assert.Equal(1, watch.Current.GetProperty("revision").GetInt64());
                    Assert.False(await watch.Next());
                }
                else
                {
                    var failure = await Assert.ThrowsAsync<SunderRpcException>(() => next);
                    Assert.Equal(SunderRpcErrorKind.ProviderFaulted, failure.Error.Kind);
                    Assert.DoesNotContain("private-watch-failure", failure.Message, StringComparison.Ordinal);
                }
                Assert.Equal(0, registered.Probe.ActiveStreams);
                Assert.All(rpc.Scopes, scope => Assert.True(scope.Disposed));
                continue;
            }
            Assert.True(await next);
            Assert.Equal(1, peer.Subscribers);
            var identity = await client.InvokeAsync<AgentServiceIdentity>("DescribeAsync", CancellationToken.None);
            Assert.Equal(peer.ProviderId, identity.Id); Assert.Equal(peer.DisplayName, identity.DisplayName);
            Assert.Equal(peer.Values, await List());
            if (mode == "normal")
            {
                var changed = watch.Next(); peer.Notify(); Assert.True(await changed);
                var before = peer.ListCalls;
                rpc.Transform = value => value with { AbiSha256 = new string('a', 64) };
                Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => List())).Error.Kind);
                Assert.Equal(before, peer.ListCalls);
                rpc.Transform = null;
                rpc.LookupFailure = SunderRpcErrorKind.PermissionDenied;
                Assert.Equal(SunderRpcErrorKind.PermissionDenied, (await Assert.ThrowsAsync<SunderRpcException>(() => List())).Error.Kind);
                rpc.LookupFailure = null;
                rpc.InvalidateAfterInvoke = true;
                Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => List())).Error.Kind);
                rpc.InvalidateAfterInvoke = false;
                // The same mismatch is now observed before forwarding a stream event.
                var rejected = watch.Next();
                peer.Notify();
                Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => rejected)).Error.Kind);
                Assert.Equal(0, peer.Subscribers);
            }
            else
            {
                peer.ListGate = new();
                using var cancellation = new CancellationTokenSource();
                var pending = List(cancellation.Token);
                await peer.ListGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (mode == "cancel")
                {
                    cancellation.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                    Assert.Equal(1, peer.Subscribers);
                    var changed = watch.Next(); peer.Notify(); Assert.True(await changed);
                }
                else
                {
                    var stoppedWatch = watch.Next();
                    var stopping = receiver.DisposeAsync().AsTask();
                    Assert.Same(stopping, receiver.DisposeAsync().AsTask());
                    var failure = await Assert.ThrowsAsync<SunderRpcException>(() => pending);
                    Assert.Equal(SunderRpcErrorKind.Unavailable, failure.Error.Kind);
                    Assert.Equal("rpc.binding.retired", failure.Error.Code);
                    Assert.Equal(SunderRpcErrorKind.Unavailable, (await Assert.ThrowsAsync<SunderRpcException>(() => stoppedWatch)).Error.Kind);
                    await stopping.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            await watch.DisposeAsync();
            await receiver.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, peer.Subscribers); Assert.Equal(0, registered.Probe.ActiveStreams);
            rpc.AssertScopedOperations(); Assert.All(rpc.Scopes, scope => Assert.True(scope.Disposed));
            var disposed = Assert.Throws<TargetInvocationException>(() => selected.Reference.GetType().GetProperty("Service")!.GetValue(selected.Reference));
            Assert.Equal(SunderRpcErrorKind.Unavailable, Assert.IsType<SunderRpcException>(disposed.InnerException).Error.Kind);
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
                    // Revision is a signed safe integer in the current contract;
                    // use an actual schema violation rather than a valid negative value.
                    yield return mode == "invalid"
                        ? JsonSerializer.SerializeToElement(new { revision = "invalid" })
                        : JsonSerializer.SerializeToElement(new { revision = 1 });
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
