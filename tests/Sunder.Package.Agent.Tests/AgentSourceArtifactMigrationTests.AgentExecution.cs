using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static async Task VerifyAgentExecutionConsumer(Assembly owner)
    {
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var descriptor = RegressionTestRpcDescriptors.Get(AgentRpcContractIds.ExecutionTarget);
        var backend = new Sunder.Generated.Rpc.ExecutionTarget.SunderAgentExecutionTargetRpcHandler(
            new RegressionTestExecutionProvider(new AgentPublicSourceMigrationTests.PromptTargetProbe()));
        var handler = new SurfaceHandler(backend);
        Add(handler);
        var rpc = new FilesRpcObserver(new RegressionTestRpcClient(state, "sunder.package.agent"), "configuration-generation");
        var snapshot = Assert.Single((await rpc.DiscoverAsync(descriptor.ContractId)).Providers);
        var client = CreateAgentExecutionClient(owner, rpc, snapshot);
        await using var lifetime = (IAsyncDisposable)client;
        Func<CancellationToken, ValueTask<AgentExecutionTargetDescriptor>> describe = token => AgentRoleBoundary.As<ValueTask<AgentExecutionTargetDescriptor>>(AgentRoleBoundary.Call(client, "DescribeAsync", token)!);
        var supportsMethod = owner.GetType("Sunder.Package.Agent.Runtime.Rpc.AgentExecutionBindings", true)!.GetMethod("SupportsFacet", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool supports(IAgentExecutionTarget target, string facet) => (bool)supportsMethod.Invoke(null, [AgentRoleBoundary.Unwrap(target), facet])!;
        var seed = AgentPublicSourceMigrationTests.CreatePromptRequest();
        var context = new AgentExecutionTargetContext(seed.Session.SessionId, seed.Profile!.ProfileId, seed.Workspace!, seed.ExecutionBinding!, true)
        {
            ApprovedResourceReferences = ["durable.reference"], ApprovedResourceCapabilities = ["transient.capability"],
            CapturePostMutationResource = true, ExpectedConfigurationGeneration = "captured-generation",
            ResourceOperation = new(Guid.NewGuid(), 7, "call.execution", "files.mutate", -1, "workspace-generation", "binding-generation",
                "example.tools", "example.execution", Guid.NewGuid().ToString("N"), 2, true),
        };
        Assert.Equal("target.one", (await describe(default)).TargetId); Assert.Empty(client.Descriptor.Facets);
        Assert.Single(handler.Calls, call => call.Method == "describe");
        Assert.Null(await client.GetConfigurationGenerationAsync(context));
        Assert.True(JsonElement.DeepEquals(RegressionTestRpcValues.ExecutionContext(context),
            handler.Calls.Last(call => call.Method == "configuration-generation").Request));
        var calls = handler.Calls.Count;
        foreach (var facet in new[] { "process-execution", "resource-resolution", "resource-authority", "execution-scope", "path-mapping",
                     "path-environment", "ranged-file-read", "structured-file-search", "scoped-instruction-discovery" }) Assert.False(supports(client, facet));
        foreach (var invoke in new Func<Task>[]
        {
            () => ((IAgentProcessExecutionTarget)client).ExecuteProcessAsync(context, new("tool", [])).AsTask(),
            () => ((IAgentExecutionResourceResolver)client).ResolveResourcesAsync(context, []).AsTask(),
            () => ((IAgentResourceAuthorityExecutionTarget)client).ValidateResourceAuthorityAsync(context).AsTask(),
            () => Task.Run(() => ((IAgentResourceAuthorityExecutionTarget)client).ReleaseResourceAuthority([])),
            () => ((IAgentExecutionScopeProvider)client).GetExecutionScopeAsync(context).AsTask(),
            () => ((IAgentExecutionPathMapper)client).MapToHostPathAsync(context, "/workspace").AsTask(),
            () => ((IAgentExecutionPathEnvironment)client).ListPathEntriesAsync(context).AsTask(),
            () => ((IAgentExecutionPathEnvironment)client).AddPathEntryAsync(context, "/tools").AsTask(),
            () => ((IAgentStructuredFileSearchExecutionTarget)client).ExecuteFileSearchAsync(context, new(".", AgentFileSearchKind.Glob, "*")).AsTask(),
            () => ((IAgentScopedInstructionDiscoveryTarget)client).DiscoverScopedInstructionsAsync(context, new([new("/workspace", true)])).AsTask(),
        }) await Assert.ThrowsAsync<NotSupportedException>(invoke);
        Assert.Equal(calls, handler.Calls.Count);
        foreach (var change in new Func<SunderRpcProviderSnapshot, SunderRpcProviderSnapshot>[]
        {
            value => value with { PackageId = "other.package" }, value => value with { PackageVersion = "2.0.1" },
            value => value with { ProviderId = "other.provider" }, value => value with { ContractId = "other.contract" },
            value => value with { ContractVersion = "1.0.1" }, value => value with { DescriptorSha256 = new string('c', 64) },
            value => value with { AbiSha256 = new string('d', 64) }, value => value with { ActivationId = Guid.NewGuid() },
            value => value with { ActivationEpoch = value.ActivationEpoch + 1 }, value => value with { SessionGeneration = value.SessionGeneration + 1 },
            value => value with { Endpoint = new("other-endpoint") }, value => value with { State = SunderRpcProviderState.Inactive },
        })
        {
            rpc.Transform = change;
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.GetConfigurationGenerationAsync(context).AsTask())).Error.Kind);
        }
        rpc.Transform = null; rpc.InvalidateAfterInvoke = true;
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.GetConfigurationGenerationAsync(context).AsTask())).Error.Kind);
        rpc.InvalidateAfterInvoke = false; rpc.Transform = null;
        rpc.LookupFailure = SunderRpcErrorKind.PermissionDenied;
        Assert.Equal(SunderRpcErrorKind.PermissionDenied, (await Assert.ThrowsAsync<SunderRpcException>(() => client.GetConfigurationGenerationAsync(context).AsTask())).Error.Kind);
        rpc.LookupFailure = null;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); calls = handler.Calls.Count;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => describe(cancelled.Token).AsTask());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetConfigurationGenerationAsync(context, cancelled.Token).AsTask());
            Assert.Equal(calls, handler.Calls.Count);
        }
        handler.BlockMethod = "describe"; handler.Gate = new();
        var fresh = CreateAgentExecutionClient(owner, rpc, snapshot); await using var freshLifetime = (IAsyncDisposable)fresh;
        Func<CancellationToken, ValueTask<AgentExecutionTargetDescriptor>> freshDescribe = token => AgentRoleBoundary.As<ValueTask<AgentExecutionTargetDescriptor>>(AgentRoleBoundary.Call(fresh, "DescribeAsync", token)!);
        using (var cancelled = new CancellationTokenSource())
        {
            var pending = freshDescribe(cancelled.Token).AsTask(); await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        handler.BlockMethod = "configuration-generation"; handler.Gate = new();
        var retiring = client.GetConfigurationGenerationAsync(context).AsTask(); await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RetireAsync(handler).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => retiring)).Error.Kind);
        handler = new(backend); Add(handler);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.GetConfigurationGenerationAsync(context).AsTask())).Error.Kind);
        handler.Transform = (method, value) =>
        {
            if (method != "describe") return value;
            var node = JsonNode.Parse(value.GetRawText())!; node["facets"] = new JsonArray("execution-scope");
            return JsonSerializer.SerializeToElement(node);
        };
        var next = CreateAgentExecutionClient(owner, rpc, Assert.Single((await rpc.DiscoverAsync(descriptor.ContractId)).Providers));
        await using var nextLifetime = (IAsyncDisposable)next;
        var exposed = next.Descriptor; ((string[])exposed.Facets)[0] = "resource-authority";
        Assert.True(supports(next, "execution-scope")); Assert.False(supports(next, "resource-authority"));
        var unsupported = await Assert.ThrowsAsync<SunderRpcException>(() => ((IAgentExecutionScopeProvider)next).GetExecutionScopeAsync(context).AsTask());
        Assert.Equal(SunderRpcErrorKind.Domain, unsupported.Error.Kind); Assert.Equal("agent.execution.facet-unavailable", unsupported.Error.Code);
        handler.BlockMethod = "configuration-generation"; handler.Gate = new();
        var stopping = next.GetConfigurationGenerationAsync(context).AsTask(); await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = nextLifetime.DisposeAsync().AsTask(); Assert.Same(stopped, nextLifetime.DisposeAsync().AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping); await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, Assert.Throws<SunderRpcException>(() => next.Descriptor).Error.Kind);
        Assert.Equal(0, rpc.UnscopedLookups); Assert.All(rpc.Scopes, scope => Assert.True(scope.Disposed));

        void Add(SurfaceHandler value) => state.Add("example.execution", descriptor, value, value, Noop.Instance, "fixture.execution", "2.0.0");
    }

    private static IAgentExecutionTarget CreateAgentExecutionClient(Assembly owner, ISunderRpcClient rpc, SunderRpcProviderSnapshot provider)
        => AgentRoleBoundary.As<IAgentExecutionTarget>(Activator.CreateInstance(owner.GetType("Sunder.Package.Agent.Runtime.Rpc.GeneratedExecutionTargetClient", true)!, rpc, provider)!);
}
