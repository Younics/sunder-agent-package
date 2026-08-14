namespace Sunder.Package.Agent.Provider.OpenAI;

internal sealed record OpenAiAuthOperationResult(
    bool IsConnected,
    bool HasCachedSession,
    DateTimeOffset? ExpiresAtUtc,
    string? ErrorMessage = null);

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
