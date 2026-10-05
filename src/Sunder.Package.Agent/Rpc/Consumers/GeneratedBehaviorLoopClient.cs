using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Behavior_Loop;
using Wire = Sunder.Generated.Rpc.BehaviorLoop;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal sealed class GeneratedBehaviorLoopClient(ISunderRpcClient rpc, SunderRpcProviderSnapshot provider,
    AgentRunControlRegistry registrations, AgentRpcCatalog catalog) : IAgentBehaviorLoop, IDisposable, IAsyncDisposable
{
    private readonly GeneratedProviderCallLifetime _calls = new(rpc, provider, "agent.behavior.provider-stale");
    private AgentBehaviorLoopDescriptor? _descriptor;

    public AgentBehaviorLoopDescriptor Descriptor
    {
        get
        {
            _calls.ThrowIfStopped();
            return _descriptor ??= _calls.Invoke(async (scope, token) => AgentConsumerValues.Convert<AgentBehaviorLoopDescriptor>(
                await Client(scope).DescribeAsync(new(), token).ConfigureAwait(false)), default).AsTask().GetAwaiter().GetResult();
        }
    }

    public ValueTask<AgentBehaviorLoopResult> RunAsync(AgentBehaviorLoopContext context, IAgentBehaviorLoopRuntime runtime,
        CancellationToken cancellationToken = default)
        => _calls.Invoke(async (scope, token) =>
        {
            var control = catalog.GetServiceReferences(AgentBehaviorBindings.RunControls).FirstOrDefault(reference =>
                reference.PackageId == "sunder.package.agent" && reference.Provider.ProviderId == "agent.run.control")
                ?? throw new SunderRpcException(new(SunderRpcErrorKind.Unavailable, "agent.run-control.unavailable", "The Agent run-control provider is unavailable."));
            if (!control.TryAcquire(out var lease))
                throw new SunderRpcException(new(SunderRpcErrorKind.StaleEndpoint, "agent.run-control.stale", "The Agent run-control provider activation is stale."));
            using (lease)
            using (var registration = registrations.Open(runtime, provider.PackageId, out var invocationId))
            using (var invocation = CancellationTokenSource.CreateLinkedTokenSource(token, lease.RetirementToken))
            {
                var result = await Client(scope).RunAsync(new Wire.BehaviorLoopRunRequest
                {
                    InvocationId = invocationId, RunControl = AgentConsumerValues.Convert<Wire.AgentRpcProviderHandle>(control.ToHandle()),
                    Context = AgentConsumerValues.Convert<Wire.AgentBehaviorLoopContext>(context),
                }, invocation.Token).ConfigureAwait(false);
                invocation.Token.ThrowIfCancellationRequested();
                return AgentConsumerValues.Convert<AgentBehaviorLoopResult>(result);
            }
        }, cancellationToken, GeneratedRunControlEndpoint.ExtendedOptions());

    private Wire.BehaviorLoopClient Client(ISunderRpcCallScope scope)
        => new PackageApi(scope).BehaviorLoop.CreateClient(scope, new Api.Provider(provider)).SBehavior_2dloop;
    public void Dispose() => _calls.Dispose();
    public ValueTask DisposeAsync() => _calls.DisposeAsync();
}
