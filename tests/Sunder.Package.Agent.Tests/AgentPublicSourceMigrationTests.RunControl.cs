using System.Reflection;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    private const string RunControlCaller = "sunder.package.agent.test.loop";

    private static async Task VerifyRunControlAsync(Assembly assembly, SunderRpcContractDescriptor descriptor)
    {
        await using var fixture = await CreateRunControlFixture(assembly, descriptor);
        var runtime = new FullRunControlProbe();
        using var lease = fixture.Registry.Open(runtime, RunControlCaller, out var invocationId);
        using var callbackCatalog = new AgentRpcCatalog(fixture.Rpc);
        var callback = (Sunder.Package.Agent.Contracts.Contracts.IAgentBehaviorLoopRuntime)Activator.CreateInstance(
            assembly.GetType("Sunder.Package.Agent.Runtime.Rpc.GeneratedBehaviorRuntime", true)!, callbackCatalog, invocationId,
            AgentRpcProviderHandle.From(Assert.Single(fixture.State.Discover(descriptor.ContractId).Providers, provider => provider.Endpoint == fixture.Source)))!;
        var invocation = new RunControlInvocation(invocationId);
        var requests = RunControlRequests(runtime, invocation);
        Assert.Equal(19, requests.Count);
        Assert.Equal(requests.Select(value => value.Route).Order(StringComparer.Ordinal),
            descriptor.Services.Single().Methods.Where(method => method.MethodId != "stream-chat").Select(method => method.MethodId).Order(StringComparer.Ordinal));
        foreach (var (route, method, request) in requests)
        {
            var expected = await InvokeRunControlRuntime(runtime, route, request);
            var arguments = runtime.Arguments.Clone();
            var actual = await InvokeGeneratedClient(fixture.Client, method, request);
            Assert.True(JsonElement.DeepEquals(expected, actual), $"{route} output: {expected} != {actual}");
            Assert.True(JsonElement.DeepEquals(arguments, runtime.Arguments), $"{route} domain arguments: {arguments} != {runtime.Arguments}");
            if (route == "list-ready-tools")
            {
                await Assert.ThrowsAnyAsync<JsonException>(() => callback.ListReadyToolsAsync().AsTask()); // The intentionally opaque schema is not valid executable tool metadata.
                runtime.ToolSchema = "{\"type\":\"object\"}";
                Assert.Equal(runtime.ToolSchema, Assert.Single(await callback.ListReadyToolsAsync()).Descriptor.ArgumentsJsonSchema);
                runtime.ToolSchema = "{opaque-schema";
            }
            else
            {
                var callbackResult = await InvokeRunControlRuntime(callback, route, request);
                Assert.True(JsonElement.DeepEquals(expected, callbackResult), $"{route} callback result: {expected} != {callbackResult}");
                Assert.True(JsonElement.DeepEquals(arguments, runtime.Arguments), $"{route} callback arguments: {arguments} != {runtime.Arguments}");
            }
        }
        foreach (var limit in new[] { 0, 1001 })
        {
            var calls = runtime.Calls;
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => InvokeGeneratedClient(fixture.Client, "ListRecentTurnsAsync", RunJson(new { invocation, limit })));
            Assert.Equal(SunderRpcErrorKind.Validation, error.Error.Kind);
            Assert.Equal(calls, runtime.Calls);
        }
        runtime.Current = false;
        Assert.False((await InvokeGeneratedClient(fixture.Client, "IsCurrentAsync", RunJson(invocation))).GetBoolean());

        // Every route must resolve the caller-bound lease before it reaches the runtime.
        var foreign = new RegressionTestRpcClient(fixture.State, "sunder.package.agent.foreign.loop");
        var before = runtime.Calls;
        foreach (var (route, _, request) in requests)
        {
            await Assert.ThrowsAsync<SunderRpcException>(() => foreign.InvokeAsync(fixture.Source, "run-control", route, request).AsTask());
            Assert.Equal("agent.run-control.invalid-invocation", Assert.IsType<SunderRpcException>(fixture.State.LastInvocationFailure).Error.Code);
        }
        await Assert.ThrowsAsync<SunderRpcException>(async () =>
        {
            await foreach (var _ in foreign.SubscribeAsync(fixture.Source, "run-control", "stream-chat", RunJson(RunChatRequest(invocation)))) { }
        });
        Assert.Equal(before, runtime.Calls);

        await VerifyRunControlCapabilitiesAsync(fixture, requests);
        foreach (var method in new[] { "BuildInstructionContextAsync", "InvokeToolAsync", "InvokeToolsAsync", "RunDefaultAsync", "AcknowledgePromptContextAsync" })
        {
            runtime.Block = true;
            runtime.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            var pending = InvokeGeneratedClient(fixture.Client, method, requests.Single(value => value.Method == method).Request, cancellation.Token);
            await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        runtime.Block = false;
        runtime.Failure = new SunderRpcException(new(SunderRpcErrorKind.Domain, "agent.run-control.capability-unavailable", "private unrelated domain error"));
        var forged = await Assert.ThrowsAsync<SunderRpcException>(() => InvokeGeneratedClient(fixture.Client, "SaveCheckpointAsync",
            requests.Single(value => value.Route == "save-checkpoint").Request));
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, forged.Error.Kind);
        Assert.DoesNotContain("private unrelated", forged.Message, StringComparison.Ordinal);
        runtime.Failure = new IOException("private run storage failure");
        var failure = await Assert.ThrowsAsync<SunderRpcException>(() => InvokeGeneratedClient(fixture.Client, "ListTurnsAsync", RunJson(invocation)));
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, failure.Error.Kind);
        Assert.DoesNotContain("private run storage", failure.Message, StringComparison.Ordinal);
        runtime.Failure = null;
        lease.Dispose();
        lease.Dispose();
        var finalCalls = runtime.Calls;
        await Assert.ThrowsAsync<SunderRpcException>(() => InvokeGeneratedClient(fixture.Client, "IsCurrentAsync", RunJson(invocation)));
        Assert.Equal(finalCalls, runtime.Calls);
        Assert.Equal("agent.run-control.invalid-invocation", Assert.IsType<SunderRpcException>(fixture.State.LastInvocationFailure).Error.Code);

        await VerifyRunControlStreamsAsync(assembly, fixture);
        await VerifyRunControlPersistenceAsync(assembly, descriptor);
    }

    private static async Task VerifyRunControlCapabilitiesAsync(RunControlFixture fixture, IReadOnlyList<(string Route, string Method, JsonElement Request)> requests)
    {
        var runtime = new RunControlProbe();
        using var lease = fixture.Registry.Open(runtime, RunControlCaller, out var invocationId);
        foreach (var (route, method, original) in requests.Where(value => value.Route is "select-session-context" or "report-activity" or "get-run-budget"
                     or "charge-run-budget" or "acknowledge-prompt-context" or "run-default"))
        {
            var request = System.Text.Json.Nodes.JsonNode.Parse(original.GetRawText())!;
            if (route == "get-run-budget") request["invocationId"] = invocationId;
            else request["invocation"]!["invocationId"] = invocationId;
            var json = RunJson(request);
            var actual = await Assert.ThrowsAsync<SunderRpcException>(() => InvokeGeneratedClient(fixture.Client, method, json));
            Assert.Equal(SunderRpcErrorKind.Domain, actual.Error.Kind);
            Assert.Equal(route switch
            {
                "acknowledge-prompt-context" => "agent.run-control.prompt-context-acknowledgment-unavailable",
                "run-default" => "agent.run-control.default-loop-unavailable",
                _ => "agent.run-control.capability-unavailable",
            }, actual.Error.Code);
        }
        Assert.Equal(0, runtime.Calls);
        Assert.Equal("{}", (await InvokeGeneratedClient(fixture.Client, "DiscardPromptContextAsync", RunJson(new RunControlInvocation(invocationId)))).GetRawText());
    }

    private static List<(string Route, string Method, JsonElement Request)> RunControlRequests(FullRunControlProbe runtime, RunControlInvocation invocation)
    {
        var checkpoint = runtime.Context.RunningCheckpoint;
        var contextCheckpoint = new AgentSessionContextCheckpointRecord(Guid.NewGuid(), runtime.Turn.SessionId, Guid.NewGuid(), runtime.Turn.TurnId, 2, "Summary", "{details", checkpoint.CreatedAtUtc);
        var tool = new AgentToolCallRequest("call.one", "tool.one", "{arguments");
        return
        [
            ("is-current", "IsCurrentAsync", RunJson(invocation)),
            ("list-turns", "ListTurnsAsync", RunJson(invocation)),
            ("list-recent-turns", "ListRecentTurnsAsync", RunJson(new RunControlRecentTurnsRequest(invocation, 1000))),
            ("build-instruction-context", "BuildInstructionContextAsync", RunJson(invocation)),
            ("select-session-context", "SelectSessionContextAsync", RunJson(new RunControlSessionProjectionRequest(invocation, new([runtime.Turn], true, 2, contextCheckpoint)))),
            ("report-activity", "ReportActivityAsync", RunJson(new RunControlActivityRequest(invocation, AgentRunActivityKind.Reasoning, "Reasoning 漢字"))),
            ("get-run-budget", "GetRunBudgetAsync", RunJson(invocation)),
            ("charge-run-budget", "ChargeRunBudgetAsync", RunJson(new RunControlBudgetChargeRequest(invocation, new(2, 3, 4000)))),
            ("acknowledge-prompt-context", "AcknowledgePromptContextAsync", RunJson(new RunControlPromptContextRequest(invocation, [new("host.one", "context.one", "/workspace/AGENTS.md", new string('b', 64))]))),
            ("discard-prompt-context", "DiscardPromptContextAsync", RunJson(invocation)),
            ("list-ready-tools", "ListReadyToolsAsync", RunJson(invocation)),
            ("save-checkpoint", "SaveCheckpointAsync", RunJson(new RunControlCheckpointRequest(invocation, AgentRunStatus.Completed, "Done"))),
            ("log-event", "LogEventAsync", RunJson(new RunControlLogRequest(invocation, PackageLogLevel.Warning, "loop.event", "Message", 0,
                new Dictionary<string, JsonElement> { ["null"] = RunJson((object?)null), ["array"] = RunJson(new object[] { false, 3, "漢字" }) }, "ignored.type", "ignored.message"))),
            ("upsert-assistant-turn", "UpsertAssistantTurnAsync", RunJson(new RunControlAssistantTurnRequest(invocation, runtime.Turn, "Complete text"))),
            ("complete-assistant-turn", "CompleteAssistantTurnAsync", RunJson(new RunControlCompleteTurnRequest(invocation, runtime.Turn))),
            ("publish-lifecycle", "PublishLifecycleAsync", RunJson(new RunControlLifecycleRequest(invocation, AgentLifecycleEventKind.RunInterrupted, AgentRunStatus.Interrupted, runtime.Turn, checkpoint, true))),
            ("invoke-tool", "InvokeToolAsync", RunJson(new RunControlToolRequest(invocation, tool, runtime.Turn))),
            ("invoke-tools", "InvokeToolsAsync", RunJson(new RunControlToolsRequest(invocation, [tool, tool with { CallId = "call.two", ToolId = "tool.two" }], runtime.Turn))),
            ("run-default", "RunDefaultAsync", RunJson(new RunControlDefaultLoopRequest(invocation, runtime.Context))),
        ];
    }

    private static async Task<RunControlFixture> CreateRunControlFixture(Assembly assembly, SunderRpcContractDescriptor descriptor)
    {
        var registry = new AgentRunControlRegistry();
        var type = assembly.GetType("Sunder.Package.Agent.Rpc.Public.RunControlService", true)!;
        var implementation = Activator.CreateInstance(type, registry)!;
        var registration = Assert.IsType<SunderWorkerProviderRegistration>(type.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(implementation, null));
        Assert.Equal("agent.run.control", registration.ProviderId);
        Assert.Equal(descriptor.Sha256, registration.DescriptorSha256);
        Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
        var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, RunControlCaller);
        state.Add("sunder.package.agent", descriptor, implementation, registration.Handler, EmptyDisposable.Instance, registration.ProviderId, "2.0.0");
        var providers = (await rpc.DiscoverAsync(descriptor.ContractId)).Providers;
        var endpoint = providers.Single(value => value.ProviderId == registration.ProviderId).Endpoint;
        var scope = await rpc.CreateCallScopeAsync();
        var client = Activator.CreateInstance(assembly.GetType("Sunder.Generated.Rpc.RunControl.RunControlClient", true)!, scope, endpoint)!;
        return new(state, rpc, scope, client, registry, endpoint);
    }

    private sealed class RunControlFixture(RegressionTestRpcState state, RegressionTestRpcClient rpc, ISunderRpcCallScope scope, object client,
        AgentRunControlRegistry registry, SunderRpcEndpointReference source) : IAsyncDisposable
    {
        public RegressionTestRpcState State { get; } = state;
        public RegressionTestRpcClient Rpc { get; } = rpc;
        public ISunderRpcCallScope Scope { get; } = scope;
        public object Client { get; } = client;
        public AgentRunControlRegistry Registry { get; } = registry;
        public SunderRpcEndpointReference Source { get; } = source;
        public async ValueTask DisposeAsync() { await Scope.DisposeAsync(); State.Dispose(); }
    }

    private static JsonElement RunJson<T>(T value) => JsonSerializer.SerializeToElement(value, ExecutionJson);
}
