using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Protocol;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Tool_Source;

namespace Sunder.Package.Agent.Runtime.Rpc;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class AgentToolBindings
{
    internal static AgentRpcService<IAgentToolSource> Service { get; } =
        AgentRpcService<IAgentToolSource>.WithProviderIdentity(Api.Contract.ContractId,
            static (rpc, provider) => new GeneratedToolSourceClient(rpc, provider));

    internal static ValueTask<(string SourceId, string DisplayName, string SourceKind)> DescribeAsync(IAgentToolSource source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return source is GeneratedToolSourceClient client ? client.DescribeAsync(token) : ValueTask.FromResult((source.SourceId, source.DisplayName, source.SourceKind));
    }
}
