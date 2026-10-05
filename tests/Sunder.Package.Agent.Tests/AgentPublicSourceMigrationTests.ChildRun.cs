using System.Reflection;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    private static async Task VerifyChildRunsAsync(Assembly assembly, SunderRpcContractDescriptor descriptor)
    {
        var probe = new ChildRunProbe();
        await using var fixture = await CreateChildRunFixture(assembly, descriptor, probe);
        var context = CreateRunControlContext();
        var request = new AgentChildRunRequest(context.Session.SessionId, context.RunId, 0, "parent.call", "workspace.one",
            "task.one", context.Profile, "Child task 漢字", "Title", "custom-child");
        foreach (var status in Enum.GetValues<AgentRunStatus>())
        foreach (var present in new[] { false, true })
        {
            probe.Result = probe.Result with { Status = status, Content = present ? "Content 漢字" : null, Title = present ? "" : null };
            var json = RunJson(request);
            var expected = RunJson(probe.Result);
            var domainRequest = RunJson(request);
            var actual = await InvokeGeneratedClient(fixture.Client, "RunChildAsync", json);
            Assert.True(JsonElement.DeepEquals(expected, actual));
            Assert.True(JsonElement.DeepEquals(domainRequest, RunJson(probe.Request)));
            Assert.Equal(present, actual.TryGetProperty("title", out _));
            Assert.Equal(present, actual.TryGetProperty("content", out _));
        }
        var minimal = request with
        {
            TaskId = null, Title = "", AgentKind = "", ParentRunRevision = 9_007_199_254_740_991,
            ChildProfile = context.Profile with
            {
                ModelBindings = null, SelectableCapabilityAssignments = null, ChatProviderId = null, ChatModelId = null,
                EmbeddingProviderId = null, EmbeddingModelId = null, BehaviorLoopId = null, BehaviorLoopSourceId = null,
                BehaviorLoopSettingsJson = null, Description = null, Instructions = null, IsInternal = false,
            },
        };
        await InvokeGeneratedClient(fixture.Client, "RunChildAsync", RunJson(minimal));
        Assert.True(JsonElement.DeepEquals(RunJson(minimal), RunJson(probe.Request)));
        foreach (var invalid in new[] { request with { ParentRunRevision = -1 }, request with { TaskId = "" }, request with { ParentToolCallId = "" } })
        {
            var calls = probe.Calls;
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => InvokeGeneratedClient(fixture.Client, "RunChildAsync", RunJson(invalid)));
            Assert.Equal(SunderRpcErrorKind.Validation, error.Error.Kind);
            Assert.Equal(calls, probe.Calls);
        }
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            var calls = probe.Calls;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeGeneratedClient(fixture.Client, "RunChildAsync", RunJson(request), cancellation.Token));
            Assert.Equal(calls, probe.Calls);
        }
        using (var cancellation = new CancellationTokenSource())
        {
            probe.Block = true;
            var pending = InvokeGeneratedClient(fixture.Client, "RunChildAsync", RunJson(request), cancellation.Token);
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            probe.Block = false;
        }
        foreach (var failure in new Exception[]
        {
            new IOException("private child execution detail"),
            new SunderRpcException(new(SunderRpcErrorKind.Domain, "test.child.undeclared", "private child domain detail")),
        })
        {
            probe.Failure = failure;
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => InvokeGeneratedClient(fixture.Client, "RunChildAsync", RunJson(request)));
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, error.Error.Kind);
            Assert.DoesNotContain("private child", error.Message, StringComparison.Ordinal);
        }
        probe.Failure = null;
        var recorder = new ChildRunCallRecorder(fixture.Rpc);
        using var roles = new RegressionTestProviderRoles();
        var bindings = roles.CreateObject("Subagents", "Rpc.SubagentRuntimeBindings", recorder);
        await using var bindingLifetime = (IAsyncDisposable)bindings;
        await (ValueTask)AgentRoleBoundary.Call(bindings, "StartAsync", CancellationToken.None)!;
        var selected = Assert.Single(((System.Collections.IEnumerable)AgentRoleBoundary.Call(bindings, "GetChildRunExecutors")!).Cast<object>());
        object?[] acquisition = [null];
        Assert.True((bool)selected.GetType().GetMethod("TryAcquire")!.Invoke(selected, acquisition)!);
        using var lease = (IDisposable)acquisition[0]!;
        var bridge = AgentRoleBoundary.As<IAgentChildRunExecutor>(lease.GetType().GetProperty("Service")!.GetValue(lease)!);
        await bridge.RunChildAsync(request);
        Assert.True(JsonElement.DeepEquals(RunJson(request), RunJson(probe.Request)));
        Assert.InRange(recorder.Options!.DeadlineUtc!.Value - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(34), TimeSpan.FromMinutes(36));

        await VerifyChildRunPersistenceAsync(assembly, descriptor);
    }

    private static async Task<ChildRunFixture> CreateChildRunFixture(Assembly assembly, SunderRpcContractDescriptor descriptor, IAgentChildRunExecutor executor)
    {
        var type = assembly.GetType("Sunder.Package.Agent.Rpc.Public.ChildRunExecutorService", true)!;
        var implementation = Activator.CreateInstance(type, executor)!;
        var registration = Assert.IsType<SunderWorkerProviderRegistration>(type.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(implementation, null));
        Assert.Equal("agent.child.runs", registration.ProviderId);
        Assert.Equal(descriptor.Sha256, registration.DescriptorSha256);
        Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
        var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent.subagents");
        state.Add("sunder.package.agent", descriptor, implementation, registration.Handler, EmptyDisposable.Instance, registration.ProviderId, "2.0.0");
        var providers = (await rpc.DiscoverAsync(descriptor.ContractId)).Providers;
        var source = providers.Single(value => value.ProviderId == registration.ProviderId).Endpoint;
        var scope = await rpc.CreateCallScopeAsync(new(DateTimeOffset.UtcNow.AddMinutes(35)));
        var client = Activator.CreateInstance(assembly.GetType("Sunder.Generated.Rpc.ChildRunExecutor.ChildRunExecutorClient", true)!, scope, source)!;
        return new(state, rpc, scope, client, implementation, source);
    }

    private sealed class ChildRunFixture(RegressionTestRpcState state, RegressionTestRpcClient rpc, ISunderRpcCallScope scope,
        object client, object implementation, SunderRpcEndpointReference source) : IAsyncDisposable
    {
        public RegressionTestRpcState State { get; } = state;
        public RegressionTestRpcClient Rpc { get; } = rpc;
        public object Client { get; } = client;
        public object Implementation { get; } = implementation;
        public SunderRpcEndpointReference Source { get; } = source;
        public async Task<AgentChildRunResult> Run(AgentChildRunRequest request, CancellationToken token = default)
            => (await InvokeGeneratedClient(Client, "RunChildAsync", RunJson(request), token)).Deserialize<AgentChildRunResult>(ExecutionJson)!;
        public async ValueTask DisposeAsync() { await scope.DisposeAsync(); State.Dispose(); }
    }

    private sealed class ChildRunProbe : IAgentChildRunExecutor
    {
        public AgentChildRunRequest? Request { get; private set; }
        public AgentChildRunResult Result { get; set; } = new(Guid.NewGuid(), AgentRunStatus.Completed, "Summary");
        public int Calls { get; private set; }
        public bool Block { get; set; }
        public Exception? Failure { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<AgentChildRunResult> RunChildAsync(AgentChildRunRequest request, CancellationToken cancellationToken = default)
        {
            Calls++; Request = request;
            cancellationToken.ThrowIfCancellationRequested();
            if (Block) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            if (Failure is not null) throw Failure;
            return Result;
        }
    }

    private sealed class ChildRunCallRecorder(ISunderRpcClient inner) : ISunderRpcClient
    {
        public SunderRpcCallOptions? Options { get; private set; }
        public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        {
            Options = options;
            return inner.CreateCallScopeAsync(options, cancellationToken);
        }
        public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
            SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        {
            Assert.Equal("child-run-executor", serviceId); Assert.Equal("run-child", methodId);
            Options = options;
            return inner.InvokeAsync(endpoint, serviceId, methodId, request, options, cancellationToken);
        }
        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
            => inner.GetProviderAsync(endpoint, cancellationToken);
        public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default)
            => inner.TryReportInvariantViolationAsync(endpoint, exception, cancellationToken);
        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
            => inner.DiscoverAsync(contractId, cancellationToken);
        public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default)
            => inner.WatchAsync(afterRevision, afterSequence, cancellationToken);
        public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
            SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
            => inner.SubscribeAsync(endpoint, serviceId, methodId, request, options, cancellationToken);
    }
}
