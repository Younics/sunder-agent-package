using System.Globalization;
using Sunder.Package.Agent.Provider.OpenAI.Protocol.Generated;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Provider.OpenAI;

internal sealed class OpenAiOwnerManagementRpcClient(ISunderRpcClient rpc) : IOpenAiOwnerManagementClient
{
    public async ValueTask<OpenAiAuthOperationResult> GetAuthStatusAsync(
        CancellationToken cancellationToken = default)
        => ToResult(await InvokeAsync(
            static (client, token) => client.GetAuthStatusAsync(new EmptyRequest(), token),
            cancellationToken));

    public async ValueTask<OpenAiAuthOperationResult> DisconnectAuthAsync(
        CancellationToken cancellationToken = default)
        => ToResult(await InvokeAsync(
            static (client, token) => client.DisconnectAuthAsync(new EmptyRequest(), token),
            cancellationToken));

    public async ValueTask<bool> HasCredentialAsync(CancellationToken cancellationToken = default)
        => (await InvokeAsync(
            static (client, token) => client.GetCredentialStatusAsync(new EmptyRequest(), token),
            cancellationToken)).HasStoredCredential;

    public async ValueTask<bool> SetCredentialAsync(
        string credential,
        CancellationToken cancellationToken = default)
        => (await InvokeAsync(
            (client, token) => client.SetCredentialAsync(
                new CredentialRequest { Credential = credential },
                token),
            cancellationToken)).HasStoredCredential;

    public async ValueTask<bool> ClearCredentialAsync(CancellationToken cancellationToken = default)
        => (await InvokeAsync(
            static (client, token) => client.ClearCredentialAsync(new EmptyRequest(), token),
            cancellationToken)).HasStoredCredential;

    private async ValueTask<T> InvokeAsync<T>(
        Func<ManagementClient, CancellationToken, ValueTask<T>> invoke,
        CancellationToken cancellationToken)
    {
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: cancellationToken);
        var client = new ManagementClient(scope, await ResolveProviderAsync(scope, cancellationToken));
        return await invoke(client, cancellationToken);
    }

    private static async ValueTask<SunderRpcEndpointReference> ResolveProviderAsync(
        ISunderRpcClient client,
        CancellationToken cancellationToken)
    {
        var descriptor = OpenAiManagementProtocol.Descriptor;
        var catalog = await client.DiscoverAsync(descriptor.ContractId, cancellationToken);
        var provider = catalog.Providers.SingleOrDefault(candidate =>
            candidate.ProviderId == OpenAiManagementProtocol.ProviderId
            && candidate.State == SunderRpcProviderState.Active
            && candidate.ContractVersion == descriptor.Version
            && candidate.DescriptorSha256 == descriptor.Sha256
            && candidate.AbiSha256 == descriptor.AbiSha256);
        return provider?.Endpoint
            ?? throw new InvalidOperationException("The OpenAI Runtime management provider is unavailable.");
    }

    private static OpenAiAuthOperationResult ToResult(AuthStatus status)
        => new(
            status.IsConnected,
            status.HasCachedSession,
            string.IsNullOrWhiteSpace(status.ExpiresAtUtc)
                ? null
                : DateTimeOffset.Parse(
                    status.ExpiresAtUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            status.ErrorMessage);
}

internal sealed class OpenAiAuthPresentationService(IOpenAiOwnerManagementClient management)
{
    internal ValueTask<OpenAiAuthOperationResult> GetStatusAsync(
        CancellationToken cancellationToken = default)
        => management.GetAuthStatusAsync(cancellationToken);

    internal ValueTask<OpenAiAuthOperationResult> DisconnectAsync(
        CancellationToken cancellationToken = default)
        => management.DisconnectAuthAsync(cancellationToken);
}
