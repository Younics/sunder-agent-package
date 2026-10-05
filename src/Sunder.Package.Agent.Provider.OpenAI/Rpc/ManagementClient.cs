using Sunder.Generated.Rpc;
using Sunder.Sdk.Rpc;
using Wire = Sunder.Generated.Rpc.Management;

namespace Sunder.Package.Agent.Provider.OpenAI;

internal sealed class OpenAiOwnerManagementRpcClient(ISunderRpcClient rpc) : IOpenAiOwnerManagementClient
{
    public async ValueTask<OpenAiAuthOperationResult> GetAuthStatusAsync(CancellationToken token = default)
        => Map(await Invoke(static (client, cancellation) => client.GetAuthStatusAsync(new Wire.ManagementQuery(), cancellation), token).ConfigureAwait(false));
    public async ValueTask<OpenAiAuthOperationResult> DisconnectAuthAsync(CancellationToken token = default)
        => Map(await Invoke(static (client, cancellation) => client.DisconnectAuthAsync(new Wire.ManagementQuery(), cancellation), token).ConfigureAwait(false));
    public async ValueTask<bool> HasCredentialAsync(CancellationToken token = default)
        => (await Invoke(static (client, cancellation) => client.GetCredentialStatusAsync(new Wire.ManagementQuery(), cancellation), token).ConfigureAwait(false)).HasStoredCredential;
    public async ValueTask<bool> SetCredentialAsync(string credential, CancellationToken token = default)
        => (await Invoke((client, cancellation) => client.SetCredentialAsync(new Wire.CredentialRequest { Credential = credential }, cancellation), token).ConfigureAwait(false)).HasStoredCredential;
    public async ValueTask<bool> ClearCredentialAsync(CancellationToken token = default)
        => (await Invoke(static (client, cancellation) => client.ClearCredentialAsync(new Wire.ManagementQuery(), cancellation), token).ConfigureAwait(false)).HasStoredCredential;

    private async ValueTask<T> Invoke<T>(Func<Wire.ManagementClient, CancellationToken, ValueTask<T>> invoke, CancellationToken token)
    {
        var api = new PackageApi(rpc).Management;
        try
        {
            var provider = await api.ResolveSelectedPackageAsync(token).ConfigureAwait(false);
            await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: token).ConfigureAwait(false);
            return await invoke(api.CreateClient(scope, provider).SManagement, token).ConfigureAwait(false);
        }
        catch (SunderRpcException error) when (error.Error.Kind == SunderRpcErrorKind.NotFound && error.Error.Code == "rpc.provider-not-found")
        { throw new InvalidOperationException("The OpenAI Runtime management provider is unavailable.", error); }
    }

    private static OpenAiAuthOperationResult Map(Wire.AuthStatus status) => new(status.IsConnected, status.HasCachedSession,
        status.ExpiresAtUtc is { } expiry ? expiry.TryGetDateTimeOffset(out var value) ? value.ToUniversalTime()
            : throw new InvalidDataException("The OpenAI auth expiry exceeds .NET precision.") : null, status.ErrorMessage);
}
