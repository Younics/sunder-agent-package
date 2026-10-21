using System.Collections.Concurrent;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifySubagentRunControlConsumer(System.Reflection.Assembly agentOwner)
    {
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var transport = new RunControlConsumerRpc(new RegressionTestRpcClient(state, "younics.ai.agent.subagents"));
        var activeRuns = new AgentActiveRunRegistry();
        var registry = new AgentRoleRunControls(agentOwner, activeRuns);
        var implementation = new AgentRunControlService(activeRuns);
        await using var receiver = new GeneratedServiceTestScope(transport);
        var registration = receiver.Register(implementation, RegressionTestBindings.RunControls);
        var descriptor = CurrentPublicRpcDescriptors.Get(RegressionTestBindings.RunControls);
        state.Add("younics.ai.agent", descriptor, implementation, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
        var client = Assert.Single((await receiver.Providers<AgentServices.IRunControlService>().ListAsync()).Providers).Service;
        var host = new AgentPublicSourceMigrationTests.FullRunControlProbe();
        using var lease = registry.Open(host, "younics.ai.agent.subagents", out var invocationId);

        var before = DateTimeOffset.UtcNow;
        var result = await client.RunDefaultAsync(invocationId, host.Context);
        Assert.Equal(AgentBehaviorLoopCompletionKind.WaitingForApproval, result.CompletionKind);
        Assert.Equal(host.Context.RunningCheckpoint, result.Checkpoint);
        var firstScope = Assert.Single(transport.Scopes);
        Assert.InRange(firstScope.DeadlineUtc - before, TimeSpan.FromMinutes(34), TimeSpan.FromMinutes(36));
        Assert.True(firstScope.Disposed);
        Assert.Equal(host.Context.UserMessage, host.Arguments.GetProperty("context").GetProperty("userMessage").GetString());
        Assert.Equal(host.Context.RunStartedAtUtc, host.Arguments.GetProperty("context").GetProperty("runStartedAtUtc").GetDateTimeOffset());

        using (var wrongCaller = registry.Open(host, "different.caller", out var wrongId))
        {
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => client.RunDefaultAsync(wrongId, host.Context).AsTask());
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, error.Error.Kind);
            Assert.Equal("agent.run-control.invalid-invocation", Assert.IsType<SunderRpcException>(state.LastInvocationFailure).Error.Code);
        }
        var closed = registry.Open(host, "younics.ai.agent.subagents", out var closedId);
        closed.Dispose();
        await Assert.ThrowsAsync<SunderRpcException>(() => client.RunDefaultAsync(closedId, host.Context).AsTask());
        using (registry.Open(new AgentPublicSourceMigrationTests.RunControlProbe(), "younics.ai.agent.subagents", out var unsupported))
        {
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => client.RunDefaultAsync(unsupported, host.Context).AsTask());
            Assert.Equal(SunderRpcErrorKind.Domain, error.Error.Kind);
            Assert.Equal("agent.run-control.default-loop-unavailable", error.Error.Code);
        }
        host.Failure = new IOException("private callback failure");
        var fault = await Assert.ThrowsAsync<SunderRpcException>(() => client.RunDefaultAsync(invocationId, host.Context).AsTask());
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, fault.Error.Kind);
        Assert.DoesNotContain("private callback failure", fault.Message, StringComparison.Ordinal);
        host.Failure = null;
        foreach (var failure in new[] { SunderRpcErrorKind.PermissionDenied, SunderRpcErrorKind.Unavailable })
        {
            transport.LookupFailure = failure;
            await Assert.ThrowsAsync<SunderRpcException>(() => client.RunDefaultAsync(invocationId, host.Context).AsTask());
        }
        transport.LookupFailure = null;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var scopes = transport.Scopes.Count;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RunDefaultAsync(invocationId, host.Context, cancelled.Token).AsTask());
            Assert.Equal(scopes, transport.Scopes.Count);
        }
        host.Block = true;
        host.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var cancelled = new CancellationTokenSource())
        {
            var pending = client.RunDefaultAsync(invocationId, host.Context, cancelled.Token).AsTask();
            await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        host.Block = false;
        transport.AfterInvoke = () => transport.LookupTransform = value => value with { SessionGeneration = value.SessionGeneration + 1 };
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.RunDefaultAsync(invocationId, host.Context).AsTask())).Error.Kind);
        transport.AfterInvoke = null;
        transport.LookupTransform = null;

        host.Block = true;
        host.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var retiring = client.RunDefaultAsync(invocationId, host.Context).AsTask();
        await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RetireAsync(implementation).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => retiring.WaitAsync(TimeSpan.FromSeconds(5)))).Error.Kind);
        Assert.All(transport.Scopes, scope => Assert.True(scope.Disposed));
    }

    private sealed class RunControlConsumerRpc(ISunderRpcClient inner) : ISunderRpcClient
    {
        internal SunderRpcErrorKind? LookupFailure;
        internal Func<SunderRpcProviderSnapshot, SunderRpcProviderSnapshot>? LookupTransform;
        internal Action? AfterInvoke;
        internal ConcurrentBag<Scope> Scopes { get; } = [];
        public async ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        {
            Assert.NotNull(options?.DeadlineUtc);
            var scope = new Scope(this, await inner.CreateCallScopeAsync(options, cancellationToken));
            Scopes.Add(scope);
            return scope;
        }
        // Receiver-owned metadata observation is independent of call scopes.
        // Actual callback dispatch below must still use an invocation-owned scope.
        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default) => inner.GetProviderAsync(endpoint, cancellationToken);
        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default) => inner.DiscoverAsync(contractId, cancellationToken);
        public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default) => inner.WatchAsync(afterRevision, afterSequence, cancellationToken);
        public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default) => throw Unscoped();
        public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default) => throw Unscoped();
        public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default) => throw Unscoped();
        private static InvalidOperationException Unscoped() => new("Run-control callbacks must use their invocation-owned scope.");

        internal sealed class Scope(RunControlConsumerRpc owner, ISunderRpcCallScope inner) : ISunderRpcCallScope
        {
            internal bool Disposed;
            public DateTimeOffset DeadlineUtc => inner.DeadlineUtc;
            public async ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
            {
                if (owner.LookupFailure is { } kind) throw new SunderRpcException(new(kind, "test.run-control.lookup", "Test callback lookup failed."));
                var value = await inner.GetProviderAsync(endpoint, cancellationToken);
                return value is not null && owner.LookupTransform is not null ? owner.LookupTransform(value) : value;
            }
            public async ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
            {
                Assert.Equal("run-control", serviceId); Assert.Equal("run-default", methodId);
                var response = await inner.InvokeAsync(endpoint, serviceId, methodId, request, options, cancellationToken);
                owner.AfterInvoke?.Invoke();
                return response;
            }
            public async ValueTask DisposeAsync() { await inner.DisposeAsync(); Disposed = true; }
            public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default) => inner.TryReportInvariantViolationAsync(endpoint, exception, cancellationToken);
            public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<SunderRpcContentReference> RegisterContentAsync(SunderRpcEndpointReference endpoint, Stream source, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(SunderRpcEndpointReference endpoint, string filePath, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<Stream> OpenContentAsync(SunderRpcContentReference reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
    }
}
