namespace Sunder.Package.Agent.Provider.OpenAI;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record OpenAiAuthOperationResult(
    bool IsConnected,
    bool HasCachedSession,
    DateTimeOffset? ExpiresAtUtc,
    string? ErrorMessage = null);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal interface IOpenAiOwnerManagementClient
{
    ValueTask<OpenAiAuthOperationResult> GetAuthStatusAsync(
        CancellationToken cancellationToken = default);

    ValueTask<OpenAiAuthOperationResult> DisconnectAuthAsync(
        CancellationToken cancellationToken = default);

    ValueTask<bool> HasCredentialAsync(CancellationToken cancellationToken = default);

    ValueTask<bool> SetCredentialAsync(
        string credential,
        CancellationToken cancellationToken = default);

    ValueTask<bool> ClearCredentialAsync(CancellationToken cancellationToken = default);
}
