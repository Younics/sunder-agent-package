using Sunder.Package.Agent.Provider.OpenAI;

namespace Sunder.Package.Agent.Provider.Shared;

internal sealed record ProviderCredentialSnapshot(bool HasStoredCredential);

internal interface IProviderCredentialOwnerClient
{
    ValueTask<ProviderCredentialSnapshot> GetStatusAsync(
        CancellationToken cancellationToken = default);

    ValueTask<ProviderCredentialSnapshot> SetAsync(
        string credential,
        CancellationToken cancellationToken = default);

    ValueTask<ProviderCredentialSnapshot> ClearAsync(
        CancellationToken cancellationToken = default);
}

internal sealed class ProviderCredentialOwnerClient(
    IOpenAiOwnerManagementClient management) : IProviderCredentialOwnerClient
{
    public async ValueTask<ProviderCredentialSnapshot> GetStatusAsync(
        CancellationToken cancellationToken = default)
        => new(await management.HasCredentialAsync(cancellationToken));

    public async ValueTask<ProviderCredentialSnapshot> SetAsync(
        string credential,
        CancellationToken cancellationToken = default)
        => new(await management.SetCredentialAsync(credential, cancellationToken));

    public async ValueTask<ProviderCredentialSnapshot> ClearAsync(
        CancellationToken cancellationToken = default)
        => new(await management.ClearCredentialAsync(cancellationToken));
}
