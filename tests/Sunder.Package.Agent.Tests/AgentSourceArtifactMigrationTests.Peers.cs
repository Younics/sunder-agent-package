using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static ISunderRpcServiceHandler AddGeneratedPeer(RegressionTestRpcState state, string packageId, string contractId,
        object implementation, string providerId, Func<ISunderRpcServiceHandler, ISunderRpcServiceHandler>? decorate = null)
    {
        var rpc = new RegressionTestRpcClient(state, packageId);
        var sourceModel = new GeneratedServiceTestScope(rpc);
        try
        {
            using var providers = new RegressionTestSourceProviders();
            var source = providers.Create(contractId, implementation, sourceModel);
            var handler = decorate?.Invoke(source.Handler) ?? source.Handler;
            var registration = new SunderWorkerProviderRegistration(providerId, source.ContractId,
                source.ContractVersion, source.DescriptorSha256, source.AbiSha256, handler);
            state.AddGenerated(packageId, decorate is null ? implementation : handler, registration,
                new GeneratedPeerLifetime(sourceModel), providerId);
            return handler;
        }
        catch { sourceModel.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
    }

    private sealed class GeneratedPeerLifetime(GeneratedServiceTestScope sourceModel) : IDisposable
    {
        public void Dispose()
        {
            sourceModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
