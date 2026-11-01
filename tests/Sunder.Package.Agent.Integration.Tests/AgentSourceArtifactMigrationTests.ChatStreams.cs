using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyProviderStreamLifecycle(Assembly runtime, string family, Assembly owner, Assembly probes)
    {
        var id = family.ToLowerInvariant();
        var packageId = AgentPackageRepositoryInventory.GetRuntimePackageId("Sunder.Package.Agent.Provider." + family);
        var errorType = runtime.GetType("Sunder.Package.Agent.Contracts.Models.AgentChatProviderException", true)!;
        var probeType = probes.GetType("RoleLocalChatProbe", true)!;
        foreach (var mode in new[] { "flood", "cancel", "retire", "error", "unsafe", "queue", "fault", "complete", "cleanup-fault", "primary-fault", "error-cleanup" })
        {
            var probe = new ChatStreamProbe(mode, errorType);
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            using var providers = new RegressionTestSourceProviders();
            await using var receiver = new GeneratedServiceTestScope(new RegressionTestRpcClient(state, packageId));
            var provider = Activator.CreateInstance(probeType, id, (Func<IChatClient>)probe.CreateClient)!;
            var registration = providers.Create(RegressionTestBindings.ChatProviders, provider, receiver);
            state.AddGenerated(packageId, provider, registration, Noop.Instance, registration.ProviderId);
            var rpc = new RegressionTestRpcClient(state, "younics.ai.agent");
            var selected = Assert.Single((await rpc.DiscoverAsync(RegressionTestBindings.ChatProviders)).Providers);
            var endpoint = selected.Endpoint;
            var observed = new FilesRpcObserver(rpc);
            var binding = await CreateAgentChatProvider(owner, observed, selected);
            await using var bindingLifetime = (IAsyncDisposable)binding;
            using var client = await CreateAgentChatClientAsync(binding, new(id, id + "/test"));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var stream = client.GetStreamingResponseAsync([new(ChatRole.User, "probe")], cancellationToken: stop.Token).GetAsyncEnumerator(stop.Token);
            try
            {
                Assert.True(await stream.MoveNextAsync());
                Assert.InRange(stream.Current.Contents.Count, 1, 16);
                if (mode == "flood")
                {
                    Assert.InRange(Volatile.Read(ref probe.Produced), 64, 100);
                }
                else if (mode is "cancel" or "retire")
                {
                    var next = stream.MoveNextAsync().AsTask();
                    await probe.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (mode == "cancel") { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next); }
                    else
                    {
                        var retiring = state.RetireAsync(provider);
                        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => next)).Error.Kind);
                        await retiring.WaitAsync(TimeSpan.FromSeconds(5));
                        var replacementProbe = new ChatStreamProbe("complete", errorType);
                        var replacement = Activator.CreateInstance(probeType, id, (Func<IChatClient>)replacementProbe.CreateClient)!;
                        var added = providers.Create(RegressionTestBindings.ChatProviders, replacement, receiver);
                        state.AddGenerated(packageId, replacement, added, Noop.Instance, added.ProviderId);
                        var current = Assert.Single((await rpc.DiscoverAsync(RegressionTestBindings.ChatProviders)).Providers);
                        Assert.NotEqual(endpoint, current.Endpoint);
                        var replacementBinding = await CreateAgentChatProvider(owner, observed, current);
                        await using var replacementLifetime = (IAsyncDisposable)replacementBinding;
                        using var replacementClient = await CreateAgentChatClientAsync(replacementBinding, new(id, id + "/test"));
                        var updates = new List<ChatResponseUpdate>();
                        await foreach (var update in replacementClient.GetStreamingResponseAsync([new(ChatRole.User, "replacement")], cancellationToken: stop.Token)) updates.Add(update);
                        Assert.Single(updates); Assert.Equal(1, replacementProbe.Disposed);
                        await using var stale = client.GetStreamingResponseAsync([new(ChatRole.User, "stale")], cancellationToken: stop.Token).GetAsyncEnumerator(stop.Token);
                        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(async () => await stale.MoveNextAsync())).Error.Kind);
                    }
                }
                else if (mode is "error" or "error-cleanup" or "unsafe" or "queue")
                {
                    var error = await AssertRoleChatFailure(owner, async () => await stream.MoveNextAsync());
                    Assert.Equal(mode is "error" or "error-cleanup" ? "probe.controlled" : mode == "queue" ? "rpc.worker.stream-queue-limit" : "rpc.worker.unsafe-dispatch", error.ErrorCode);
                    if (mode is "unsafe" or "queue") Assert.Equal(SunderRpcRetrySafety.Unsafe, Assert.IsType<SunderRpcException>(error.InnerException).Error.RetrySafety);
                }
                else if (mode is "fault" or "cleanup-fault" or "primary-fault")
                {
                    var error = await Assert.ThrowsAsync<SunderRpcException>(async () => await stream.MoveNextAsync());
                    Assert.Equal(SunderRpcErrorKind.ProviderFaulted, error.Error.Kind);
                    Assert.DoesNotContain("private-failure", error.Message, StringComparison.Ordinal);
                    if (mode == "primary-fault") Assert.Equal("private-failure", state.LastInvocationFailure!.Message);
                }
                else Assert.False(await stream.MoveNextAsync());
            }
            finally { await stream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            Assert.Equal(1, probe.Disposed); Assert.True(probe.IteratorDisposed);
            observed.AssertScopedOperations(); Assert.All(observed.Scopes, scope => Assert.True(scope.Disposed));
            await state.RetireAsync(provider).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task<RoleChatFailure> AssertRoleChatFailure(Assembly owner, Func<Task> call)
    {
        var error = await Record.ExceptionAsync(call);
        Assert.NotNull(error);
        Assert.Equal(owner.GetType(typeof(AgentChatProviderException).FullName!, true), error.GetType());
        return new(error);
    }

    private sealed class RoleChatFailure(Exception error)
    {
        internal string? ErrorCode => (string?)error.GetType().GetProperty("ErrorCode")!.GetValue(error);
        internal string Content => (string)error.GetType().GetProperty("Content")!.GetValue(error)!;
        internal string Message => error.Message;
        internal Exception? InnerException => error.InnerException;
        internal AgentChatProviderFailureKind FailureKind => Enum.Parse<AgentChatProviderFailureKind>(error.GetType().GetProperty("FailureKind")!.GetValue(error)!.ToString()!);
        public override string ToString() => error.ToString();
    }

    private sealed class ChatStreamProbe(string mode, Type errorType)
    {
        internal int Produced;
        internal int Disposed;
        internal bool IteratorDisposed;
        internal TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IChatClient CreateClient() => new ProbeClient(this);
        private Exception Failure(Exception? inner) => (Exception)Activator.CreateInstance(errorType, "Controlled failure", "Controlled content", "probe.controlled", inner)!;

        private sealed class ProbeClient(ChatStreamProbe owner) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                try
                {
                    do
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Interlocked.Increment(ref owner.Produced);
                        yield return new(ChatRole.Assistant, "text");
                    } while (owner.Mode == "flood");
                    if (owner.Mode is "cancel" or "retire") { owner.Waiting.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                    if (owner.Mode is "fault" or "primary-fault") throw new IOException("private-failure");
                    if (owner.Mode is "error" or "error-cleanup" or "unsafe" or "queue")
                        throw owner.Failure(
                            owner.Mode is "error" or "error-cleanup" ? null : new AggregateException(new SunderRpcException(new(SunderRpcErrorKind.ResourceExhausted,
                                owner.Mode == "queue" ? "rpc.worker.stream-queue-limit" : "different.code", "Queue", SunderRpcDispatchState.PossiblyDispatched, SunderRpcRetrySafety.Unsafe))));
                }
                finally { owner.IteratorDisposed = true; }
            }
            public object? GetService(Type serviceType, object? serviceKey = null) => null;
            public void Dispose()
            {
                Interlocked.Increment(ref owner.Disposed);
                if (owner.Mode is "cleanup-fault" or "primary-fault" or "error-cleanup") throw new IOException("cleanup-failure");
            }
        }
        private string Mode => mode;
    }
}
