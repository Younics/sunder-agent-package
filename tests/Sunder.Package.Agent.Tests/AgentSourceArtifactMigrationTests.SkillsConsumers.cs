using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifySkillsConsumers(Type type)
    {
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var client = new MemoryConsumerRpc(new RegressionTestRpcClient(state, "sunder.package.agent.skills"));
        var instance = Activator.CreateInstance(type, client)!;
        await using var owned = (IAsyncDisposable)instance;
        Assert.Null(await profile("profile.generated", default));
        Assert.Equal(0, client.DiscoveryCount);
        Assert.Equal(0, client.ActiveWatches);
        await type.GetMethod("StartAsync")!.CreateDelegate<Func<CancellationToken, ValueTask>>(instance)(default);
        Assert.Null(await profile("profile.generated", default));
        var runtime = new MemoryConsumerRuntime();
        var catalog = new SkillsConsumerHandler(new Rpc.Public.RuntimeCatalogService(runtime).CreateSunderRpcProviderRegistration().Handler);
        AddCatalog(catalog);
        Assert.Equal(runtime.Profile.ProfileId, (await profile(runtime.Profile.ProfileId, default))!.ProfileId);
        Assert.Null(await profile("missing", default));
        var target = new SkillsConsumerHandler();
        AddTarget(target);
        var handle = SelectTarget();
        var now = DateTimeOffset.UtcNow;
        var context = new AgentExecutionTargetContext(Guid.NewGuid(), runtime.Profile.ProfileId,
            new("workspace.skills", "Skills", null, now, now),
            new("binding.skills", "workspace.skills", AgentRpcContractIds.ExecutionTarget, "test-target", "execution", true, 0, now, now));
        AgentExecutionResourceDescriptor[] resources = [new("skill.fixture", "skill", "sunder.package.agent.skills", "Fixture skill", "/host/skills/fixture", "/sunder/skills/fixture",
            AgentExecutionResourceAccessMode.ReadOnly, new Dictionary<string, string> { ["skill_id"] = "skill.fixture" })];
        Assert.Empty(await resolve(context, resources, null, default));
        var result = Assert.Single(await resolve(context, resources, handle, default));
        Assert.Equal("/executor/skill.fixture", result.ExecutionPath);
        Assert.Equal(resources[0].HostPath, result.HostPath);
        Assert.Equal(AgentExecutionResourceAccessMode.ReadOnly, result.AccessMode);
        Assert.Equal("skill.fixture", result.Metadata!["skill_id"]);
        Assert.Equal(context.Binding.BindingId, target.LastRequest.GetProperty("context").GetProperty("context").GetProperty("binding").GetProperty("bindingId").GetString());
        Assert.Empty(target.LastRequest.GetProperty("context").GetProperty("approvedResourceCapabilities").EnumerateArray());
        var count = target.Calls;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var discoveries = client.DiscoveryCount;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => profile(runtime.Profile.ProfileId, cancelled.Token).AsTask());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolve(context, resources, handle, cancelled.Token).AsTask());
            Assert.Equal(discoveries, client.DiscoveryCount);
            Assert.Equal(count, target.Calls);
        }
        Assert.Empty(await resolve(context with { Binding = context.Binding with { ContributionId = "wrong-target" } }, resources, handle, default));
        target.HasFacet = false;
        Assert.Empty(await resolve(context, resources, handle, default));
        target.HasFacet = true;
        Assert.Equal(count, target.Calls);
        Assert.Single(await resolve(context with { Binding = context.Binding with { ContributionId = "TEST" } }, resources, handle, default));
        foreach (var stale in new[] { handle with { SessionGeneration = handle.SessionGeneration + 1 }, handle with { ActivationEpoch = handle.ActivationEpoch + 1 },
                     handle with { ActivationId = Guid.NewGuid() }, handle with { PackageVersion = "2.0.1" }, handle with { DescriptorSha256 = new('0', 64) } })
        {
            count = target.Calls;
            Assert.Empty(await resolve(context, resources, stale, default));
            Assert.Equal(count, target.Calls);
        }
        client.LookupFailure = SunderRpcErrorKind.Unavailable;
        Assert.Empty(await resolve(context, resources, handle, default));
        Assert.Null(await profile(runtime.Profile.ProfileId, default));
        client.LookupFailure = SunderRpcErrorKind.PermissionDenied;
        Assert.Equal(SunderRpcErrorKind.PermissionDenied, (await Assert.ThrowsAsync<SunderRpcException>(() => resolve(context, resources, handle, default).AsTask())).Error.Kind);
        Assert.Equal(SunderRpcErrorKind.PermissionDenied, (await Assert.ThrowsAsync<SunderRpcException>(() => profile(runtime.Profile.ProfileId, default).AsTask())).Error.Kind);
        client.LookupFailure = null;
        target.Failure = SunderRpcErrorKind.PermissionDenied;
        // A provider cannot mint a Host-authenticated denial or availability failure.
        // The real Worker boundary sanitizes both; Skills must propagate that fault.
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, (await Assert.ThrowsAsync<SunderRpcException>(() => resolve(context, resources, handle, default).AsTask())).Error.Kind);
        target.Failure = SunderRpcErrorKind.Unavailable;
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, (await Assert.ThrowsAsync<SunderRpcException>(() => resolve(context, resources, handle, default).AsTask())).Error.Kind);
        target.Failure = null;
        target.AfterResponse = () => client.LookupTransform = value => value with { SessionGeneration = value.SessionGeneration + 1 };
        Assert.Empty(await resolve(context, resources, handle, default));
        client.LookupTransform = null;
        target.AfterResponse = null;
        catalog.AfterResponse = () => client.LookupTransform = value => value with { SessionGeneration = value.SessionGeneration + 1 };
        Assert.Null(await profile(runtime.Profile.ProfileId, default));
        catalog.AfterResponse = null;
        client.LookupTransform = null;

        target.Hold();
        using (var caller = new CancellationTokenSource())
        {
            var cancelled = resolve(context, resources, handle, caller.Token).AsTask();
            await target.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            var failure = await Record.ExceptionAsync(() => cancelled.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(failure is OperationCanceledException || failure is SunderRpcException { Error.Kind: SunderRpcErrorKind.Cancelled });
        }
        target.Hold();
        var retiring = resolve(context, resources, handle, default).AsTask();
        await target.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RetireAsync(target).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(await retiring.WaitAsync(TimeSpan.FromSeconds(5)));
        var replacement = new SkillsConsumerHandler();
        AddTarget(replacement);
        Assert.Empty(await resolve(context, resources, handle, default));
        Assert.Equal(0, replacement.Calls);
        handle = SelectTarget();
        Assert.Single(await resolve(context, resources, handle, default));
        catalog.Hold();
        var retiringProfile = profile(runtime.Profile.ProfileId, default).AsTask();
        await catalog.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RetireAsync(catalog).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await retiringProfile.WaitAsync(TimeSpan.FromSeconds(5)));
        AddCatalog(new(new Rpc.Public.RuntimeCatalogService(runtime).CreateSunderRpcProviderRegistration().Handler));
        Assert.NotNull(await profile(runtime.Profile.ProfileId, default));

        replacement.Hold();
        var inFlight = resolve(context, resources, handle, default).AsTask();
        await replacement.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = owned.DisposeAsync().AsTask();
        Assert.Same(stopping, owned.DisposeAsync().AsTask());
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(await inFlight.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, client.ActiveWatches);
        foreach (var scope in client.Scopes)
            await Assert.ThrowsAsync<ObjectDisposedException>(() => scope.GetProviderAsync(new(handle.EndpointReference)).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => profile(runtime.Profile.ProfileId, default).AsTask());

        void AddTarget(SkillsConsumerHandler value) => state.Add("example.skills.executor", RegressionTestRpcDescriptors.Get(AgentRpcContractIds.ExecutionTarget), value, value, Noop.Instance, "skills.execution.target", "2.0.0");
        void AddCatalog(SkillsConsumerHandler value) => state.Add("sunder.package.agent", RegressionTestRpcDescriptors.Get(AgentRpcContractIds.RuntimeCatalog), value, value, Noop.Instance, "agent.runtime.catalog", "2.0.0");
        AgentRpcProviderHandle SelectTarget()
            => ToProviderHandle(Assert.Single(state.Discover(AgentRpcContractIds.ExecutionTarget).Providers));

        async ValueTask<AgentProfileRecord?> profile(string? id, CancellationToken token)
        {
            var value = await Call(instance, "GetProfileAsync", id, token);
            if (value is null) return null;
            Assert.Same(type.Assembly, value.GetType().Assembly);
            return JsonSerializer.SerializeToElement(value, MemoryProviderJson).Deserialize<AgentProfileRecord>(MemoryProviderJson);
        }
        async ValueTask<IReadOnlyList<AgentResolvedExecutionResource>> resolve(AgentExecutionTargetContext context,
            IReadOnlyList<AgentExecutionResourceDescriptor> resources, AgentRpcProviderHandle? handle, CancellationToken token)
        {
            var parameters = type.GetMethod("ResolveResourcesAsync")!.GetParameters();
            object? Local(object? value, int index) => value is null ? null
                : JsonSerializer.SerializeToElement(value, MemoryProviderJson).Deserialize(parameters[index].ParameterType, MemoryProviderJson);
            var result = (await Call(instance, "ResolveResourcesAsync", Local(context, 0), Local(resources, 1), Local(handle, 2), token))!;
            Assert.All(((System.Collections.IEnumerable)result).Cast<object>(), value => Assert.Same(type.Assembly, value.GetType().Assembly));
            return JsonSerializer.SerializeToElement(result, MemoryProviderJson).Deserialize<AgentResolvedExecutionResource[]>(MemoryProviderJson)!;
        }
    }

    private static AgentRpcProviderHandle ToProviderHandle(SunderRpcProviderSnapshot value)
        => new(value.PackageId, value.PackageVersion, value.ProviderId, value.ContractId, value.ContractVersion, value.DescriptorSha256,
            value.AbiSha256, value.ActivationId, value.ActivationEpoch, value.SessionGeneration, value.Endpoint.Value);

    private sealed class SkillsConsumerHandler(ISunderRpcServiceHandler? inner = null) : ISunderRpcServiceHandler
    {
        internal bool HasFacet = true;
        internal int Calls;
        internal bool Block;
        internal SunderRpcErrorKind? Failure;
        internal JsonElement LastRequest;
        internal Action? AfterResponse;
        internal TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Hold() { Block = true; Started = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public async ValueTask<JsonElement> InvokeUnaryAsync(SunderRpcInvocationContext context, string serviceId, string methodId, JsonElement request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (inner is null && methodId == "describe") return JsonSerializer.SerializeToElement(new AgentExecutionTargetDescriptor("test", "test-target", "Skills Target", null, false, false)
            { Facets = HasFacet ? ["resource-resolution"] : [] }, MemoryProviderJson);
            Assert.Equal(inner is null ? "resolve-resources" : "get-profile", methodId);
            LastRequest = request.Clone();
            Interlocked.Increment(ref Calls);
            if (Failure is { } failure) throw new SunderRpcException(new(failure, "test.skills-resource", "Test resource failure."));
            if (Block)
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            var result = inner is not null ? await inner.InvokeUnaryAsync(context, serviceId, methodId, request, cancellationToken)
                : JsonSerializer.SerializeToElement(request.GetProperty("resources").Deserialize<AgentExecutionResourceDescriptor[]>(MemoryProviderJson)!
                    .Select(value => new AgentResolvedExecutionResource(value.ResourceId, value.ResourceKind, value.SourceId, value.DisplayName,
                        value.HostPath, "/executor/" + value.ResourceId, value.AccessMode, value.Metadata)).ToArray(), MemoryProviderJson);
            AfterResponse?.Invoke();
            return result;
        }
        public IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(SunderRpcInvocationContext context, string serviceId, string methodId, JsonElement request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
