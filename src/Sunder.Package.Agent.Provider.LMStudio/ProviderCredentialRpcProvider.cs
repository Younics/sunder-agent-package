using Sunder.Package.Agent.Provider.LMStudio.Protocol.Generated;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Provider.LMStudio;

internal sealed class ProviderCredentialRpcProvider(ProviderCredentialAccessor credentials)
    : ICredentialSettingsProvider
{
    public async ValueTask<CredentialSnapshot> QueryAsync(
        SunderRpcInvocationContext context,
        CredentialQuery request,
        CancellationToken cancellationToken = default)
        => new()
        {
            HasStoredCredential = await credentials
                .HasCredentialAsync(cancellationToken)
                .ConfigureAwait(false),
        };

    public async ValueTask<CredentialSnapshot> CommandAsync(
        SunderRpcInvocationContext context,
        CredentialCommand request,
        CancellationToken cancellationToken = default)
    {
        switch (request.Kind)
        {
            case 0:
                if (string.IsNullOrWhiteSpace(request.Credential))
                {
                    throw Domain(
                        "provider.credential.required",
                        "A non-empty credential is required for a set command.");
                }
                await credentials.SetCredentialAsync(request.Credential, cancellationToken).ConfigureAwait(false);
                break;
            case 1:
                if (request.Credential is not null)
                {
                    throw Domain(
                        "provider.credential.unexpected",
                        "A clear command must not contain a credential.");
                }
                await credentials.DeleteCredentialAsync(cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw Domain("provider.credential.command-invalid", "Unknown credential command.");
        }

        return new CredentialSnapshot
        {
            HasStoredCredential = await credentials
                .HasCredentialAsync(cancellationToken)
                .ConfigureAwait(false),
        };
    }

    private static SunderRpcException Domain(string code, string message)
        => new(new SunderRpcError(SunderRpcErrorKind.Domain, code, message));
}
