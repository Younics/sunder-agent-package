using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Subagents.Runtime;
using Sunder.Sdk.Rpc;
using Api = Sunder.Generated.Rpc.ImportApis.CSunder_Agent_Chat_Provider;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Chat_Provider;

namespace Sunder.Package.Agent.Subagents.Rpc;

internal sealed class ChatCatalogClient(ISunderRpcClient rpc, Api.Contract contract, Api.Provider provider, CancellationToken retirement) : ISubagentChatCatalog
{
    private readonly CatalogClientInvocation<Wire.ChatProviderClient> _calls = new(rpc, scope => contract.CreateClient(scope, provider).SChat_2dprovider,
        async token => await contract.GetAsync(provider, token).ConfigureAwait(false) is { State: SunderRpcProviderState.Active } current && current.SessionGeneration == provider.SessionGeneration, retirement);
    private AgentProviderDescriptor? _descriptor;
    public AgentProviderDescriptor Descriptor => _descriptor ??= _calls.Invoke(async (client, token) => SubagentProviderValues.Convert<AgentProviderDescriptor>(await client.DescribeAsync(new(), token).ConfigureAwait(false)));
    public ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        => _calls.InvokeAsync<IReadOnlyList<AgentModelDescriptor>>(async (client, token) => SubagentProviderValues.Convert<AgentModelDescriptor[]>(await client.ListModelsAsync(new(), token).ConfigureAwait(false)), cancellationToken);
    public ValueTask<AgentProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
        => _calls.InvokeAsync(async (client, token) => SubagentProviderValues.Convert<AgentProviderReadiness>(await client.GetReadinessAsync(new(), token).ConfigureAwait(false)), cancellationToken);
}
