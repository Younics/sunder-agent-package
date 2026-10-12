using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifySkillsConsumers(Type type, Assembly agentOwner)
    {
        Assert.True(agentOwner.GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Services.IRuntimeCatalogService", true)!.IsInterface);
        using var package = RegressionTestPackageScope.Create();
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var (_, composition, services) = CreateGeneratedWorker(type.Assembly, package.Context,
            new RegressionTestRpcClient(state, "sunder.package.agent.skills"));
        await using var compositionLifetime = composition;
        var store = services.GetRequiredService(type.Assembly.GetType("Sunder.Package.Agent.Skills.Services.SkillStore", true)!);
        var importer = services.GetRequiredService(type.Assembly.GetType("Sunder.Package.Agent.Skills.Services.SkillImportService", true)!);
        var source = Directory.CreateDirectory(Path.Combine(package.RootPath, "consumer-skill")).FullName;
        File.WriteAllText(Path.Combine(source, "SKILL.md"), "---\nname: Fixture skill\ndescription: Resource consumer fixture\n---\nFixture instructions.");
        var installed = (await AgentRoleBoundary.Await(AgentRoleBoundary.Call(importer, "ImportLocalFolderAsync", source, CancellationToken.None)!))!;
        var skillId = (string)installed.GetType().GetProperty("SkillId")!.GetValue(installed)!;
        var hostPath = (string)AgentRoleBoundary.Call(store, "GetSkillRootPath", installed)!;
        var now = DateTimeOffset.UtcNow;
        var profile = new AgentProfileRecord("profile.generated", "Generated", null, null, null, null, null, null, now, now,
            SelectableCapabilityAssignments: [new("skill", skillId, "sunder.package.agent.skills")]);
        var runtime = new MemoryConsumerRuntime(profile);
        await using var consumer = NewConsumer();
        Assert.Equal(0, consumer.Transport.DiscoveryCount);
        Assert.Equal(0, consumer.Transport.ActiveWatches);
        Assert.Null(await consumer.Profile(profile.ProfileId));
        var catalog = AddCatalog();
        Assert.Equal(profile.ProfileId, (await consumer.Profile(profile.ProfileId))!.ProfileId);
        Assert.Null(await consumer.Profile("missing"));
        var target = new SkillsConsumerHandler();
        AddTarget(target);
        var handle = await consumer.Target();
        var context = new AgentExecutionTargetContext(Guid.NewGuid(), profile.ProfileId,
            new("workspace.skills", "Skills", null, now, now),
            new("binding.skills", "workspace.skills", RegressionTestBindings.ExecutionTargets, "test-target", "execution", true, 0, now, now));
        Assert.Empty(await consumer.Resolve(context, null));
        var result = Assert.Single(await consumer.Resolve(context, handle));
        Assert.Equal("/executor/" + skillId, result.ExecutionPath);
        Assert.Equal(hostPath, result.HostPath);
        Assert.Equal(AgentExecutionResourceAccessMode.ReadOnly, result.AccessMode);
        Assert.Equal(skillId, result.Metadata!["skill_id"]);
        Assert.Equal(context.Binding.BindingId, target.LastRequest.GetProperty("context").GetProperty("binding").GetProperty("bindingId").GetString());
        Assert.Empty(target.LastRequest.GetProperty("context").GetProperty("approvedResourceCapabilities").EnumerateArray());
        var count = target.Calls;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var discoveries = consumer.Transport.DiscoveryCount;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer.Profile(profile.ProfileId, cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer.Resolve(context, handle, cancelled.Token));
            Assert.Equal(discoveries, consumer.Transport.DiscoveryCount);
            Assert.Equal(count, target.Calls);
        }
        Assert.Empty(await consumer.Resolve(context with { Binding = context.Binding with { ContributionId = "wrong-target" } }, handle));
        target.HasFacet = false;
        Assert.Empty(await consumer.Resolve(context, handle));
        target.HasFacet = true;
        Assert.Equal(count, target.Calls);
        Assert.Single(await consumer.Resolve(context with { Binding = context.Binding with { ContributionId = "TEST" } }, handle));

        // References are opaque SDK values. Inject each identity mismatch at the
        // metadata boundary rather than manufacturing a forged CLR reference.
        foreach (var mutate in new Func<SunderRpcProviderSnapshot, SunderRpcProviderSnapshot>[]
        {
            value => value with { SessionGeneration = value.SessionGeneration + 1 },
            value => value with { ActivationEpoch = value.ActivationEpoch + 1 },
            value => value with { ActivationId = Guid.NewGuid() },
            value => value with { PackageVersion = "2.0.1" },
            value => value with { DescriptorSha256 = new('0', 64) },
        })
        {
            await using var invalid = NewConsumer();
            var selected = await invalid.Target();
            invalid.Transport.LookupTransform = mutate;
            count = target.Calls;
            Assert.Empty(await invalid.Resolve(context, selected));
            Assert.Equal(count, target.Calls);
        }
        foreach (var failure in new[] { SunderRpcErrorKind.Unavailable, SunderRpcErrorKind.PermissionDenied })
        {
            await using var failed = NewConsumer();
            var selected = await failed.Target();
            failed.Transport.LookupFailure = failure;
            if (failure == SunderRpcErrorKind.Unavailable)
            {
                Assert.Empty(await failed.Resolve(context, selected));
                Assert.Null(await failed.Profile(profile.ProfileId));
            }
            else
            {
                Assert.Equal(failure, (await Assert.ThrowsAsync<SunderRpcException>(() => failed.Resolve(context, selected))).Error.Kind);
                Assert.Equal(failure, (await Assert.ThrowsAsync<SunderRpcException>(() => failed.Profile(profile.ProfileId))).Error.Kind);
            }
        }
        foreach (var failure in new[] { SunderRpcErrorKind.PermissionDenied, SunderRpcErrorKind.Unavailable })
        {
            target.Failure = failure;
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, (await Assert.ThrowsAsync<SunderRpcException>(() => consumer.Resolve(context, handle))).Error.Kind);
        }
        target.Failure = null;
        await using (var stale = NewConsumer())
        {
            var selected = await stale.Target();
            target.AfterResponse = () => stale.Transport.LookupTransform = value => value with { SessionGeneration = value.SessionGeneration + 1 };
            try { Assert.Empty(await stale.Resolve(context, selected)); }
            finally { target.AfterResponse = null; }
        }
        await using (var stale = NewConsumer())
        {
            catalog.AfterResponse = () => stale.Transport.LookupTransform = value => value with { SessionGeneration = value.SessionGeneration + 1 };
            try { Assert.Null(await stale.Profile(profile.ProfileId)); }
            finally { catalog.AfterResponse = null; }
        }
        target.Hold();
        using (var caller = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var pending = consumer.Resolve(context, handle, caller.Token);
            await target.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        target.Hold();
        var retiring = consumer.Resolve(context, handle);
        await target.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RetireAsync(target).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(await retiring.WaitAsync(TimeSpan.FromSeconds(5)));
        var replacement = new SkillsConsumerHandler();
        AddTarget(replacement);
        Assert.Empty(await consumer.Resolve(context, handle));
        Assert.Equal(0, replacement.Calls);
        handle = await consumer.Target();
        Assert.Single(await consumer.Resolve(context, handle));
        catalog.Hold();
        var retiringProfile = consumer.Profile(profile.ProfileId);
        await catalog.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RetireAsync(catalog).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await retiringProfile.WaitAsync(TimeSpan.FromSeconds(5)));
        AddCatalog();
        Assert.NotNull(await consumer.Profile(profile.ProfileId));
        replacement.Hold();
        var inFlight = consumer.Resolve(context, handle);
        await replacement.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = consumer.DisposeAsync().AsTask();
        Assert.Same(stopping, consumer.DisposeAsync().AsTask());
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(await inFlight.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, consumer.Transport.ActiveWatches);
        foreach (var scope in consumer.Transport.Scopes)
            await Assert.ThrowsAsync<ObjectDisposedException>(() => scope.GetProviderAsync(handle.Snapshot.Endpoint).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => consumer.Profile(profile.ProfileId));

        SkillsConsumerScope NewConsumer() => new(type, store, profile, state);
        void AddTarget(SkillsConsumerHandler value) => state.Add("example.skills.executor", CurrentPublicRpcDescriptors.Get(RegressionTestBindings.ExecutionTargets),
            value, value, Noop.Instance, "skills.execution.target", "2.0.0");
        SkillsConsumerHandler AddCatalog() => (SkillsConsumerHandler)AddGeneratedPeer(state, "sunder.package.agent", RegressionTestBindings.RuntimeCatalog,
            runtime, "agent.runtime.catalog", handler => new SkillsConsumerHandler(handler));
    }

    private sealed class SkillsConsumerScope : IAsyncDisposable
    {
        private readonly Type _type;
        private readonly AgentProfileRecord _profile;
        private readonly GeneratedServiceTestScope _receiver;
        private readonly RoleLocalValue _feature;
        internal MemoryConsumerRpc Transport { get; }
        internal SkillsConsumerScope(Type type, object store, AgentProfileRecord profile, RegressionTestRpcState state)
        {
            _type = type; _profile = profile;
            Transport = new(new RegressionTestRpcClient(state, "sunder.package.agent.skills"));
            _receiver = new(Transport);
            var catalog = _receiver.Providers(type.Assembly.GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Services.IRuntimeCatalogService", true)!);
            _feature = new(Activator.CreateInstance(type, store, catalog)!);
        }
        internal Task<AgentProfileRecord?> Profile(string? id, CancellationToken token = default)
            => _feature.InvokeAsync<AgentProfileRecord?>("GetProfileAsync", id, token).AsTask();
        internal async Task<ProjectedArtifactProvider> Target()
            => Assert.Single(await ProjectedProviders(_receiver, _type.Assembly, "IExecutionTargetService"));
        internal Task<AgentResolvedExecutionResource[]> Resolve(AgentExecutionTargetContext context, ProjectedArtifactProvider? reference,
            CancellationToken token = default)
            => _feature.InvokeAsync<AgentResolvedExecutionResource[]>("ResolveResourcesAsync", _profile, context.SessionId,
                context.Workspace, context.Binding, reference?.Reference, token).AsTask();
        public ValueTask DisposeAsync() => _receiver.DisposeAsync();
    }

    private sealed class SkillsConsumerHandler(ISunderRpcServiceHandler? inner = null) : ISunderRpcServiceHandler
    {
        private static readonly JsonSerializerOptions Json = new(MemoryProviderJson)
        { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never };
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
            { Facets = HasFacet ? ["resource-resolution"] : [] }, Json);
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
                : JsonSerializer.SerializeToElement(request.GetProperty("resources").Deserialize<AgentExecutionResourceDescriptor[]>(Json)!
                    .Select(value => new AgentResolvedExecutionResource(value.ResourceId, value.ResourceKind, value.SourceId, value.DisplayName,
                        value.HostPath, "/executor/" + value.ResourceId, value.AccessMode, value.Metadata)).ToArray(), Json);
            AfterResponse?.Invoke();
            return result;
        }
        public IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(SunderRpcInvocationContext context, string serviceId, string methodId, JsonElement request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
