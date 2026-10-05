using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyAgentObservedTools(Assembly owner)
    {
        foreach (var mode in new[] { "normal", "terminal", "empty-result", "no-result", "invalid", "duplicate-dispatch", "callback-failure", "late-stale" })
        {
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            var handler = new ObservedToolPeer(mode);
            Add(state, handler);
            var rpc = new FilesRpcObserver(new RegressionTestRpcClient(state, "sunder.package.agent"));
            var snapshot = Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.ObservedToolExecution)).Providers);
            var client = CreateAgentObservedToolClient(owner, rpc, snapshot);
            await using var lifetime = (IAsyncDisposable)client;
            Assert.Equal("fixture.tools", client.SourceId);
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
            var before = DateTimeOffset.UtcNow;
            var pending = client.ExecuteAsync(context, request).AsTask();
            if (mode is "empty-result" or "no-result" or "callback-failure") await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
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
            Assert.True(JsonElement.DeepEquals(RegressionTestRpcValues.ToolInvocation(context, request), handler.Request));
            Assert.Contains(rpc.Scopes, scope => scope.DeadlineUtc >= before.AddMinutes(34) && scope.DeadlineUtc <= DateTimeOffset.UtcNow.AddMinutes(36));
            Assert.All(rpc.Scopes, scope => Assert.True(scope.Disposed)); Assert.Equal(0, rpc.UnscopedLookups);
        }
        using (var state = new RegressionTestRpcState(workerStyleProviderBoundary: true))
        {
            var handler = new ObservedToolPeer("normal") { Gate = new() }; Add(state, handler);
            var rpc = new FilesRpcObserver(new RegressionTestRpcClient(state, "sunder.package.agent"));
            var snapshot = Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.ObservedToolExecution)).Providers);
            var client = CreateAgentObservedToolClient(owner, rpc, snapshot); await using var lifetime = (IAsyncDisposable)client;
            var context = new AgentToolExecutionContext(Guid.NewGuid()); var request = new AgentToolRequest("fixture.tool", "{}");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ExecuteAsync(context, request, cancelled.Token).AsTask());
                Assert.Equal(0, handler.Starts);
            }
            var pending = client.ExecuteAsync(context, request).AsTask(); await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await state.RetireAsync(handler).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => pending)).Error.Kind); Assert.True(handler.Disposed);
            handler = new("normal") { Gate = new() }; Add(state, handler);
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.ExecuteAsync(context, request).AsTask())).Error.Kind);
            var next = CreateAgentObservedToolClient(owner, rpc, Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.ObservedToolExecution)).Providers));
            await using var nextLifetime = (IAsyncDisposable)next;
            var stopping = next.ExecuteAsync(context, request).AsTask(); await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopped = nextLifetime.DisposeAsync().AsTask(); Assert.Same(stopped, nextLifetime.DisposeAsync().AsTask());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping); await stopped.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(handler.Disposed); Assert.All(rpc.Scopes, scope => Assert.True(scope.Disposed));
        }
        static void Add(RegressionTestRpcState state, ObservedToolPeer value) => state.Add("example.tools", RegressionTestRpcDescriptors.Get(AgentRpcContractIds.ObservedToolExecution),
            value, value, Noop.Instance, "fixture.tools.observed", "2.0.0");
    }

    private static IAgentObservedToolExecution CreateAgentObservedToolClient(Assembly owner, ISunderRpcClient rpc, SunderRpcProviderSnapshot selected)
        => AgentRoleBoundary.As<IAgentObservedToolExecution>(Activator.CreateInstance(owner.GetType("Sunder.Package.Agent.Runtime.Rpc.GeneratedObservedToolExecutionClient", true)!, rpc, selected)!);

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
            => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { sourceId = "fixture.tools" }));
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
                yield return JsonSerializer.SerializeToElement(new { kind = "result", result = new AgentToolResult("fixture.tool", "Completed 漢字", "result", "{opaque") }, MemoryProviderJson);
                AdvancedAfterResult = true;
                await Task.Delay(Timeout.Infinite, token);
            }
            finally { Disposed = true; }
        }
    }
}
