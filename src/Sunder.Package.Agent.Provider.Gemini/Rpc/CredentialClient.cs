using Sunder.Generated.Rpc;
using Sunder.Sdk.Rpc;
using Wire = Sunder.Generated.Rpc.CredentialSettings;

namespace Sunder.Package.Agent.Provider.Shared
{
    internal sealed record ProviderCredentialSnapshot(bool HasStoredCredential);
    internal interface IProviderCredentialOwnerClient
    {
        ValueTask<ProviderCredentialSnapshot> GetStatusAsync(CancellationToken cancellationToken = default);
        ValueTask<ProviderCredentialSnapshot> SetAsync(string credential, CancellationToken cancellationToken = default);
        ValueTask<ProviderCredentialSnapshot> ClearAsync(CancellationToken cancellationToken = default);
    }
}

namespace Sunder.Package.Agent.Provider.Gemini
{
    using Sunder.Package.Agent.Provider.Shared;

    internal sealed class ProviderCredentialOwnerRpcClient(ISunderRpcClient rpcClient) : IProviderCredentialOwnerClient
    {
        public ValueTask<ProviderCredentialSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
            => InvokeAsync(static (client, token) => client.QueryAsync(new Wire.CredentialQuery(), token), cancellationToken);
        public ValueTask<ProviderCredentialSnapshot> SetAsync(string credential, CancellationToken cancellationToken = default)
            => InvokeAsync((client, token) => client.CommandAsync(new Wire.CredentialCommand
            { Kind = Wire.CredentialOperation.Set, Credential = credential }, token), cancellationToken);
        public ValueTask<ProviderCredentialSnapshot> ClearAsync(CancellationToken cancellationToken = default)
            => InvokeAsync(static (client, token) => client.CommandAsync(new Wire.CredentialCommand
            { Kind = Wire.CredentialOperation.Clear }, token), cancellationToken);

        private async ValueTask<ProviderCredentialSnapshot> InvokeAsync(
            Func<Wire.CredentialSettingsClient, CancellationToken, ValueTask<Wire.CredentialSnapshot>> invoke, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var api = new PackageApi(rpcClient).CredentialSettings;
                var provider = await api.ResolveSelectedPackageAsync(token).ConfigureAwait(false);
                await using var scope = await rpcClient.CreateCallScopeAsync(cancellationToken: token).ConfigureAwait(false);
                var response = await invoke(api.CreateClient(scope, provider).SCredential_2dsettings, token).ConfigureAwait(false);
                return new(response.HasStoredCredential);
            }
            catch (SunderRpcException error) when (error.Error.Kind == SunderRpcErrorKind.NotFound && error.Error.Code == "rpc.provider-not-found")
            {
                throw new SunderRpcException(new(SunderRpcErrorKind.Unavailable, "provider.credential.unavailable", "The package credential service is unavailable."));
            }
            catch (SunderRpcException error) when (error.Error.Kind == SunderRpcErrorKind.Cancelled)
            {
                throw new OperationCanceledException(error.Message, error, token.IsCancellationRequested ? token : CancellationToken.None);
            }
        }
    }
}
