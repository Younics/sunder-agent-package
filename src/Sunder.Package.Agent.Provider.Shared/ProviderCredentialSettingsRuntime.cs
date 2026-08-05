using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Provider.Shared;

internal static class ProviderCredentialRuntimeOperations
{
    internal static readonly PackageRuntimeOperation<ProviderCredentialQuery, ProviderCredentialSnapshot> Query =
        new("provider.credential.query.v1");

    internal static readonly PackageRuntimeOperation<ProviderCredentialCommand, ProviderCredentialSnapshot> Command =
        new("provider.credential.command.v1");
}

internal sealed record ProviderCredentialQuery;

internal sealed record ProviderCredentialSnapshot(bool HasStoredCredential);

internal enum ProviderCredentialCommandKind
{
    Set,
    Clear,
}

internal sealed record ProviderCredentialCommand(
    ProviderCredentialCommandKind Kind,
    string? Credential = null);

internal interface IProviderCredentialSettingsGateway
{
    ValueTask<ProviderCredentialSnapshot> GetStatusAsync(CancellationToken cancellationToken = default);

    ValueTask<ProviderCredentialSnapshot> SetAsync(
        string credential,
        CancellationToken cancellationToken = default);

    ValueTask<ProviderCredentialSnapshot> ClearAsync(CancellationToken cancellationToken = default);
}

internal sealed class ProviderCredentialAppRuntimeGateway(IPackageRuntimeClient runtimeClient)
    : IProviderCredentialSettingsGateway
{
    public ValueTask<ProviderCredentialSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
        => runtimeClient.InvokeAsync(
            ProviderCredentialRuntimeOperations.Query,
            new ProviderCredentialQuery(),
            cancellationToken);

    public ValueTask<ProviderCredentialSnapshot> SetAsync(
        string credential,
        CancellationToken cancellationToken = default)
        => runtimeClient.InvokeAsync(
            ProviderCredentialRuntimeOperations.Command,
            new ProviderCredentialCommand(ProviderCredentialCommandKind.Set, credential),
            cancellationToken);

    public ValueTask<ProviderCredentialSnapshot> ClearAsync(CancellationToken cancellationToken = default)
        => runtimeClient.InvokeAsync(
            ProviderCredentialRuntimeOperations.Command,
            new ProviderCredentialCommand(ProviderCredentialCommandKind.Clear),
            cancellationToken);
}

internal sealed class ProviderCredentialRuntimeHandler(ProviderCredentialAccessor credentials)
    : IPackageRuntimeOperationHandler<ProviderCredentialQuery, ProviderCredentialSnapshot>,
      IPackageRuntimeOperationHandler<ProviderCredentialCommand, ProviderCredentialSnapshot>
{
    public async ValueTask<ProviderCredentialSnapshot> HandleAsync(
        ProviderCredentialQuery request,
        CancellationToken cancellationToken = default)
        => new(await credentials.HasCredentialAsync(cancellationToken).ConfigureAwait(false));

    public async ValueTask<ProviderCredentialSnapshot> HandleAsync(
        ProviderCredentialCommand request,
        CancellationToken cancellationToken = default)
    {
        switch (request.Kind)
        {
            case ProviderCredentialCommandKind.Set:
                if (string.IsNullOrWhiteSpace(request.Credential))
                {
                    throw new InvalidOperationException("A non-empty credential is required for a set command.");
                }

                await credentials.SetCredentialAsync(request.Credential, cancellationToken).ConfigureAwait(false);
                break;
            case ProviderCredentialCommandKind.Clear:
                if (request.Credential is not null)
                {
                    throw new InvalidOperationException("A clear command must not contain a credential.");
                }

                await credentials.DeleteCredentialAsync(cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unknown credential command.");
        }

        return new ProviderCredentialSnapshot(
            await credentials.HasCredentialAsync(cancellationToken).ConfigureAwait(false));
    }
}
