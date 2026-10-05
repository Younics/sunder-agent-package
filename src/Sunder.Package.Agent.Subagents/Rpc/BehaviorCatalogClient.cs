using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Subagents.Runtime;
using Sunder.Sdk.Rpc;
using Api = Sunder.Generated.Rpc.ImportApis.CSunder_Agent_Behavior_Loop;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Behavior_Loop;

namespace Sunder.Package.Agent.Subagents.Rpc;

internal sealed class BehaviorCatalogClient(ISunderRpcClient rpc, Api.Contract contract, Api.Provider provider, CancellationToken retirement) : ISubagentBehaviorCatalog
{
    private readonly CatalogClientInvocation<Wire.BehaviorLoopClient> _calls = new(rpc, scope => contract.CreateClient(scope, provider).SBehavior_2dloop,
        async token => await contract.GetAsync(provider, token).ConfigureAwait(false) is { State: SunderRpcProviderState.Active } current && current.SessionGeneration == provider.SessionGeneration, retirement);
    private AgentBehaviorLoopDescriptor? _descriptor;
    public AgentBehaviorLoopDescriptor Descriptor => _descriptor ??= _calls.Invoke(async (client, token) => SubagentProviderValues.Convert<AgentBehaviorLoopDescriptor>(await client.DescribeAsync(new(), token).ConfigureAwait(false)));
}
