using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Provider.Anthropic.Rpc;

[SunderService(ContractId = "sunder.package.agent.provider.anthropic.credentials", ServiceId = "credential-settings", IncludeSchemaDialect = true)]
[SunderServiceProvider(ProviderId = "anthropic.credentials")]
[SunderServiceError("provider.credential.required", Description = "A non-empty credential is required.")]
internal sealed partial class CredentialSettingsService(ProviderCredentialAccessor credentials)
{
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal CredentialSettingsService(ProviderSettings settings) : this(settings.Credentials) { }

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public async ValueTask<bool> GetStatusAsync(CancellationToken cancellationToken = default)
        => await credentials.HasCredentialAsync(cancellationToken).ConfigureAwait(false);

    [SunderMethod(Errors = ["provider.credential.required"])]
    public async ValueTask<bool> SetAsync(string credential, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(credential))
            throw new SunderRpcException(new(SunderRpcErrorKind.Domain, "provider.credential.required", "A non-empty credential is required."));
        await credentials.SetCredentialAsync(credential, cancellationToken).ConfigureAwait(false);
        return await credentials.HasCredentialAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> ClearAsync(CancellationToken cancellationToken = default)
    {
        await credentials.DeleteCredentialAsync(cancellationToken).ConfigureAwait(false);
        return await credentials.HasCredentialAsync(cancellationToken).ConfigureAwait(false);
    }
}
