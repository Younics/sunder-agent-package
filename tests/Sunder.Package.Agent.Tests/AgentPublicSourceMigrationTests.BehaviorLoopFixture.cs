using System.Reflection;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    private static async Task<BehaviorFixture> CreateBehaviorFixture(Assembly assembly, IReadOnlyDictionary<string, SunderRpcContractDescriptor> descriptors,
        IAgentBehaviorLoop loop, string loopPackage = "test.behavior.extension")
    {
        var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var owner = new BehaviorCallRecorder(new RegressionTestRpcClient(state, "sunder.package.agent"));
        var ownerCatalog = new AgentRpcCatalog(owner);
        var providerCatalog = new AgentRpcCatalog(new RegressionTestRpcClient(state, loopPackage));
        var registry = new AgentRunControlRegistry();
        var controlType = assembly.GetType("Sunder.Package.Agent.Rpc.Public.RunControlService", true)!;
        var control = Activator.CreateInstance(controlType, registry)!;
        var controlRegistration = Registration(controlType, control);
        state.Add("sunder.package.agent", descriptors[AgentRpcContractIds.RunControl], control, controlRegistration.Handler,
            EmptyDisposable.Instance, controlRegistration.ProviderId, "2.0.0");
        var loopType = assembly.GetType("Sunder.Package.Agent.Rpc.Public.BehaviorLoopService", true)!;
        var implementation = Activator.CreateInstance(loopType, loop, providerCatalog)!;
        var registration = Registration(loopType, implementation);
        var descriptor = descriptors[AgentRpcContractIds.BehaviorLoop];
        Assert.Equal("agent.behavior.loop", registration.ProviderId);
        Assert.Equal(descriptor.Sha256, registration.DescriptorSha256);
        Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
        state.Add(loopPackage, descriptor, implementation, registration.Handler, EmptyDisposable.Instance, registration.ProviderId, "2.0.0");
        var providers = (await owner.DiscoverAsync(descriptor.ContractId)).Providers;
        var source = providers.Single(value => value.ProviderId == registration.ProviderId);
        var scope = await owner.CreateCallScopeAsync(new(DateTimeOffset.UtcNow.AddMinutes(35)));
        var client = Activator.CreateInstance(assembly.GetType("Sunder.Generated.Rpc.BehaviorLoop.BehaviorLoopClient", true)!, scope, source.Endpoint)!;
        var bridge = (IAgentBehaviorLoop)Activator.CreateInstance(assembly.GetType("Sunder.Package.Agent.Runtime.Rpc.GeneratedBehaviorLoopClient", true)!,
            owner, source, registry, ownerCatalog)!;
        return new(state, owner, ownerCatalog, providerCatalog, registry, scope, client, implementation, control, source, loopPackage, bridge);

        static SunderWorkerProviderRegistration Registration(Type type, object value)
            => Assert.IsType<SunderWorkerProviderRegistration>(type.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(value, null));
    }

    private sealed class BehaviorFixture(RegressionTestRpcState state, BehaviorCallRecorder owner, AgentRpcCatalog ownerCatalog,
        AgentRpcCatalog providerCatalog, AgentRunControlRegistry registry, ISunderRpcCallScope scope, object client,
        object implementation, object control, SunderRpcProviderSnapshot source, string loopPackage, IAgentBehaviorLoop bridge) : IAsyncDisposable
    {
        public RegressionTestRpcState State { get; } = state;
        public BehaviorCallRecorder Owner { get; } = owner;
        public AgentRpcCatalog OwnerCatalog { get; } = ownerCatalog;
        public AgentRpcCatalog ProviderCatalog { get; } = providerCatalog;
        public AgentRunControlRegistry Registry { get; } = registry;
        public object Client { get; } = client;
        public object Implementation { get; } = implementation;
        public object Control { get; } = control;
        public SunderRpcProviderSnapshot Source { get; } = source;
        public string LoopPackage { get; } = loopPackage;
        public AgentRpcProviderHandle ControlHandle => AgentRpcProviderHandle.From(Assert.Single(State.Discover(AgentRpcContractIds.RunControl).Providers,
            provider => provider.PackageId == "sunder.package.agent" && provider.ProviderId == "agent.run.control"));
        public IAgentBehaviorLoop Bridge { get; } = bridge;
        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await ((IAsyncDisposable)Bridge).DisposeAsync();
            ProviderCatalog.Dispose(); OwnerCatalog.Dispose(); State.Dispose();
            Assert.All(Owner.Scopes, item => Assert.True(item.Disposed));
        }
    }

    private sealed class BehaviorCallRecorder(ISunderRpcClient inner) : ISunderRpcClient
    {
        public BehaviorLoopRunRequest? LastRun { get; private set; }
        public SunderRpcCallOptions? LastRunOptions { get; private set; }
        public List<Scope> Scopes { get; } = [];
        public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
            SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (serviceId == "behavior-loop" && methodId == "run")
            {
                LastRun = request.Deserialize<BehaviorLoopRunRequest>(ExecutionJson)!;
                LastRunOptions = options;
            }
            return inner.InvokeAsync(endpoint, serviceId, methodId, request, options, cancellationToken);
        }
        public async ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        {
            var scope = new Scope(this, await inner.CreateCallScopeAsync(options, cancellationToken));
            Scopes.Add(scope); return scope;
        }
        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
            => inner.GetProviderAsync(endpoint, cancellationToken);
        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
            => inner.DiscoverAsync(contractId, cancellationToken);
        public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default)
            => inner.WatchAsync(afterRevision, afterSequence, cancellationToken);
        public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default)
            => inner.TryReportInvariantViolationAsync(endpoint, exception, cancellationToken);
        public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
            SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
            => inner.SubscribeAsync(endpoint, serviceId, methodId, request, options, cancellationToken);

        public sealed class Scope(BehaviorCallRecorder owner, ISunderRpcCallScope innerScope) : ISunderRpcCallScope
        {
            public bool Disposed { get; private set; }
            public DateTimeOffset DeadlineUtc => innerScope.DeadlineUtc;
            public async ValueTask DisposeAsync() { await innerScope.DisposeAsync(); Disposed = true; }
            public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
                => owner.CreateCallScopeAsync(options, cancellationToken);
            public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
                SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
            {
                if (serviceId == "behavior-loop" && methodId == "run")
                { owner.LastRun = request.Deserialize<BehaviorLoopRunRequest>(ExecutionJson)!; owner.LastRunOptions = options ?? new(DeadlineUtc); }
                return innerScope.InvokeAsync(endpoint, serviceId, methodId, request, options, cancellationToken);
            }
            public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
                => innerScope.GetProviderAsync(endpoint, cancellationToken);
            public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default) => innerScope.DiscoverAsync(contractId, cancellationToken);
            public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default)
                => innerScope.TryReportInvariantViolationAsync(endpoint, exception, cancellationToken);
            public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default)
                => innerScope.WatchAsync(afterRevision, afterSequence, cancellationToken);
            public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
                SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
                => innerScope.SubscribeAsync(endpoint, serviceId, methodId, request, options, cancellationToken);
            public ValueTask<SunderRpcContentReference> RegisterContentAsync(SunderRpcEndpointReference endpoint, Stream source, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default)
                => innerScope.RegisterContentAsync(endpoint, source, options, cancellationToken);
            public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(SunderRpcEndpointReference endpoint, string path, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default)
                => innerScope.RegisterContentFileAsync(endpoint, path, options, cancellationToken);
            public ValueTask<Stream> OpenContentAsync(SunderRpcContentReference reference, CancellationToken cancellationToken = default) => innerScope.OpenContentAsync(reference, cancellationToken);
        }
    }

    private sealed class BehaviorProbe : IAgentBehaviorLoop
    {
        public AgentBehaviorLoopDescriptor Descriptor { get; set; } = new("loop.one", "Loop 漢字", "Description", "source.one", ["tools", "orchestration"], "{opaque-settings");
        public Func<AgentBehaviorLoopContext, IAgentBehaviorLoopRuntime, CancellationToken, ValueTask<AgentBehaviorLoopResult>> Execute { get; set; }
            = (context, _, _) => ValueTask.FromResult(new AgentBehaviorLoopResult(context.RunningCheckpoint, AgentBehaviorLoopCompletionKind.Completed));
        public AgentBehaviorLoopContext? Context { get; private set; }
        public IAgentBehaviorLoopRuntime? Runtime { get; private set; }
        public int Calls { get; private set; }
        public ValueTask<AgentBehaviorLoopResult> RunAsync(AgentBehaviorLoopContext context, IAgentBehaviorLoopRuntime runtime, CancellationToken cancellationToken = default)
        {
            Calls++; Context = context; Runtime = runtime;
            return Execute(context, runtime, cancellationToken);
        }
    }
}
