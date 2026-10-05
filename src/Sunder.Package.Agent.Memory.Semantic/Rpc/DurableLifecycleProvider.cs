using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Contracts.Models;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Durable_Lifecycle_Observer;

namespace Sunder.Package.Agent.Memory.Semantic.Rpc;

[SunderRpcProvider(typeof(IDurableLifecycleService), ProviderId = "semantic.memory.lifecycle")]
public sealed partial class DurableLifecycleProvider(IAgentDurableLifecycleObserver observer) : IDurableLifecycleService
{
    public ValueTask<Wire.AgentRpcIdentity> DescribeAsync(SunderRpcInvocationContext context, Wire.Unit request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new Wire.AgentRpcIdentity { Id = observer.ObserverId, DisplayName = observer.DisplayName });
    }

    public async ValueTask<Wire.Unit> HandleAsync(SunderRpcInvocationContext context, Wire.AgentDurableLifecycleEventEnvelope request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await observer.HandleDurableLifecycleEventAsync(SemanticProviderValues.Convert<Domain.AgentDurableLifecycleEventEnvelope>(request), token).ConfigureAwait(false);
        return new();
    }
}
