using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Protocol;
using Cleaners = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Session_Cleaner;
using Lifecycle = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Durable_Lifecycle_Observer;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal static class AgentCleanupBindings
{
    internal static AgentRpcService<IAgentSessionDataCleaner> SessionCleaners { get; } =
        AgentRpcService<IAgentSessionDataCleaner>.WithProviderIdentity(Cleaners.Contract.ContractId,
            static (rpc, provider) => new GeneratedSessionCleanerClient(rpc, provider));

    internal static AgentRpcService<IAgentDurableLifecycleObserver> DurableLifecycleObservers { get; } =
        AgentRpcService<IAgentDurableLifecycleObserver>.WithProviderIdentity(Lifecycle.Contract.ContractId,
            static (rpc, provider) => new GeneratedDurableLifecycleClient(rpc, provider));
}
