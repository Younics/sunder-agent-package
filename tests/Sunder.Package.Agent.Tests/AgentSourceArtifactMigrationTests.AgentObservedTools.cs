using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyAgentObservedTools()
    {
        foreach (var mode in new[] { "normal", "terminal", "empty-result", "no-result", "invalid", "duplicate-dispatch", "callback-failure", "late-stale" })
        {
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            var handler = new ObservedToolPeer(mode);
            Add(state, handler);
            var rpc = new FilesRpcObserver(new RegressionTestRpcClient(state, "sunder.package.agent"));
            await using var receiver = new GeneratedServiceTestScope(rpc);
            var observed = Assert.Single((await receiver.Providers<AgentServices.IToolExecutionObservedService>().ListAsync()).Providers);
            var source = Assert.Single((await receiver.Providers<AgentServices.IToolSourceService>().ListAsync()).Providers);
            Assert.Equal("fixture.tools", await observed.Service.DescribeAsync());
            var dispatches = 0;
            var context = new AgentToolExecutionContext(Guid.NewGuid(), RunId: Guid.NewGuid(), RunRevision: 1, ToolCallId: "call.observed")
            {
                ApprovedResourceCapabilities = ["transient"], ExecutionTargetConfigurationGeneration = "generation",
                ResourceOperation = new(Guid.NewGuid(), 1, "call.observed", "files.mutate", 0, "workspace", "binding", "example.tools", "example.target", "activation", 2, true),
                DispatchStarted = () =>
                {
                    Assert.Equal(0, handler.ResumedAfterDispatch); dispatches++;
                    if (mode == "callback-failure") throw new InvalidOperationException("fixture callback rejected dispatch");
                    if (mode == "late-stale") rpc.Transform = value => value with { ActivationEpoch = value.ActivationEpoch + 1 };
                },
            };
            var request = new AgentToolRequest("fixture.tool", new string('x', 900_001));
            var invocation = new Services.AgentToolInvocationReference("example.tools",
                new AgentToolDescriptor("fixture.tool", "Fixture tool", "Observed execution"), source, observed,
                null, null, null, Guid.NewGuid().ToString("N"));
            var execute = typeof(Services.AgentToolService).GetMethod("ExecuteToolAsync",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            var before = DateTimeOffset.UtcNow;
            var scopesBeforeExecute = rpc.Scopes.ToHashSet(ReferenceEqualityComparer.Instance);
            var pending = Execute(execute, invocation, context, request);
            if (mode == "empty-result")
            {
                var malformed = await Assert.ThrowsAsync<SunderRpcException>(() => pending);
                Assert.Equal(SunderRpcErrorKind.ProviderFaulted, malformed.Error.Kind);
                Assert.Equal("rpc.provider.handler-fault", malformed.Error.Code);
                var validation = Assert.IsType<SunderRpcException>(state.LastInvocationFailure);
                Assert.Equal(SunderRpcErrorKind.Validation, validation.Error.Kind);
                Assert.Equal("test.rpc.schema-validation", validation.Error.Code);
            }
            else if (mode is "no-result" or "callback-failure") await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
            else if (mode is "invalid" or "late-stale")
            {
                var error = await Assert.ThrowsAsync<SunderRpcException>(() => pending);
                Assert.Equal(mode == "invalid" ? SunderRpcErrorKind.ProviderFaulted : SunderRpcErrorKind.StaleEndpoint, error.Error.Kind);
            }
            else Assert.Equal("Completed 漢字", (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Summary);
            Assert.True(handler.Disposed);
            Assert.Equal(mode is "terminal" or "empty-result" or "invalid" ? 0 : mode == "duplicate-dispatch" ? 2 : 1, dispatches);
            if (mode == "callback-failure") Assert.Equal(0, handler.ResumedAfterDispatch);
            if (mode is "normal" or "terminal" or "duplicate-dispatch") Assert.False(handler.AdvancedAfterResult);
            var sentContext = handler.Request.GetProperty("context");
            Assert.Equal(context.ToolCallId, sentContext.GetProperty("toolCallId").GetString());
            Assert.Equal(context.ApprovedResourceCapabilities, sentContext.GetProperty("approvedResourceCapabilities").Deserialize<string[]>(MemoryProviderJson));
            Assert.Equal(context.ResourceOperation!.AuthorityActivationId, sentContext.GetProperty("resourceOperation").GetProperty("authorityActivationId").GetString());
            Assert.Equal(request.ToolId, handler.Request.GetProperty("request").GetProperty("toolId").GetString());
            Assert.Equal(request.ArgumentsJson, handler.Request.GetProperty("request").GetProperty("argumentsJson").GetString());
            var executionScope = Assert.Single(rpc.Scopes, scope => !scopesBeforeExecute.Contains(scope));
            Assert.InRange(executionScope.DeadlineUtc, before.AddSeconds(2_099), before.AddSeconds(2_101));
            await receiver.DisposeAsync();
            Assert.All(rpc.Scopes, scope => Assert.True(scope.Disposed)); rpc.AssertScopedOperations();
        }
        using (var state = new RegressionTestRpcState(workerStyleProviderBoundary: true))
        {
            var handler = new ObservedToolPeer("normal") { Gate = new() }; Add(state, handler);
            var rpc = new FilesRpcObserver(new RegressionTestRpcClient(state, "sunder.package.agent"));
            await using var receiver = new GeneratedServiceTestScope(rpc);
            var observed = Assert.Single((await receiver.Providers<AgentServices.IToolExecutionObservedService>().ListAsync()).Providers);
            var source = Assert.Single((await receiver.Providers<AgentServices.IToolSourceService>().ListAsync()).Providers);
            var execute = typeof(Services.AgentToolService).GetMethod("ExecuteToolAsync",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            var context = new AgentToolExecutionContext(Guid.NewGuid()); var request = new AgentToolRequest("fixture.tool", "{}");
            var invocation = new Services.AgentToolInvocationReference("example.tools",
                new AgentToolDescriptor("fixture.tool", "Fixture tool", "Observed execution"), source, observed,
                null, null, null, Guid.NewGuid().ToString("N"));
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Execute(execute, invocation, context, request, cancelled.Token));
                Assert.Equal(0, handler.Starts);
            }
            var pending = Execute(execute, invocation, context, request); await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await state.RetireAsync(handler).WaitAsync(TimeSpan.FromSeconds(5));
            var retired = await Assert.ThrowsAsync<SunderRpcException>(() => pending);
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, retired.Error.Kind);
            Assert.True(handler.Disposed);
            handler = new("normal") { Gate = new() }; Add(state, handler);
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => Execute(execute, invocation, context, request))).Error.Kind);
            await receiver.DisposeAsync();
            await using var nextReceiver = new GeneratedServiceTestScope(rpc);
            var nextObserved = Assert.Single((await nextReceiver.Providers<AgentServices.IToolExecutionObservedService>().ListAsync()).Providers);
            var nextSource = Assert.Single((await nextReceiver.Providers<AgentServices.IToolSourceService>().ListAsync()).Providers);
            var nextInvocation = new Services.AgentToolInvocationReference("example.tools",
                new AgentToolDescriptor("fixture.tool", "Fixture tool", "Observed execution"), nextSource, nextObserved,
                null, null, null, Guid.NewGuid().ToString("N"));
            var stopping = Execute(execute, nextInvocation, context, request); await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopped = nextReceiver.DisposeAsync().AsTask(); Assert.Same(stopped, nextReceiver.DisposeAsync().AsTask());
            var disposed = await Assert.ThrowsAsync<SunderRpcException>(() => stopping);
            Assert.Equal(SunderRpcErrorKind.Unavailable, disposed.Error.Kind);
            Assert.Equal("rpc.binding.retired", disposed.Error.Code);
            Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, disposed.Error.DispatchState);
            Assert.Equal(SunderRpcRetrySafety.Unsafe, disposed.Error.RetrySafety);
            await stopped.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(handler.Disposed); Assert.All(rpc.Scopes, scope => Assert.True(scope.Disposed));
        }
        static void Add(RegressionTestRpcState state, ObservedToolPeer value)
        {
            var descriptor = CurrentPublicRpcDescriptors.Get(RegressionTestBindings.ObservedToolExecutions);
            CurrentPublicRpcDescriptors.AssertCurrent(descriptor);
            state.Add("example.tools", descriptor, value, value, Noop.Instance, "fixture.tools.observed", "2.0.0");
            var sourceDescriptor = CurrentPublicRpcDescriptors.Get(RegressionTestBindings.ToolSources);
            CurrentPublicRpcDescriptors.AssertCurrent(sourceDescriptor);
            state.Add("example.tools", sourceDescriptor, value, value, Noop.Instance, "fixture.tools.source", "2.0.0");
        }

        static Task<AgentToolResult> Execute(MethodInfo method, Services.AgentToolInvocationReference invocation,
            AgentToolExecutionContext context, AgentToolRequest request, CancellationToken token = default)
            => ((ValueTask<AgentToolResult>)method.Invoke(null, [invocation, context, request, token])!).AsTask();
    }

    private sealed class ObservedToolPeer(string mode) : ISunderRpcServiceHandler, ISunderRpcDeclaredErrorMetadata
    {
        internal int ResumedAfterDispatch;
        internal int Starts;
        internal bool Disposed;
        internal bool AdvancedAfterResult;
        internal FilesCallGate? Gate;
        internal JsonElement Request;
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> DeclaredErrors { get; } =
            new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>();
        public ValueTask<JsonElement> InvokeUnaryAsync(SunderRpcInvocationContext context, string serviceId, string methodId, JsonElement request, CancellationToken token = default)
        {
            Assert.Equal("tool-execution-observed", serviceId);
            Assert.Equal("describe", methodId);
            Assert.Equal(JsonValueKind.Object, request.ValueKind);
            Assert.Empty(request.EnumerateObject());
            return ValueTask.FromResult(JsonSerializer.SerializeToElement("fixture.tools"));
        }
        public async IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(SunderRpcInvocationContext context, string serviceId, string methodId, JsonElement request,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            Starts++; Request = request.Clone();
            try
            {
                if (Gate is { } gate) { gate.Started.TrySetResult(); await gate.Release.Task.WaitAsync(token); }
                if (mode == "invalid") { yield return JsonSerializer.SerializeToElement(new { kind = "unknown" }); yield break; }
                if (mode == "empty-result") { yield return JsonSerializer.SerializeToElement(new { kind = "result" }); yield break; }
                if (mode != "terminal")
                {
                    yield return JsonSerializer.SerializeToElement(new { kind = "dispatched" });
                    if (mode == "duplicate-dispatch") yield return JsonSerializer.SerializeToElement(new { kind = "dispatched" });
                    ResumedAfterDispatch++;
                }
                if (mode == "no-result") yield break;
                yield return JsonSerializer.SerializeToElement(
                    new { kind = "result", result = new AgentToolResult("fixture.tool", "Completed 漢字", "result", "{opaque") },
                    new JsonSerializerOptions(MemoryProviderJson) { DefaultIgnoreCondition = JsonIgnoreCondition.Never });
                AdvancedAfterResult = true;
                await Task.Delay(Timeout.Infinite, token);
            }
            finally { Disposed = true; }
        }
    }
}
