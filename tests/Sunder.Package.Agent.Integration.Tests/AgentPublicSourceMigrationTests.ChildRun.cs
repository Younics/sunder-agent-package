extern alias SubagentsWorker;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;
using IChildRunExecutorService = SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Child_Run_Executor.IChildRunExecutorService;
using ChildConsumerModels = SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models;
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
            var expected = RunJson(probe.Result);
            var domainRequest = RunJson(request);
            var actual = await fixture.Run(request);
            Assert.True(JsonElement.DeepEquals(expected, RunJson(actual)));
            Assert.True(JsonElement.DeepEquals(domainRequest, RunJson(probe.Request)));
            Assert.Equal(present, actual.Title is not null);
            Assert.Equal(present, actual.Content is not null);
        }
        foreach (var absentTaskId in new string?[] { null, "", " \t " })
        {
            var absentTask = request with { TaskId = absentTaskId };
            var calls = probe.Calls;
            await fixture.Run(absentTask);
            Assert.Equal(calls + 1, probe.Calls);
            Assert.Equal(absentTaskId, probe.Request!.TaskId);
            Assert.True(JsonElement.DeepEquals(RunJson(absentTask), RunJson(probe.Request)));
            var sentTaskId = fixture.State.LastInvocationRequest.GetProperty("taskId");
            Assert.Equal(absentTaskId is null ? JsonValueKind.Null : JsonValueKind.String, sentTaskId.ValueKind);
            Assert.Equal(absentTaskId, sentTaskId.GetString());
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
        await fixture.Run(minimal);
        Assert.True(JsonElement.DeepEquals(RunJson(minimal), RunJson(probe.Request)));
        var validGeneratedRequest = fixture.State.LastInvocationRequest;
        var beforeCalls = probe.Calls;
        var beforePeer = fixture.State.InvocationAttemptCount;
        await Assert.ThrowsAsync<JsonException>(() => fixture.Run(request with { ParentRunRevision = -1 }));
        Assert.Equal(beforePeer, fixture.State.InvocationAttemptCount);
        Assert.Equal(beforeCalls, probe.Calls);
        var invalidRevisionWire = JsonNode.Parse(validGeneratedRequest.GetRawText())!.AsObject();
        invalidRevisionWire["parentRunRevision"] = -1;
        var invalidRevision = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Rpc.InvokeAsync(fixture.Source,
            "child-run-executor", "run-child", JsonSerializer.SerializeToElement(invalidRevisionWire)).AsTask());
        Assert.Equal(SunderRpcErrorKind.Validation, invalidRevision.Error.Kind);
        Assert.Equal(beforePeer + 1, fixture.State.InvocationAttemptCount);
        Assert.Equal(beforeCalls, probe.Calls);
        beforeCalls = probe.Calls;
        beforePeer = fixture.State.InvocationAttemptCount;
        await Assert.ThrowsAsync<JsonException>(() => fixture.Run(request with { ParentToolCallId = "" }));
        Assert.Equal(beforePeer, fixture.State.InvocationAttemptCount);
        Assert.Equal(beforeCalls, probe.Calls);
        var invalidCorrelationWire = JsonNode.Parse(validGeneratedRequest.GetRawText())!.AsObject();
        invalidCorrelationWire["parentToolCallId"] = "";
        var invalidCorrelation = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Rpc.InvokeAsync(fixture.Source,
            "child-run-executor", "run-child", JsonSerializer.SerializeToElement(invalidCorrelationWire)).AsTask());
        Assert.Equal(SunderRpcErrorKind.Validation, invalidCorrelation.Error.Kind);
        Assert.Equal(beforePeer + 1, fixture.State.InvocationAttemptCount);
        Assert.Equal(beforeCalls, probe.Calls);
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            var calls = probe.Calls;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Run(request, cancellation.Token));
            Assert.Equal(calls, probe.Calls);
        }
        using (var cancellation = new CancellationTokenSource())
        {
            probe.Block = true;
            var pending = fixture.Run(request, cancellation.Token);
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
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Run(request));
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, error.Error.Kind);
            Assert.DoesNotContain("private child", error.Message, StringComparison.Ordinal);
        }
        probe.Failure = null;
        var recorder = new ChildRunCallRecorder(fixture.Rpc);
        await using var consumer = new GeneratedServiceTestScope(recorder);
        var selected = Assert.Single((await consumer.Providers<IChildRunExecutorService>().ListAsync()).Providers);
        await selected.EnsureCurrentAsync();
        await InvokeChildRunAsync(selected.Service, consumer, request, default);
        Assert.True(JsonElement.DeepEquals(RunJson(request), RunJson(probe.Request)));
        var recordedCall = Assert.Single(recorder.CreatedScopes);
        Assert.True(recordedCall.Options?.DeadlineUtc is not null,
            $"Child-run scope creation received no explicit deadline. Actual scope deadline: {recordedCall.Scope.DeadlineUtc:O}");
        Assert.Equal(recordedCall.Options!.DeadlineUtc!.Value, recordedCall.Scope.DeadlineUtc);
        Assert.InRange(recordedCall.Scope.DeadlineUtc - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(34), TimeSpan.FromMinutes(36));

        await VerifyChildRunPersistenceAsync(assembly, descriptor);
    }

    private static async Task<ChildRunFixture> CreateChildRunFixture(Assembly assembly, SunderRpcContractDescriptor descriptor, object executor)
    {
        var type = assembly.GetType("Sunder.Generated.Packages.Younics_Ai_Agent_Services.IChildRunExecutorService", true)!;
        Assert.True(type.IsInterface);
        var implementation = executor;
        var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "younics.ai.agent.subagents");
        var receiver = new GeneratedServiceTestScope(rpc);
        using var providers = new RegressionTestSourceProviders();
        var registration = providers.Create(descriptor.ContractId, implementation, receiver);
        Assert.Equal(executor is ChildRunProbe ? "fixture.regression.child.run" : "agent.child.runs", registration.ProviderId);
        Assert.Equal(descriptor.Sha256, registration.DescriptorSha256);
        Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
        state.Add("younics.ai.agent", descriptor, implementation, registration.Handler, EmptyDisposable.Instance, registration.ProviderId, "2.0.0");
        var provider = Assert.Single((await receiver.Providers<IChildRunExecutorService>().ListAsync()).Providers,
            value => value.ProviderId == registration.ProviderId);
        var endpoint = Assert.Single(state.Discover(descriptor.ContractId).Providers,
            value => value.ProviderId == registration.ProviderId).Endpoint;
        return new(state, rpc, receiver, provider.Service, implementation, endpoint);
    }

    private sealed class ChildRunFixture(RegressionTestRpcState state, RegressionTestRpcClient rpc, GeneratedServiceTestScope receiver,
        IChildRunExecutorService client, object implementation, SunderRpcEndpointReference source) : IAsyncDisposable
    {
        public RegressionTestRpcState State { get; } = state;
        public RegressionTestRpcClient Rpc { get; } = rpc;
        public object Implementation { get; } = implementation;
        public SunderRpcEndpointReference Source { get; } = source;
        public async Task<AgentChildRunResult> Run(AgentChildRunRequest request, CancellationToken token = default)
            => await InvokeChildRunAsync(client, receiver, request, token);
        public async ValueTask DisposeAsync() { await receiver.DisposeAsync(); State.Dispose(); }
    }

    private static async Task<AgentChildRunResult> InvokeChildRunAsync(IChildRunExecutorService client,
        GeneratedServiceTestScope receiver, AgentChildRunRequest request, CancellationToken token)
    {
        var projection = RegressionTestGeneratedProviderProjection.FromCandidate(
            Environment.GetEnvironmentVariable("SUNDER_AGENT_PUBLIC_FIXTURE_CANDIDATE")!, RegressionTestBindings.ChildRunExecutor);
        var input = (ChildConsumerModels.AgentChildRunRequest)(await projection.ProjectAsync(
            request, typeof(ChildConsumerModels.AgentChildRunRequest), receiver, token))!;
        var result = await client.RunChildAsync(input, token);
        return (AgentChildRunResult)(await projection.ProjectAsync(result, typeof(AgentChildRunResult), receiver, token))!;
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
        public List<(SunderRpcCallOptions? Options, ISunderRpcCallScope Scope)> CreatedScopes { get; } = [];
        public async ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        {
            Options = options;
            var scope = await inner.CreateCallScopeAsync(options, cancellationToken);
            CreatedScopes.Add((options, scope));
            return scope;
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
