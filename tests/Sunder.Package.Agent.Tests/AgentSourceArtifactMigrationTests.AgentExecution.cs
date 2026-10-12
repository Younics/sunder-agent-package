using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static async Task VerifyAgentExecutionConsumer(Assembly owner)
    {
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new FilesRpcObserver(new RegressionTestRpcClient(state, "sunder.package.agent"), "configuration-generation");
        var receiver = new GeneratedServiceTestScope(rpc);
        using var providers = new RegressionTestSourceProviders();
        var descriptor = CurrentPublicRpcDescriptors.Get(RegressionTestBindings.ExecutionTargets);
        var target = new AgentPublicSourceMigrationTests.PromptTargetProbe();
        var registration = providers.Create(descriptor.ContractId, target, receiver);
        var handler = new SurfaceHandler(registration.Handler);
        Add(handler);
        var client = Assert.Single((await receiver.Providers<AgentServices.IExecutionTargetService>().ListAsync()).Providers).Service;
        AgentExecutionTargetDescriptor? awaitedDescriptor = null;
        Func<CancellationToken, ValueTask<AgentExecutionTargetDescriptor>> describe = client.DescribeAsync;
        bool supports(string facet) => (awaitedDescriptor ?? throw new InvalidOperationException()).SupportsFacet(facet);
        var seed = AgentPublicSourceMigrationTests.CreatePromptRequest();
        var context = new AgentExecutionTargetContext(seed.Session.SessionId, seed.Profile!.ProfileId, seed.Workspace!, seed.ExecutionBinding!, true)
        {
            ApprovedResourceReferences = ["durable.reference"], ApprovedResourceCapabilities = ["transient.capability"],
            CapturePostMutationResource = true, ExpectedConfigurationGeneration = "captured-generation",
            ResourceOperation = new(Guid.NewGuid(), 7, "call.execution", "files.mutate", -1, "workspace-generation", "binding-generation",
                "example.tools", "example.execution", Guid.NewGuid().ToString("N"), 2, true),
        };
        awaitedDescriptor = await describe(default);
        Assert.Equal("target.one", awaitedDescriptor.TargetId); Assert.Empty(awaitedDescriptor.Facets);
        Assert.Single(handler.Calls, call => call.Method == "describe");
        Assert.Null(await client.ConfigurationGenerationAsync(context));
        var wireOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) },
        };
        var expectedContext = JsonSerializer.SerializeToElement(new
        {
            context.SessionId, context.ProfileId, context.Workspace, context.Binding, context.AllowOutsideConfiguredScope,
            context.ApprovedResourceReferences, context.ApprovedResourceClaims, context.ApprovedResourceCapabilities,
            ResourceOperation = new
            {
                context.ResourceOperation.RunId, context.ResourceOperation.RunRevision, context.ResourceOperation.ToolCallId,
                context.ResourceOperation.ActionId, context.ResourceOperation.ResourceIndex, context.ResourceOperation.WorkspaceGeneration,
                context.ResourceOperation.BindingGeneration, context.ResourceOperation.ToolOwnerPackageId,
                context.ResourceOperation.ExecutionTargetOwnerPackageId, context.ResourceOperation.AuthorityActivationId,
                context.ResourceOperation.AuthorityUseCount, context.ResourceOperation.CanIssueOutsideAuthority,
            },
            context.CapturePostMutationResource, context.ExpectedConfigurationGeneration,
        }, wireOptions);
        var actualContext = handler.Calls.Last(call => call.Method == "configuration-generation").Request;
        Assert.True(JsonElement.DeepEquals(expectedContext, actualContext),
            string.Join(Environment.NewLine, PromptWireDifferences(expectedContext, actualContext, "$")));
        var calls = handler.Calls.Count;
        var descriptions = target.Describes;
        foreach (var facet in new[] { "process-execution", "resource-resolution", "resource-authority", "execution-scope", "path-mapping",
                      "path-environment", "ranged-file-read", "structured-file-search", "scoped-instruction-discovery" }) Assert.False(supports(facet));
        foreach (var invoke in new Func<Task>[]
        {
            () => client.ExecuteProcessAsync(context, new("tool", [])).AsTask(),
            () => client.ResolveResourcesAsync(context, []).AsTask(),
            () => client.ValidateResourceAuthorityAsync(context).AsTask(),
            () => client.ReleaseResourceAuthorityAsync([]).AsTask(),
            () => client.ExecutionScopeAsync(context).AsTask(),
            () => client.MapPathAsync(context, "/workspace").AsTask(),
            () => client.ListPathAsync(context).AsTask(),
            () => client.AddPathAsync(context, "/tools").AsTask(),
            () => client.StructuredFileSearchAsync(context, new(".", AgentFileSearchKind.Glob, "*")).AsTask(),
            () => client.DiscoverInstructionsAsync(context, new([new("/workspace", true)])).AsTask(),
        })
        {
            var unavailable = await Assert.ThrowsAsync<SunderRpcException>(invoke);
            Assert.Equal(SunderRpcErrorKind.Domain, unavailable.Error.Kind);
            Assert.Equal("agent.execution.facet-unavailable", unavailable.Error.Code);
        }
        Assert.Equal(calls + 10, handler.Calls.Count);
        // The generated service dispatches the request, but its business facet guard
        // rejects it before any unsupported target operation is invoked.
        Assert.Equal(descriptions, target.Describes);
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
            var selection = await SelectAsync();
            try
            {
                rpc.Transform = change;
                var before = handler.Calls.Count;
                Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => selection.Service.ConfigurationGenerationAsync(context).AsTask())).Error.Kind);
                Assert.Equal(before, handler.Calls.Count);
            }
            finally
            {
                rpc.Transform = null;
                await selection.Scope.DisposeAsync();
            }
        }
        var invalidated = await SelectAsync();
        try
        {
            rpc.InvalidateAfterInvoke = true;
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => invalidated.Service.ConfigurationGenerationAsync(context).AsTask())).Error.Kind);
        }
        finally
        {
            rpc.InvalidateAfterInvoke = false;
            rpc.Transform = null;
            await invalidated.Scope.DisposeAsync();
        }
        var permissionDenied = await SelectAsync();
        try
        {
            rpc.LookupFailure = SunderRpcErrorKind.PermissionDenied;
            var before = handler.Calls.Count;
            Assert.Equal(SunderRpcErrorKind.PermissionDenied, (await Assert.ThrowsAsync<SunderRpcException>(() => permissionDenied.Service.ConfigurationGenerationAsync(context).AsTask())).Error.Kind);
            Assert.Equal(before, handler.Calls.Count);
        }
        finally
        {
            rpc.LookupFailure = null;
            await permissionDenied.Scope.DisposeAsync();
        }
        var cancellable = await SelectAsync();
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); calls = handler.Calls.Count;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancellable.Service.DescribeAsync(cancelled.Token).AsTask());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancellable.Service.ConfigurationGenerationAsync(context, cancelled.Token).AsTask());
            Assert.Equal(calls, handler.Calls.Count);
        }
        await cancellable.Scope.DisposeAsync();
        handler.BlockMethod = "describe"; handler.Gate = new();
        var fresh = await SelectAsync();
        Func<CancellationToken, ValueTask<AgentExecutionTargetDescriptor>> freshDescribe = fresh.Service.DescribeAsync;
        using (var cancelled = new CancellationTokenSource())
        {
            var pending = freshDescribe(cancelled.Token).AsTask(); await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        await fresh.Scope.DisposeAsync();
        handler.BlockMethod = "configuration-generation"; handler.Gate = new();
        var retiringSelection = await SelectAsync();
        var retiring = retiringSelection.Service.ConfigurationGenerationAsync(context).AsTask(); await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RetireAsync(handler).WaitAsync(TimeSpan.FromSeconds(5));
        var retired = await Assert.ThrowsAsync<SunderRpcException>(() => retiring);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, retired.Error.Kind);
        await retiringSelection.Scope.DisposeAsync();
        handler = new(registration.Handler); Add(handler);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.ConfigurationGenerationAsync(context).AsTask())).Error.Kind);
        handler.Transform = (method, value) =>
        {
            if (method != "describe") return value;
            var node = JsonNode.Parse(value.GetRawText())!; node["facets"] = new JsonArray("execution-scope");
            return JsonSerializer.SerializeToElement(node);
        };
        var nextReceiver = new GeneratedServiceTestScope(rpc);
        var next = Assert.Single((await nextReceiver.Providers<AgentServices.IExecutionTargetService>().ListAsync()).Providers).Service;
        var exposed = await next.DescribeAsync();
        Assert.IsAssignableFrom<IList<string>>(exposed.Facets)[0] = "resource-authority";
        awaitedDescriptor = await next.DescribeAsync();
        Assert.True(supports("execution-scope")); Assert.False(supports("resource-authority"));
        var unsupported = await Assert.ThrowsAsync<SunderRpcException>(() => next.ExecutionScopeAsync(context).AsTask());
        Assert.Equal(SunderRpcErrorKind.Domain, unsupported.Error.Kind); Assert.Equal("agent.execution.facet-unavailable", unsupported.Error.Code);
        handler.BlockMethod = "configuration-generation"; handler.Gate = new();
        var stopping = next.ConfigurationGenerationAsync(context).AsTask(); await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = nextReceiver.DisposeAsync().AsTask(); Assert.Same(stopped, nextReceiver.DisposeAsync().AsTask());
        var stoppedCall = await Assert.ThrowsAsync<SunderRpcException>(() => stopping);
        Assert.Equal(SunderRpcErrorKind.Unavailable, stoppedCall.Error.Kind);
        Assert.Equal("rpc.binding.retired", stoppedCall.Error.Code);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, stoppedCall.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, stoppedCall.Error.RetrySafety);
        await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => next.DescribeAsync().AsTask());
        await receiver.DisposeAsync();
        rpc.AssertScopedOperations(); Assert.All(rpc.Scopes, scope => Assert.True(scope.Disposed));

        void Add(SurfaceHandler value) => state.Add("example.execution", descriptor, value, value, Noop.Instance, registration.ProviderId, "2.0.0");

        async Task<(GeneratedServiceTestScope Scope, AgentServices.IExecutionTargetService Service)> SelectAsync()
        {
            var scope = new GeneratedServiceTestScope(rpc);
            try
            {
                var service = Assert.Single((await scope.Providers<AgentServices.IExecutionTargetService>().ListAsync()).Providers).Service;
                return (scope, service);
            }
            catch
            {
                await scope.DisposeAsync();
                throw;
            }
        }
    }
}
