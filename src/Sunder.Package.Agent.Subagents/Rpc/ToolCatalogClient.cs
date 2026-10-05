using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Subagents.Runtime;
using Sunder.Sdk.Rpc;
using Api = Sunder.Generated.Rpc.ImportApis.CSunder_Agent_Tool_Source;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Tool_Source;

namespace Sunder.Package.Agent.Subagents.Rpc;

internal sealed class ToolCatalogClient(ISunderRpcClient rpc, Api.Contract contract, Api.Provider provider, CancellationToken retirement) : ISubagentToolCatalog
{
    private readonly CatalogClientInvocation<Wire.ToolSourceClient> _calls = new(rpc, scope => contract.CreateClient(scope, provider).STool_2dsource,
        async token => await contract.GetAsync(provider, token).ConfigureAwait(false) is { State: SunderRpcProviderState.Active } current && current.SessionGeneration == provider.SessionGeneration, retirement);
    private Wire.ToolSourceDescription? _description;
    private Wire.ToolSourceDescription Description => _description ??= _calls.Invoke((client, token) => client.DescribeAsync(new(), token));
    public string SourceId => Description.SourceId;
    public string DisplayName => Description.DisplayName;
    public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(AgentToolSourceContext context, CancellationToken cancellationToken = default)
        => _calls.InvokeAsync<IReadOnlyList<AgentToolDescriptor>>(async (client, token) => SubagentProviderValues.Convert<AgentToolDescriptor[]>(await client.ListToolsAsync(new()
        {
            Context = SubagentProviderValues.Convert<Wire.AgentToolSourceContext>(context),
            ExecutionTargetConfigurationGeneration = context.ExecutionTargetConfigurationGeneration,
        }, token).ConfigureAwait(false)), cancellationToken);
}
