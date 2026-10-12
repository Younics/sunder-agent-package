using System.Reflection;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Sunder.Sdk.Worker;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    private static async Task<BehaviorFixture> CreateBehaviorFixture(Assembly assembly, IReadOnlyDictionary<string, SunderRpcContractDescriptor> descriptors,
        IAgentBehaviorLoop loop, string loopPackage = "test.behavior.extension")
    {
        var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var owner = new BehaviorCallRecorder(new RegressionTestRpcClient(state, "sunder.package.agent"));
        var ownerReceiver = new GeneratedServiceTestScope(owner);
        var providerReceiver = new GeneratedServiceTestScope(new RegressionTestRpcClient(state, loopPackage));
        var activeRuns = new AgentActiveRunRegistry();
        var registry = new AgentRoleRunControls(assembly, activeRuns);
        var control = new AgentRunControlService(activeRuns);
        var controlRegistration = ownerReceiver.Register(control, descriptors[RegressionTestBindings.RunControls].ContractId);
        state.Add("sunder.package.agent", descriptors[RegressionTestBindings.RunControls], control, controlRegistration.Handler,
            EmptyDisposable.Instance, controlRegistration.ProviderId, "2.0.0");
        var loopType = assembly.GetType("Sunder.Package.Agent.Rpc.Public.BehaviorLoopService", true)!;
        Assert.True(loopType.IsInterface);
        var implementation = loop;
        using var providers = new RegressionTestSourceProviders();
        var registration = providers.Create(descriptors[RegressionTestBindings.BehaviorLoops].ContractId, implementation, providerReceiver);
        var descriptor = descriptors[RegressionTestBindings.BehaviorLoops];
        Assert.Equal(loop is BehaviorProbe ? "fixture.regression.behavior.loop" : "agent.behavior.loop", registration.ProviderId);
        Assert.Equal(descriptor.Sha256, registration.DescriptorSha256);
        Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
        state.Add(loopPackage, descriptor, implementation, registration.Handler, EmptyDisposable.Instance, registration.ProviderId, "2.0.0");
        var provider = Assert.Single((await ownerReceiver.Providers<AgentServices.IBehaviorLoopService>().ListAsync()).Providers,
            value => value.ProviderId == registration.ProviderId);
        var controlReference = Assert.Single((await ownerReceiver.Providers<AgentServices.IRunControlService>().ListAsync()).Providers,
            value => value.ProviderId == controlRegistration.ProviderId).Reference;
        var controlClient = Assert.Single((await providerReceiver.Providers<AgentServices.IRunControlService>().ListAsync()).Providers,
            value => value.ProviderId == controlRegistration.ProviderId).Service;
        var bridge = new GeneratedBehaviorLoopBridge(provider.Service, controlReference, registry, loopPackage);
        return new(state, owner, ownerReceiver, providerReceiver, registry, provider.Service, implementation, control, provider, loopPackage, bridge, controlReference, controlClient);
    }

    private sealed class BehaviorFixture(RegressionTestRpcState state, BehaviorCallRecorder owner, GeneratedServiceTestScope ownerReceiver,
        GeneratedServiceTestScope providerReceiver, AgentRoleRunControls registry, AgentServices.IBehaviorLoopService client,
        object implementation, object control, ServiceProvider<AgentServices.IBehaviorLoopService> source, string loopPackage,
        IAgentBehaviorLoop bridge, ServiceReference<AgentServices.IRunControlService> controlReference,
        AgentServices.IRunControlService controlClient) : IAsyncDisposable
    {
        public RegressionTestRpcState State { get; } = state;
        public BehaviorCallRecorder Owner { get; } = owner;
        public GeneratedServiceTestScope OwnerReceiver { get; } = ownerReceiver;
        public AgentRoleRunControls Registry { get; } = registry;
        public AgentServices.IBehaviorLoopService Client { get; } = client;
        public object Implementation { get; } = implementation;
        public object Control { get; } = control;
        public ServiceProvider<AgentServices.IBehaviorLoopService> Source { get; } = source;
        public string LoopPackage { get; } = loopPackage;
        public ServiceReference<AgentServices.IRunControlService> ControlReference { get; } = controlReference;
        public AgentServices.IRunControlService ControlClient { get; } = controlClient;
        public IAgentBehaviorLoop Bridge { get; } = bridge;
        public async ValueTask DisposeAsync()
        {
            await OwnerReceiver.DisposeAsync();
            await ((IAsyncDisposable)Bridge).DisposeAsync();
            await providerReceiver.DisposeAsync();
            State.Dispose();
            Assert.All(Owner.Scopes, item => Assert.True(item.Disposed));
        }
    }

    private sealed class GeneratedBehaviorLoopBridge(AgentServices.IBehaviorLoopService client,
        ServiceReference<AgentServices.IRunControlService> control, AgentRoleRunControls registry, string packageId) : IAgentBehaviorLoop, IAsyncDisposable
    {
        public AgentBehaviorLoopDescriptor Descriptor => client.DescribeAsync().AsTask().GetAwaiter().GetResult();

        public async ValueTask<AgentBehaviorLoopResult> RunAsync(AgentBehaviorLoopContext context, IAgentBehaviorLoopRuntime runtime,
            CancellationToken cancellationToken = default)
        {
            using var grant = registry.Open(runtime, packageId, out var invocationId);
            return await client.RunAsync(context, invocationId, control, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BehaviorCallRecorder(ISunderRpcClient inner) : ISunderRpcClient
    {
        private static readonly JsonSerializerOptions ObservationJson = new(JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) },
        };
        public BehaviorLoopRunRequest? LastRun { get; private set; }
        public SunderRpcCallOptions? LastRunOptions { get; private set; }
        public List<Scope> Scopes { get; } = [];
        public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
            SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (serviceId == "behavior-loop" && methodId == "run")
            {
                LastRun = request.Deserialize<BehaviorLoopRunRequest>(ObservationJson)!;
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
                { owner.LastRun = request.Deserialize<BehaviorLoopRunRequest>(ObservationJson)!; owner.LastRunOptions = options ?? new(DeadlineUtc); }
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
