using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static ISunderRpcServiceHandler AddGeneratedPeer(RegressionTestRpcState state, string packageId, string contractId,
        object implementation, string providerId, Func<ISunderRpcServiceHandler, ISunderRpcServiceHandler>? decorate = null)
    {
        var providers = new RegressionTestSourceProviders();
        var rpc = new RegressionTestRpcClient(state, packageId);
        var catalog = AgentRpcCatalog.CreateDormant(rpc);
        IDisposable? lifetime = null;
        try
        {
            var source = providers.Create(contractId, implementation, catalog, rpc);
            lifetime = source.Lifetime;
            var handler = decorate?.Invoke(source.Registration.Handler) ?? source.Registration.Handler;
            var registration = new SunderWorkerProviderRegistration(providerId, source.Registration.ContractId,
                source.Registration.ContractVersion, source.Registration.DescriptorSha256, source.Registration.AbiSha256, handler);
            state.AddGenerated(packageId, decorate is null ? implementation : handler, registration,
                new GeneratedPeerLifetime(catalog, providers, lifetime), providerId);
            return handler;
        }
        catch { catalog.Dispose(); lifetime?.Dispose(); providers.Dispose(); throw; }
    }

    private sealed class GeneratedPeerLifetime(AgentRpcCatalog catalog, RegressionTestSourceProviders providers, IDisposable? lifetime) : IDisposable
    {
        public void Dispose()
        {
            try { catalog.Dispose(); }
            finally
            {
                try { lifetime?.Dispose(); }
                finally { providers.Dispose(); }
            }
        }
    }
}
