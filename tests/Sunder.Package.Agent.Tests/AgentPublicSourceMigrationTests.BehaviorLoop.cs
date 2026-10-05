using System.Reflection;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
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
        var request = new BehaviorLoopRunRequest(invocationId, fixture.ControlHandle, host.Context);
        foreach (var descriptor in new[]
        {
            loop.Descriptor,
            loop.Descriptor with { SourceId = null, FeatureKinds = null, SettingsSchemaJson = null },
            loop.Descriptor with { FeatureKinds = [], SettingsSchemaJson = "" },
        })
        {
            loop.Descriptor = descriptor;
            var expected = RunJson(descriptor);
            var actual = await InvokeGeneratedClient(fixture.Client, "DescribeAsync", EmptyRequest());
            Assert.True(JsonElement.DeepEquals(expected, actual));
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
            var actual = await InvokeGeneratedClient(fixture.Client, "RunAsync", RunJson(request));
            Assert.True(JsonElement.DeepEquals(expected, actual));
            Assert.True(JsonElement.DeepEquals(arguments, RunJson(loop.Context)));
        }
        var minimal = host.Context with
        {
            Workspace = null, ExecutionBinding = null, ModelVariant = null, ModelModeOption = null, ModelSpeedOption = null,
            Profile = host.Context.Profile with { ModelBindings = null, SelectableCapabilityAssignments = null, BehaviorLoopSettingsJson = null },
            RunCapabilities = host.Context.RunCapabilities with { ContextWindowTokens = null, MaxOutputTokens = null },
        };
        await InvokeGeneratedClient(fixture.Client, "RunAsync", RunJson(request with { Context = minimal }));
        Assert.True(JsonElement.DeepEquals(RunJson(minimal), RunJson(loop.Context)));

        await VerifyBehaviorHandleIntegrityAsync(fixture, loop, host, request);
        await VerifyBehaviorRegistrationLifecycleAsync(fixture, loop, host);
        await VerifyBehaviorRetirementAsync(assembly, descriptors);
        await VerifyDefaultBehaviorAsync(assembly, descriptors);
    }

    private static async Task VerifyBehaviorHandleIntegrityAsync(BehaviorFixture fixture, BehaviorProbe loop, FullRunControlProbe host, BehaviorLoopRunRequest request)
    {
        var handle = request.RunControl;
        var mismatches = new[]
        {
            handle with { PackageId = "different.package" }, handle with { PackageVersion = "2.0.1" },
            handle with { ProviderId = "different.provider" }, handle with { ContractId = "different.contract" },
            handle with { ContractVersion = "1.0.1" }, handle with { DescriptorSha256 = new string('0', 64) },
            handle with { AbiSha256 = new string('0', 64) }, handle with { ActivationId = Guid.NewGuid() },
            handle with { ActivationEpoch = handle.ActivationEpoch + 1 }, handle with { SessionGeneration = handle.SessionGeneration + 1 },
            handle with { EndpointReference = "test.rpc.missing" },
        };
        var calls = loop.Calls;
        foreach (var mismatch in mismatches)
        {
            var invalid = RunJson(request with { RunControl = mismatch });
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => InvokeGeneratedClient(fixture.Client, "RunAsync", invalid));
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, error.Error.Kind);
            Assert.Equal(mismatch.ContractId == handle.ContractId ? SunderRpcErrorKind.StaleEndpoint : SunderRpcErrorKind.Validation,
                Assert.IsType<SunderRpcException>(fixture.State.LastInvocationFailure).Error.Kind);
            Assert.Equal(calls, loop.Calls);
        }
        fixture.State.SetProviderLookupFailure(fixture.LoopPackage, SunderRpcErrorKind.PermissionDenied);
        try
        {
            await Assert.ThrowsAsync<SunderRpcException>(() => InvokeGeneratedClient(fixture.Client, "RunAsync", RunJson(request)));
            Assert.Equal(calls, loop.Calls);
            Assert.Equal(SunderRpcErrorKind.PermissionDenied, Assert.IsType<SunderRpcException>(fixture.State.LastInvocationFailure).Error.Kind);
        }
        finally { fixture.State.SetProviderLookupFailure(fixture.LoopPackage, null); }

        using var wrongCaller = fixture.Registry.Open(host, fixture.LoopPackage.ToUpperInvariant(), out var wrongId);
        var hostCalls = host.Calls;
        await Assert.ThrowsAsync<SunderRpcException>(() => InvokeGeneratedClient(fixture.Client, "RunAsync", RunJson(request with { InvocationId = wrongId })));
        Assert.Equal(hostCalls, host.Calls);
    }

    private static async Task VerifyBehaviorRegistrationLifecycleAsync(BehaviorFixture fixture, BehaviorProbe loop, FullRunControlProbe host)
    {
        var bridge = fixture.Bridge;
        Assert.True(JsonElement.DeepEquals(RunJson(loop.Descriptor), RunJson(bridge.Descriptor)));
        loop.Execute = (context, runtime, _) =>
        {
            Assert.True(runtime.IsCurrentRun());
            Assert.Same(host, fixture.Registry.Resolve(fixture.Owner.LastRun!.InvocationId, fixture.LoopPackage));
            Assert.Throws<SunderRpcException>(() => fixture.Registry.Resolve(fixture.Owner.LastRun.InvocationId, "sunder.package.agent"));
            return ValueTask.FromResult(new AgentBehaviorLoopResult(context.RunningCheckpoint, AgentBehaviorLoopCompletionKind.Completed));
        };
        var result = await bridge.RunAsync(host.Context, host);
        Assert.Equal(AgentBehaviorLoopCompletionKind.Completed, result.CompletionKind);
        var sent = Assert.IsType<BehaviorLoopRunRequest>(fixture.Owner.LastRun);
        Assert.Equal(fixture.ControlHandle, sent.RunControl);
        Assert.InRange(fixture.Owner.LastRunOptions!.DeadlineUtc!.Value - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(34), TimeSpan.FromMinutes(36));
        AssertClosed(sent.InvocationId);
        var runtimeCalls = host.Calls;
        await Assert.ThrowsAsync<SunderRpcException>(() => InvokeGeneratedClient(fixture.Client, "RunAsync", RunJson(sent)));
        Assert.Equal(runtimeCalls, host.Calls);

        loop.Execute = (_, _, _) => throw new IOException("private behavior implementation detail");
        var failure = await Assert.ThrowsAsync<SunderRpcException>(() => bridge.RunAsync(host.Context, host).AsTask());
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, failure.Error.Kind);
        Assert.DoesNotContain("private behavior", failure.Message, StringComparison.Ordinal);
        AssertClosed(fixture.Owner.LastRun!.InvocationId);

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
        AssertClosed(fixture.Owner.LastRun!.InvocationId);

        void AssertClosed(Guid id)
            => Assert.Equal("agent.run-control.invalid-invocation", Assert.Throws<SunderRpcException>(() => fixture.Registry.Resolve(id, fixture.LoopPackage)).Error.Code);
    }

    private static async Task VerifyBehaviorRetirementAsync(Assembly assembly, IReadOnlyDictionary<string, SunderRpcContractDescriptor> descriptors)
    {
        foreach (var retireControl in new[] { true, false })
        {
            var loop = new BehaviorProbe();
            await using var fixture = await CreateBehaviorFixture(assembly, descriptors, loop);
            var host = new FullRunControlProbe { Block = !retireControl };
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            loop.Execute = async (context, runtime, token) =>
            {
                started.TrySetResult();
                if (retireControl) await Task.Delay(Timeout.Infinite, token);
                else await runtime.BuildInstructionContextAsync(token);
                return new(context.RunningCheckpoint, AgentBehaviorLoopCompletionKind.Completed);
            };
            var pending = fixture.Bridge.RunAsync(host.Context, host).AsTask();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!retireControl) await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.State.RetireAsync(retireControl ? fixture.Control : fixture.Implementation).WaitAsync(TimeSpan.FromSeconds(5));
            if (retireControl) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            else Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)))).Error.Kind);
            Assert.Throws<SunderRpcException>(() => fixture.Registry.Resolve(fixture.Owner.LastRun!.InvocationId, fixture.LoopPackage));
            if (retireControl)
            {
                var controlDescriptor = descriptors[AgentRpcContractIds.RunControl];
                fixture.State.Add("test.foreign.control", controlDescriptor, new object(), new Rpc.Public.RunControlService(fixture.Registry).CreateSunderRpcProviderRegistration().Handler,
                    EmptyDisposable.Instance, "agent.run.control", "2.0.0");
                fixture.State.Add("sunder.package.agent", controlDescriptor, new object(), new Rpc.Public.RunControlService(fixture.Registry).CreateSunderRpcProviderRegistration().Handler,
                    EmptyDisposable.Instance, "different.run.control", "2.0.0");
                var calls = loop.Calls;
                var unavailable = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Bridge.RunAsync(host.Context, host).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal("agent.run-control.unavailable", unavailable.Error.Code);
                Assert.Equal(calls, loop.Calls);

                var registration = (Sunder.Sdk.Worker.SunderWorkerProviderRegistration)fixture.Control.GetType()
                    .GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(fixture.Control, null)!;
                fixture.State.Add("sunder.package.agent", controlDescriptor, fixture.Control, registration.Handler, EmptyDisposable.Instance, registration.ProviderId, "2.0.0");
                using var invocation = fixture.Registry.Open(host, fixture.LoopPackage, out var id);
                var retained = fixture.Owner.LastRun! with { InvocationId = id };
                await Assert.ThrowsAsync<SunderRpcException>(() => InvokeGeneratedClient(fixture.Client, "RunAsync", RunJson(retained)).WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal("agent.rpc.provider-stale", Assert.IsType<SunderRpcException>(fixture.State.LastInvocationFailure).Error.Code);
                Assert.Equal(calls, loop.Calls);
            }
        }
    }
}
