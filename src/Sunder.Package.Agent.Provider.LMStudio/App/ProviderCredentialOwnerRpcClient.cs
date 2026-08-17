using Sunder.Package.Agent.Provider.LMStudio.Protocol;
using Sunder.Package.Agent.Provider.LMStudio.Protocol.Generated;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Provider.Shared
{
    internal sealed record ProviderCredentialSnapshot(bool HasStoredCredential);

    internal interface IProviderCredentialOwnerClient
    {
        ValueTask<ProviderCredentialSnapshot> GetStatusAsync(CancellationToken cancellationToken = default);

        ValueTask<ProviderCredentialSnapshot> SetAsync(
            string credential,
            CancellationToken cancellationToken = default);

        ValueTask<ProviderCredentialSnapshot> ClearAsync(CancellationToken cancellationToken = default);
    }
}

namespace Sunder.Package.Agent.Provider.LMStudio
{
    using Sunder.Package.Agent.Provider.Shared;

    internal sealed class ProviderCredentialOwnerRpcClient(ISunderRpcClient rpcClient)
        : IProviderCredentialOwnerClient
    {
        private const string PackageId = "sunder.package.agent.provider.lmstudio";
        private const string ProviderId = "lmstudio.credentials";

        public ValueTask<ProviderCredentialSnapshot> GetStatusAsync(
            CancellationToken cancellationToken = default)
            => InvokeAsync(
                static (client, token) => client.QueryAsync(new CredentialQuery(), token),
                cancellationToken);

        public ValueTask<ProviderCredentialSnapshot> SetAsync(
            string credential,
            CancellationToken cancellationToken = default)
            => InvokeAsync(
                (client, token) => client.CommandAsync(new CredentialCommand
                {
                    Kind = CredentialOperation.Set,
                    Credential = credential,
                }, token),
                cancellationToken);

        public ValueTask<ProviderCredentialSnapshot> ClearAsync(
            CancellationToken cancellationToken = default)
            => InvokeAsync(
                static (client, token) => client.CommandAsync(new CredentialCommand
                {
                    Kind = CredentialOperation.Clear,
                }, token),
                cancellationToken);

        private async ValueTask<ProviderCredentialSnapshot> InvokeAsync(
            Func<CredentialSettingsClient, CancellationToken, ValueTask<CredentialSnapshot>> invoke,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var scope = await rpcClient
                    .CreateCallScopeAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var catalog = await scope
                    .DiscoverAsync(LMStudioCredentialProtocol.Descriptor.ContractId, cancellationToken)
                    .ConfigureAwait(false);
                var provider = catalog.Providers.FirstOrDefault(candidate =>
                    candidate.PackageId == PackageId
                    && candidate.ProviderId == ProviderId
                    && candidate.State == SunderRpcProviderState.Active)
                    ?? throw new SunderRpcException(new SunderRpcError(
                        SunderRpcErrorKind.Unavailable,
                        "provider.credential.unavailable",
                        "The package credential service is unavailable."));
                var response = await invoke(
                        new CredentialSettingsClient(scope, provider.Endpoint),
                        cancellationToken)
                    .ConfigureAwait(false);
                return new ProviderCredentialSnapshot(response.HasStoredCredential);
            }
            catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Cancelled)
            {
                throw new OperationCanceledException(
                    exception.Message,
                    exception,
                    cancellationToken.IsCancellationRequested ? cancellationToken : CancellationToken.None);
            }
        }
    }
}
