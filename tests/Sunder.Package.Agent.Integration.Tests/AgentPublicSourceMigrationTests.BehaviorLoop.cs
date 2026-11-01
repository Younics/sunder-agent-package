using System.Reflection;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    private static async Task VerifyBehaviorLoopAsync(Assembly assembly, IReadOnlyDictionary<string, SunderRpcContractDescriptor> descriptors)
    {
        var loop = new BehaviorProbe();
        await using var fixture = await CreateBehaviorFixture(assembly, descriptors, loop);
        var host = new FullRunControlProbe();
        using var registration = fixture.Registry.Open(host, fixture.LoopPackage, out var invocationId);
        foreach (var descriptor in new[]
        {
            loop.Descriptor,
            loop.Descriptor with { SourceId = null, FeatureKinds = null, SettingsSchemaJson = null },
            loop.Descriptor with { FeatureKinds = [], SettingsSchemaJson = "" },
        })
        {
            loop.Descriptor = descriptor;
            var expected = RunJson(descriptor);
            var actual = await fixture.Client.DescribeAsync();
            Assert.True(JsonElement.DeepEquals(expected, RunJson(actual)));
        }
        foreach (var kind in Enum.GetValues<AgentBehaviorLoopCompletionKind>())
        {
            loop.Execute = async (context, runtime, token) =>
            {
                Assert.True(runtime.IsCurrentRun());
                Assert.True(JsonElement.DeepEquals(RunJson(host.Turn), RunJson(Assert.Single(runtime.ListRecentTurns(1)))));
                Assert.Equal(new AgentRunBudgetState(2, 3, 4000), Assert.IsAssignableFrom<IAgentRunBudgetRuntime>(runtime).GetRunBudgetState());
                await Assert.IsAssignableFrom<IAgentPromptContextAcknowledgmentRuntime>(runtime).AcknowledgePromptContextAsync([], token);
                return new(context.RunningCheckpoint, kind);
            };
            var expected = RunJson(new AgentBehaviorLoopResult(host.Context.RunningCheckpoint, kind));
            var arguments = RunJson(host.Context);
            var actual = await fixture.Client.RunAsync(host.Context, invocationId, fixture.ControlReference);
            Assert.True(JsonElement.DeepEquals(expected, RunJson(actual)));
            Assert.True(JsonElement.DeepEquals(arguments, RunJson(loop.Context)));
        }
        var minimal = host.Context with
        {
            Workspace = null, ExecutionBinding = null, ModelVariant = null, ModelModeOption = null, ModelSpeedOption = null,
            Profile = host.Context.Profile with { ModelBindings = null, SelectableCapabilityAssignments = null, BehaviorLoopSettingsJson = null },
            RunCapabilities = host.Context.RunCapabilities with { ContextWindowTokens = null, MaxOutputTokens = null },
        };
        await fixture.Client.RunAsync(minimal, invocationId, fixture.ControlReference);
        Assert.True(JsonElement.DeepEquals(RunJson(minimal), RunJson(loop.Context)));

        // Denial permanently retires received references. Keep that hostile
        // scenario separate from the later valid invocation-lifecycle checks.
        await using (var integrity = await CreateBehaviorFixture(assembly, descriptors, loop))
        {
            using var integrityGrant = integrity.Registry.Open(host, integrity.LoopPackage, out var integrityId);
            await VerifyBehaviorHandleIntegrityAsync(integrity, loop, host, integrityId);
        }
        await VerifyBehaviorRegistrationLifecycleAsync(fixture, loop, host);
        await VerifyBehaviorRetirementAsync(assembly, descriptors);
        await VerifyDefaultBehaviorAsync(assembly, descriptors);
    }

    private static async Task VerifyBehaviorHandleIntegrityAsync(BehaviorFixture fixture, BehaviorProbe loop, FullRunControlProbe host, Guid invocationId)
    {
        var calls = loop.Calls;
        fixture.State.SetProviderLookupFailure(fixture.LoopPackage, SunderRpcErrorKind.PermissionDenied);
        try
        {
            await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Client.RunAsync(host.Context, invocationId, fixture.ControlReference).AsTask());
            Assert.Equal(calls, loop.Calls);
            Assert.Equal(SunderRpcErrorKind.PermissionDenied, Assert.IsType<SunderRpcException>(fixture.State.LastInvocationFailure).Error.Kind);
        }
        finally { fixture.State.SetProviderLookupFailure(fixture.LoopPackage, null); }

        using var wrongCaller = fixture.Registry.Open(host, fixture.LoopPackage.ToUpperInvariant(), out var wrongId);
        var hostCalls = host.Calls;
        await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Client.RunAsync(host.Context, wrongId, fixture.ControlReference).AsTask());
        Assert.Equal(hostCalls, host.Calls);
    }

    private static async Task VerifyBehaviorRegistrationLifecycleAsync(BehaviorFixture fixture, BehaviorProbe loop, FullRunControlProbe host)
    {
        var bridge = fixture.Bridge;
        Assert.True(JsonElement.DeepEquals(RunJson(loop.Descriptor), RunJson(bridge.Descriptor)));
        loop.Execute = (context, runtime, _) =>
        {
            var calls = host.Calls;
            Assert.True(runtime.IsCurrentRun());
            Assert.Equal(calls + 1, host.Calls);
            return ValueTask.FromResult(new AgentBehaviorLoopResult(context.RunningCheckpoint, AgentBehaviorLoopCompletionKind.Completed));
        };
        var result = await bridge.RunAsync(host.Context, host);
        Assert.Equal(AgentBehaviorLoopCompletionKind.Completed, result.CompletionKind);
        var sent = Assert.IsType<BehaviorLoopRunRequest>(fixture.Owner.LastRun);
        Assert.InRange(fixture.Owner.LastRunOptions!.DeadlineUtc!.Value - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(34), TimeSpan.FromMinutes(36));
        await AssertClosedAsync(sent.InvocationId);
        var runtimeCalls = host.Calls;
        await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Client.RunAsync(sent.Context, sent.InvocationId, fixture.ControlReference).AsTask());
        Assert.Equal(runtimeCalls, host.Calls);

        loop.Execute = (_, _, _) => throw new IOException("private behavior implementation detail");
        var failure = await Assert.ThrowsAsync<SunderRpcException>(() => bridge.RunAsync(host.Context, host).AsTask());
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, failure.Error.Kind);
        Assert.DoesNotContain("private behavior", failure.Message, StringComparison.Ordinal);
        await AssertClosedAsync(fixture.Owner.LastRun!.InvocationId);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.Execute = async (_, _, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Canceled loop cannot complete.");
        };
        using var cancellation = new CancellationTokenSource();
        var pending = bridge.RunAsync(host.Context, host, cancellation.Token).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await AssertClosedAsync(fixture.Owner.LastRun!.InvocationId);

        async Task AssertClosedAsync(Guid id)
        {
            var calls = host.Calls;
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.ControlClient.IsCurrentAsync(id).AsTask());
            Assert.Equal(calls, host.Calls);
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, error.Error.Kind);
            Assert.Equal("rpc.provider.handler-fault", error.Error.Code);
            var rejected = Assert.IsType<SunderRpcException>(fixture.State.LastInvocationFailure);
            Assert.Equal(SunderRpcErrorKind.PermissionDenied, rejected.Error.Kind);
            Assert.Equal("agent.run-control.invalid-invocation", rejected.Error.Code);
        }
    }

    private static async Task VerifyBehaviorRetirementAsync(Assembly assembly, IReadOnlyDictionary<string, SunderRpcContractDescriptor> descriptors)
    {
        foreach (var retireControl in new[] { true, false })
        {
            var loop = new BehaviorProbe();
            await using var fixture = await CreateBehaviorFixture(assembly, descriptors, loop);
            var host = new FullRunControlProbe { Block = !retireControl };
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ownerRetired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var ownerRetirement = fixture.ControlReference.RetirementToken.Register(() => ownerRetired.TrySetResult());
            loop.Execute = async (context, runtime, token) =>
            {
                started.TrySetResult();
                if (retireControl)
                {
                    try { await Task.Delay(Timeout.Infinite, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        // The two receiver sessions observe retirement independently.
                        // This branch tests caller-owned reference retirement, not
                        // provider cancellation arriving before that owner signal.
                        await ownerRetired.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        throw;
                    }
                }
                else await runtime.BuildInstructionContextAsync(token);
                return new(context.RunningCheckpoint, AgentBehaviorLoopCompletionKind.Completed);
            };
            var pending = fixture.Bridge.RunAsync(host.Context, host).AsTask();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!retireControl) await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.State.RetireAsync(retireControl ? fixture.Control : fixture.Implementation).WaitAsync(TimeSpan.FromSeconds(5));
            if (retireControl)
            {
                // The transferred reference retired, not the caller token. Its
                // owned call must retain the SDK retirement/unsafe-dispatch result.
                var fault = await Assert.ThrowsAsync<SunderRpcException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(fault.Error.Kind == SunderRpcErrorKind.Unavailable,
                    $"Run-control retirement boundary: kind={fault.Error.Kind}, code={fault.Error.Code}, "
                     + $"dispatch={fault.Error.DispatchState}, retry={fault.Error.RetrySafety}; "
                     + $"owner reference retired={fixture.ControlReference.RetirementToken.IsCancellationRequested}; "
                     + $"provider failure={fixture.State.LastInvocationFailure}");
                Assert.Equal("rpc.binding.retired", fault.Error.Code);
                Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, fault.Error.DispatchState);
                Assert.Equal(SunderRpcRetrySafety.Unsafe, fault.Error.RetrySafety);
                var cancellation = Assert.IsAssignableFrom<OperationCanceledException>(fixture.State.LastInvocationFailure);
                Assert.True(cancellation.CancellationToken.IsCancellationRequested);
            }
            else Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)))).Error.Kind);
            if (retireControl)
            {
                var retired = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.ControlClient.IsCurrentAsync(fixture.Owner.LastRun!.InvocationId).AsTask());
                Assert.Equal(SunderRpcErrorKind.StaleEndpoint, retired.Error.Kind);
                var stale = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.ControlReference.EnsureCurrentAsync().AsTask());
                Assert.Equal(SunderRpcErrorKind.StaleEndpoint, stale.Error.Kind);
            }
            else
            {
                var closed = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.ControlClient.IsCurrentAsync(fixture.Owner.LastRun!.InvocationId).AsTask());
                Assert.Equal(SunderRpcErrorKind.ProviderFaulted, closed.Error.Kind);
                Assert.Equal("rpc.provider.handler-fault", closed.Error.Code);
                var denied = Assert.IsType<SunderRpcException>(fixture.State.LastInvocationFailure);
                Assert.Equal(SunderRpcErrorKind.PermissionDenied, denied.Error.Kind);
                Assert.Equal("agent.run-control.invalid-invocation", denied.Error.Code);
            }
            if (retireControl)
            {
                var controlDescriptor = descriptors[RegressionTestBindings.RunControls];
                var foreignControl = new AgentRunControlService((AgentActiveRunRegistry)fixture.Registry.Value);
                var foreignRegistration = fixture.OwnerReceiver.Register(foreignControl, controlDescriptor.ContractId);
                fixture.State.Add("test.foreign.control", controlDescriptor, foreignControl, foreignRegistration.Handler,
                    EmptyDisposable.Instance, "agent.run.control", "2.0.0");
                var differentControl = new AgentRunControlService((AgentActiveRunRegistry)fixture.Registry.Value);
                var differentRegistration = fixture.OwnerReceiver.Register(differentControl, controlDescriptor.ContractId);
                fixture.State.Add("younics.ai.agent", controlDescriptor, differentControl, differentRegistration.Handler,
                    EmptyDisposable.Instance, "different.run.control", "2.0.0");
                var calls = loop.Calls;
                var peerCalls = fixture.State.InvocationAttemptCount;
                var unavailable = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Bridge.RunAsync(host.Context, host).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(SunderRpcErrorKind.StaleEndpoint, unavailable.Error.Kind);
                Assert.Equal("rpc.stale-endpoint", unavailable.Error.Code);
                Assert.Equal(peerCalls, fixture.State.InvocationAttemptCount);
                Assert.Equal(calls, loop.Calls);

                var registration = fixture.OwnerReceiver.Register(fixture.Control, controlDescriptor.ContractId);
                fixture.State.Add("younics.ai.agent", controlDescriptor, fixture.Control, registration.Handler, EmptyDisposable.Instance, registration.ProviderId, "2.0.0");
                using var invocation = fixture.Registry.Open(host, fixture.LoopPackage, out var id);
                var retained = fixture.Owner.LastRun! with { InvocationId = id };
                var stale = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Client.RunAsync(retained.Context, retained.InvocationId, fixture.ControlReference).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(SunderRpcErrorKind.StaleEndpoint, stale.Error.Kind);
                Assert.Equal("rpc.stale-endpoint", stale.Error.Code);
                Assert.Equal(peerCalls, fixture.State.InvocationAttemptCount);
                Assert.Equal(calls, loop.Calls);
            }
        }
    }
}
