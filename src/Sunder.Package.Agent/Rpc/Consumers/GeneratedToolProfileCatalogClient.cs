using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Runtime_Catalog;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal sealed class GeneratedToolProfileCatalogClient(ISunderRpcClient rpc, SunderRpcProviderSnapshot provider)
    : IAgentToolProfileCatalog, IDisposable, IAsyncDisposable
{
    private readonly GeneratedProviderCallLifetime _calls = new(rpc, provider, "agent.profile.catalog-stale");

    public AgentProfileRecord? GetProfile(string profileId)
        => _calls.Invoke(async (scope, token) =>
        {
            var value = await new PackageApi(scope).RuntimeCatalog.CreateClient(scope, new Api.Provider(provider))
                .SRuntime_2dcatalog.GetProfileAsync(new() { Value = profileId }, token).ConfigureAwait(false);
            return value is null ? null : AgentConsumerValues.Convert<AgentProfileRecord>(value);
        }, default).AsTask().GetAwaiter().GetResult();

    public void Dispose() => _calls.Dispose();
    public ValueTask DisposeAsync() => _calls.DisposeAsync();
}
