using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    private const string RunControlCaller = "younics.ai.agent.test.loop";

    private static async Task VerifyRunControlAsync(System.Reflection.Assembly assembly, SunderRpcContractDescriptor descriptor)
    {
        await using var fixture = await CreateRunControlFixture(assembly, descriptor);
        var runtime = new FullRunControlProbe();
        using var lease = fixture.Registry.Open(runtime, RunControlCaller, out var invocationId);
        var control = fixture.Client;
        var checkpoint = runtime.Context.RunningCheckpoint;
        var contextCheckpoint = new AgentSessionContextCheckpointRecord(Guid.NewGuid(), runtime.Turn.SessionId, Guid.NewGuid(), runtime.Turn.TurnId, 2, "Summary", "{details", checkpoint.CreatedAtUtc);
        var tool = new AgentToolCallRequest("call.one", "tool.one", "{arguments");

        Assert.Equal(runtime.IsCurrentRun(), await control.IsCurrentAsync(invocationId));
        Assert.True(JsonElement.DeepEquals(RunJson(runtime.ListTurns()), RunJson(await control.ListTurnsAsync(invocationId))));
        Assert.True(JsonElement.DeepEquals(RunJson(runtime.ListRecentTurns(100)), RunJson(await control.ListRecentTurnsAsync(invocationId, 100))));
        var validRecentTurnsRequest = fixture.State.LastInvocationRequest;
        Assert.True(JsonElement.DeepEquals(RunJson(await runtime.BuildInstructionContextAsync()), RunJson(await control.BuildInstructionContextAsync(invocationId))));
        var projection = new AgentSessionPromptProjection([runtime.Turn], true, 2, contextCheckpoint);
        runtime.SelectSessionContextProjection(projection);
        await control.SelectSessionContextAsync(invocationId, projection);
        Assert.Equal(invocationId, fixture.State.LastInvocationRequest.GetProperty("invocationId").GetGuid());
        Assert.True(JsonElement.DeepEquals(runtime.Arguments, RunJson(new { projection })));
        runtime.ReportRunActivity(AgentRunActivityKind.Reasoning, "Reasoning 漢字");
        await control.ReportActivityAsync(invocationId, AgentRunActivityKind.Reasoning, "Reasoning 漢字");
        Assert.Equal(invocationId, fixture.State.LastInvocationRequest.GetProperty("invocationId").GetGuid());
        Assert.True(JsonElement.DeepEquals(runtime.Arguments, RunJson(new { kind = AgentRunActivityKind.Reasoning, text = "Reasoning 漢字" })));
        Assert.Equal(runtime.GetRunBudgetState(), await control.GetRunBudgetAsync(invocationId));
        var charge = new AgentRunBudgetCharge(2, 3, 4000);
        Assert.Equal(runtime.ChargeRunBudget(charge), await control.ChargeRunBudgetAsync(invocationId, charge));
        await Assert.IsAssignableFrom<IAgentPromptContextAcknowledgmentRuntime>(runtime).AcknowledgePromptContextAsync([], default);
        await control.AcknowledgePromptContextAsync(invocationId, []);
        await control.DiscardPromptContextAsync(invocationId);
        Assert.Equal(runtime.ToolSchema, Assert.Single(await control.ListReadyToolsAsync(invocationId)).ArgumentsJsonSchema);
        runtime.ToolSchema = "{\"type\":\"object\"}";
        Assert.Equal(runtime.ToolSchema, Assert.Single(await control.ListReadyToolsAsync(invocationId)).ArgumentsJsonSchema);
        runtime.ToolSchema = "{opaque-schema";
        Assert.Equal(runtime.SaveCheckpoint(AgentRunStatus.Completed, "Done"), await control.SaveCheckpointAsync(invocationId, AgentRunStatus.Completed, "Done"));
        var attributes = new Dictionary<string, JsonElement> { ["null"] = RunJson((object?)null), ["array"] = RunJson(new object[] { false, 3, "漢字" }) };
        runtime.LogEvent(PackageLogLevel.Warning, "loop.event", "Message", 0, attributes.ToDictionary(item => item.Key, item => (object?)item.Value));
        var expectedLog = runtime.Arguments;
        await control.LogEventAsync(invocationId, "warning", "loop.event", "Message", 0, attributes);
        Assert.True(JsonElement.DeepEquals(expectedLog, runtime.Arguments));
        var assistant = runtime.UpsertAssistantTurn(runtime.Turn, "Complete text");
        Assert.True(JsonElement.DeepEquals(RunJson(assistant), RunJson(await control.UpsertAssistantTurnAsync(invocationId, runtime.Turn, "Complete text"))));
        Assert.True(JsonElement.DeepEquals(RunJson(runtime.CompleteAssistantTurn(runtime.Turn)), RunJson(await control.CompleteAssistantTurnAsync(invocationId, runtime.Turn))));
        await runtime.PublishLifecycleEventAsync(AgentLifecycleEventKind.RunInterrupted, AgentRunStatus.Interrupted, runtime.Turn, checkpoint, true, default);
        await control.PublishLifecycleAsync(invocationId, AgentLifecycleEventKind.RunInterrupted, AgentRunStatus.Interrupted, runtime.Turn, checkpoint, true);
        Assert.True(JsonElement.DeepEquals(RunJson(await runtime.InvokeToolAsync(tool, runtime.Turn)), RunJson(await control.InvokeToolAsync(invocationId, tool, runtime.Turn))));
        var tools = new[] { tool, tool with { CallId = "call.two", ToolId = "tool.two" } };
        Assert.True(JsonElement.DeepEquals(RunJson(await runtime.InvokeToolsAsync(tools, runtime.Turn)), RunJson(await control.InvokeToolsAsync(invocationId, tools, runtime.Turn))));
        Assert.True(JsonElement.DeepEquals(RunJson(await Assert.IsAssignableFrom<IAgentInnerBehaviorLoopRuntime>(runtime).RunDefaultLoopAsync(runtime.Context)), RunJson(await control.RunDefaultAsync(invocationId, runtime.Context))));

        foreach (var limit in new[] { 0, 101, 1001 })
        {
            var calls = runtime.Calls;
            var peerCalls = fixture.State.InvocationAttemptCount;
            await Assert.ThrowsAsync<JsonException>(() => control.ListRecentTurnsAsync(invocationId, limit).AsTask());
            Assert.Equal(peerCalls, fixture.State.InvocationAttemptCount);
            Assert.Equal(calls, runtime.Calls);
            var invalidWire = JsonNode.Parse(validRecentTurnsRequest.GetRawText())!.AsObject();
            invalidWire["limit"] = limit;
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Rpc.InvokeAsync(fixture.Source,
                "run-control", "list-recent-turns", JsonSerializer.SerializeToElement(invalidWire)).AsTask());
            Assert.Equal(SunderRpcErrorKind.Validation, error.Error.Kind);
            Assert.Equal(peerCalls + 1, fixture.State.InvocationAttemptCount);
            Assert.Equal(calls, runtime.Calls);
        }
        runtime.Current = false;
        Assert.False(await control.IsCurrentAsync(invocationId));

        // A generated catalog bound to another caller must not acquire this lease.
        var foreignRpc = new RegressionTestRpcClient(fixture.State, "younics.ai.agent.foreign.loop");
        await using var foreignScope = new GeneratedServiceTestScope(foreignRpc);
        var foreign = Assert.Single((await foreignScope.Providers<AgentServices.IRunControlService>().ListAsync()).Providers).Service;
        var before = runtime.Calls;
        await Assert.ThrowsAsync<SunderRpcException>(() => foreign.IsCurrentAsync(invocationId).AsTask());
        await Assert.ThrowsAsync<SunderRpcException>(() => ReadRunControlStream(foreign, invocationId, RunChatRequest(), null, default));
        Assert.Equal(before, runtime.Calls);
        Assert.Equal("agent.run-control.invalid-invocation", Assert.IsType<SunderRpcException>(fixture.State.LastInvocationFailure).Error.Code);

        await VerifyRunControlCapabilitiesAsync(fixture);
        foreach (var call in new Func<CancellationToken, Task>[]
        {
            token => control.BuildInstructionContextAsync(invocationId, token).AsTask(),
            token => control.InvokeToolAsync(invocationId, tool, runtime.Turn, token).AsTask(),
            token => control.InvokeToolsAsync(invocationId, tools, runtime.Turn, token).AsTask(),
            token => control.RunDefaultAsync(invocationId, runtime.Context, token).AsTask(),
            token => control.AcknowledgePromptContextAsync(invocationId, [], token).AsTask(),
        })
        {
            runtime.Block = true;
            runtime.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            var pending = call(cancellation.Token);
            await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        runtime.Block = false;
        runtime.Failure = new SunderRpcException(new(SunderRpcErrorKind.Domain, "agent.run-control.capability-unavailable", "private unrelated domain error"));
        var forged = await Assert.ThrowsAsync<SunderRpcException>(() => control.SaveCheckpointAsync(invocationId, AgentRunStatus.Completed, "Done").AsTask());
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, forged.Error.Kind);
        Assert.DoesNotContain("private unrelated", forged.Message, StringComparison.Ordinal);
        runtime.Failure = new IOException("private run storage failure");
        var failure = await Assert.ThrowsAsync<SunderRpcException>(() => control.ListTurnsAsync(invocationId).AsTask());
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, failure.Error.Kind);
        Assert.DoesNotContain("private run storage", failure.Message, StringComparison.Ordinal);
        runtime.Failure = null;
        lease.Dispose();
        lease.Dispose();
        var finalCalls = runtime.Calls;
        await Assert.ThrowsAsync<SunderRpcException>(() => control.IsCurrentAsync(invocationId).AsTask());
        Assert.Equal(finalCalls, runtime.Calls);
        Assert.Equal("agent.run-control.invalid-invocation", Assert.IsType<SunderRpcException>(fixture.State.LastInvocationFailure).Error.Code);

        await VerifyRunControlStreamsAsync(assembly, fixture);
        await VerifyRunControlPersistenceAsync(assembly, descriptor);
    }

    private static async Task VerifyRunControlCapabilitiesAsync(RunControlFixture fixture)
    {
        var runtime = new RunControlProbe();
        using var lease = fixture.Registry.Open(runtime, RunControlCaller, out var invocationId);
        var control = fixture.Client;
        foreach (var (call, code) in new (Func<Task> Call, string Code)[]
        {
            (() => control.SelectSessionContextAsync(invocationId, new([], true, 2)).AsTask(), "agent.run-control.capability-unavailable"),
            (() => control.ReportActivityAsync(invocationId, AgentRunActivityKind.Reasoning, "Reasoning").AsTask(), "agent.run-control.capability-unavailable"),
            (() => control.GetRunBudgetAsync(invocationId).AsTask(), "agent.run-control.capability-unavailable"),
            (() => control.ChargeRunBudgetAsync(invocationId, new(2, 3, 4000)).AsTask(), "agent.run-control.capability-unavailable"),
            (() => control.AcknowledgePromptContextAsync(invocationId, []).AsTask(), "agent.run-control.prompt-context-acknowledgment-unavailable"),
            (() => control.RunDefaultAsync(invocationId, runtime.Context).AsTask(), "agent.run-control.default-loop-unavailable"),
        })
        {
            var actual = await Assert.ThrowsAsync<SunderRpcException>(call);
            Assert.Equal(SunderRpcErrorKind.Domain, actual.Error.Kind);
            Assert.Equal(code, actual.Error.Code);
        }
        Assert.Equal(0, runtime.Calls);
        await control.DiscardPromptContextAsync(invocationId);
    }

    private static async Task<RunControlFixture> CreateRunControlFixture(System.Reflection.Assembly assembly, SunderRpcContractDescriptor descriptor)
    {
        var activeRuns = new AgentActiveRunRegistry();
        var registry = new AgentRoleRunControls(assembly, activeRuns);
        var implementation = new AgentRunControlService(activeRuns);
        var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, RunControlCaller);
        var receiver = new GeneratedServiceTestScope(rpc);
        var registration = receiver.Register(implementation, descriptor.ContractId);
        Assert.Equal("agent.run.control", registration.ProviderId);
        Assert.Equal(descriptor.Sha256, registration.DescriptorSha256);
        Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
        state.Add("younics.ai.agent", descriptor, implementation, registration.Handler, EmptyDisposable.Instance, registration.ProviderId, "2.0.0");
        var endpoint = (await rpc.DiscoverAsync(descriptor.ContractId)).Providers.Single(value => value.ProviderId == registration.ProviderId).Endpoint;
        var client = Assert.Single((await receiver.Providers<AgentServices.IRunControlService>().ListAsync()).Providers).Service;
        var scope = await rpc.CreateCallScopeAsync();
        return new(state, rpc, scope, receiver, client, registry, endpoint);
    }

    private sealed class RunControlFixture(RegressionTestRpcState state, RegressionTestRpcClient rpc, ISunderRpcCallScope scope, GeneratedServiceTestScope receiver,
        AgentServices.IRunControlService client, AgentRoleRunControls registry, SunderRpcEndpointReference source) : IAsyncDisposable
    {
        public RegressionTestRpcState State { get; } = state;
        public RegressionTestRpcClient Rpc { get; } = rpc;
        public ISunderRpcCallScope Scope { get; } = scope;
        public AgentServices.IRunControlService Client { get; } = client;
        public AgentRoleRunControls Registry { get; } = registry;
        public SunderRpcEndpointReference Source { get; } = source;
        public async ValueTask DisposeAsync() { await Scope.DisposeAsync(); await receiver.DisposeAsync(); State.Dispose(); }
    }

    private static JsonElement RunJson<T>(T value) => JsonSerializer.SerializeToElement(value, ExecutionJson);
}
