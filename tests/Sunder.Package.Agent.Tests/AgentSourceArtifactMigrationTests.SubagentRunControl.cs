using System.Collections.Concurrent;
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
    private static async Task VerifySubagentRunControlConsumer(Type clientType, Assembly agentOwner)
    {
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var transport = new RunControlConsumerRpc(new RegressionTestRpcClient(state, "sunder.package.agent.subagents"));
        var registry = new AgentRoleRunControls(agentOwner);
        var host = new AgentPublicSourceMigrationTests.FullRunControlProbe();
        AddSourceRunControl(state, agentOwner, registry);
        var handle = ToProviderHandle(Assert.Single(state.Discover(AgentRpcContractIds.RunControl).Providers));
        using var registration = registry.Open(host, "sunder.package.agent.subagents", out var invocationId);
        var client = Create(handle, invocationId, DateTimeOffset.UtcNow.AddHours(1));
        Assert.Empty(transport.Scopes);
        var before = DateTimeOffset.UtcNow;
        var result = await client.RunDefaultLoopAsync(host.Context);
        Assert.Equal(AgentBehaviorLoopCompletionKind.WaitingForApproval, result.CompletionKind);
        Assert.Equal(host.Context.RunningCheckpoint, result.Checkpoint);
        var firstScope = Assert.Single(transport.Scopes);
        Assert.InRange(firstScope.DeadlineUtc - before, TimeSpan.FromMinutes(34), TimeSpan.FromMinutes(36));
        Assert.True(firstScope.Disposed);
        Assert.Equal(host.Context.UserMessage, host.Arguments.GetProperty("context").GetProperty("userMessage").GetString());
        Assert.Equal(host.Context.RunStartedAtUtc, host.Arguments.GetProperty("context").GetProperty("runStartedAtUtc").GetDateTimeOffset());
        var shorter = DateTimeOffset.UtcNow.AddMinutes(2);
        await Create(handle, invocationId, shorter).RunDefaultLoopAsync(host.Context with
        { Workspace = null, ExecutionBinding = null, ModelVariant = null, ModelModeOption = null, ModelSpeedOption = null });
        Assert.Contains(transport.Scopes, scope => scope.DeadlineUtc == shorter && scope.Disposed);

        var calls = host.Calls;
        foreach (var stale in new[]
        {
            handle with { PackageId = "different.package" }, handle with { PackageVersion = "2.0.1" }, handle with { ProviderId = "different.provider" },
            handle with { ContractId = "different.contract" }, handle with { ContractVersion = "9.0.0" }, handle with { DescriptorSha256 = new('0', 64) },
            handle with { AbiSha256 = new('0', 64) }, handle with { ActivationId = Guid.NewGuid() }, handle with { ActivationEpoch = handle.ActivationEpoch + 1 },
            handle with { SessionGeneration = handle.SessionGeneration + 1 }, handle with { EndpointReference = "test.rpc.missing" },
        })
        {
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => Create(stale, invocationId).RunDefaultLoopAsync(host.Context).AsTask());
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, error.Error.Kind);
            Assert.Equal(calls, host.Calls);
        }
        using (var wrongCaller = registry.Open(host, "different.caller", out var wrongId))
        {
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, (await Assert.ThrowsAsync<SunderRpcException>(() => Create(handle, wrongId).RunDefaultLoopAsync(host.Context).AsTask())).Error.Kind);
            Assert.Equal("agent.run-control.invalid-invocation", Assert.IsType<SunderRpcException>(state.LastInvocationFailure).Error.Code);
            Assert.Equal(calls, host.Calls);
        }
        var closed = registry.Open(host, "sunder.package.agent.subagents", out var closedId);
        closed.Dispose();
        await Assert.ThrowsAsync<SunderRpcException>(() => Create(handle, closedId).RunDefaultLoopAsync(host.Context).AsTask());
        Assert.Equal(calls, host.Calls);
        using (registry.Open(new AgentPublicSourceMigrationTests.RunControlProbe(), "sunder.package.agent.subagents", out var unsupported))
        {
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => Create(handle, unsupported).RunDefaultLoopAsync(host.Context).AsTask());
            Assert.Equal(SunderRpcErrorKind.Domain, error.Error.Kind);
            Assert.Equal("agent.run-control.default-loop-unavailable", error.Error.Code);
        }
        host.Failure = new IOException("private callback failure");
        var fault = await Assert.ThrowsAsync<SunderRpcException>(() => client.RunDefaultLoopAsync(host.Context).AsTask());
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, fault.Error.Kind);
        Assert.DoesNotContain("private callback failure", fault.Message, StringComparison.Ordinal);
        host.Failure = null;
        foreach (var failure in new[] { SunderRpcErrorKind.PermissionDenied, SunderRpcErrorKind.Unavailable })
        {
            transport.LookupFailure = failure;
            calls = host.Calls;
            Assert.Equal(failure, (await Assert.ThrowsAsync<SunderRpcException>(() => client.RunDefaultLoopAsync(host.Context).AsTask())).Error.Kind);
            Assert.Equal(calls, host.Calls);
        }
        transport.LookupFailure = null;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var scopes = transport.Scopes.Count;
            calls = host.Calls;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RunDefaultLoopAsync(host.Context, cancelled.Token).AsTask());
            Assert.Equal(scopes, transport.Scopes.Count);
            Assert.Equal(calls, host.Calls);
        }
        host.Block = true;
        host.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var cancelled = new CancellationTokenSource())
        {
            var pending = client.RunDefaultLoopAsync(host.Context, cancelled.Token).AsTask();
            await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        var expired = Create(handle, invocationId, DateTimeOffset.UtcNow.AddMilliseconds(250));
        var deadlineFailure = await Record.ExceptionAsync(() => expired.RunDefaultLoopAsync(host.Context).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(deadlineFailure is OperationCanceledException || deadlineFailure is SunderRpcException { Error.Kind: SunderRpcErrorKind.DeadlineExceeded });
        host.Block = false;
        transport.AfterInvoke = () => transport.LookupTransform = value => value with { SessionGeneration = value.SessionGeneration + 1 };
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.RunDefaultLoopAsync(host.Context).AsTask())).Error.Kind);
        transport.AfterInvoke = null;
        transport.LookupTransform = null;

        host.Block = true;
        host.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var retiring = client.RunDefaultLoopAsync(host.Context).AsTask();
        await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RetireAsync(registry).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => retiring.WaitAsync(TimeSpan.FromSeconds(5)))).Error.Kind);
        var replacementRegistry = new AgentRoleRunControls(agentOwner);
        var replacement = new AgentPublicSourceMigrationTests.FullRunControlProbe();
        AddSourceRunControl(state, agentOwner, replacementRegistry);
        using var replacementRegistration = replacementRegistry.Open(replacement, "sunder.package.agent.subagents", out var replacementId);
        await Assert.ThrowsAsync<SunderRpcException>(() => client.RunDefaultLoopAsync(host.Context).AsTask());
        Assert.Equal(0, replacement.Calls);
        var replacementHandle = ToProviderHandle(Assert.Single(state.Discover(AgentRpcContractIds.RunControl).Providers));
        Assert.Equal(AgentBehaviorLoopCompletionKind.WaitingForApproval,
            (await Create(replacementHandle, replacementId).RunDefaultLoopAsync(replacement.Context)).CompletionKind);
        Assert.All(transport.Scopes, scope => Assert.True(scope.Disposed));

        SubagentRunControlProbe Create(AgentRpcProviderHandle selected, Guid id, DateTimeOffset? deadline = null)
        {
            var handleType = Assert.Single(clientType.GetConstructors()).GetParameters()[2].ParameterType;
            var wireHandle = JsonSerializer.SerializeToElement(selected, MemoryProviderJson).Deserialize(handleType, MemoryProviderJson)!;
            return new(Activator.CreateInstance(clientType, transport, id, wireHandle, deadline ?? DateTimeOffset.UtcNow.AddMinutes(35))!);
        }
    }

    private static void AddSourceRunControl(RegressionTestRpcState state, Assembly agentOwner, AgentRoleRunControls registry)
    {
        var type = agentOwner.GetType("Sunder.Package.Agent.Rpc.Public.RunControlService", true)!;
        var provider = Activator.CreateInstance(type, registry.Value)!;
        var registration = Assert.IsType<SunderWorkerProviderRegistration>(type.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(provider, null));
        var descriptor = RegressionTestRpcDescriptors.Get(AgentRpcContractIds.RunControl);
        Assert.Equal(descriptor.Sha256, registration.DescriptorSha256);
        Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
        state.Add("sunder.package.agent", descriptor, registry, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
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
        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default) => throw Unscoped();
        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default) => throw Unscoped();
        public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default) => throw Unscoped();
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
