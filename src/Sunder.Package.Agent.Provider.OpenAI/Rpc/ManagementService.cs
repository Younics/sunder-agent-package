using System.Globalization;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Provider.OpenAI.Rpc;

[SunderRpcService(SunderRpcExposure.Package, ContractId = "sunder.package.agent.provider.openai.management", ServiceId = "management", IncludeSchemaDialect = true)]
[SunderRpcProvider(ProviderId = "openai.management")]
[SunderRpcDomainError("openai.management.failed", Description = "The OpenAI management request failed.")]
internal sealed partial class ManagementService(IOpenAiOwnerManagementClient operations)
{
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = ["openai.management.failed"])]
    public async ValueTask<AuthStatus> GetAuthStatusAsync(ManagementQuery request, CancellationToken token = default)
        => Map(await operations.GetAuthStatusAsync(token).ConfigureAwait(false));
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = ["openai.management.failed"])]
    public async ValueTask<AuthStatus> DisconnectAuthAsync(ManagementQuery request, CancellationToken token = default)
        => Map(await operations.DisconnectAuthAsync(token).ConfigureAwait(false));
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = ["openai.management.failed"])]
    public async ValueTask<CredentialStatus> GetCredentialStatusAsync(ManagementQuery request, CancellationToken token = default)
        => new() { HasStoredCredential = await operations.HasCredentialAsync(token).ConfigureAwait(false) };
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = ["openai.management.failed"])]
    public async ValueTask<CredentialStatus> SetCredentialAsync(CredentialRequest request, CancellationToken token = default)
        => new() { HasStoredCredential = await operations.SetCredentialAsync(request.Credential.Value, token).ConfigureAwait(false) };
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = ["openai.management.failed"])]
    public async ValueTask<CredentialStatus> ClearCredentialAsync(ManagementQuery request, CancellationToken token = default)
        => new() { HasStoredCredential = await operations.ClearCredentialAsync(token).ConfigureAwait(false) };

    private static AuthStatus Map(OpenAiAuthOperationResult result) => new()
    {
        IsConnected = result.IsConnected,
        HasCachedSession = result.HasCachedSession,
        ExpiresAtUtc = result.ExpiresAtUtc is { } expiry
            ? new SunderRpcOffsetDateTime(expiry.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture))
            : default(SunderRpcOptional<SunderRpcOffsetDateTime>),
        ErrorMessage = string.IsNullOrWhiteSpace(result.ErrorMessage) ? default(SunderRpcOptional<StatusMessage>)
            : new StatusMessage { Value = result.ErrorMessage.Length <= 512 ? result.ErrorMessage : result.ErrorMessage[..512] },
    };
}

internal sealed class ManagementQuery;
[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)]
internal sealed class StatusMessage { public required string Value { get; init; } }
internal sealed class AuthStatus
{
    public required bool IsConnected { get; init; }
    public required bool HasCachedSession { get; init; }
    public SunderRpcOptional<SunderRpcOffsetDateTime> ExpiresAtUtc { get; init; }
    public SunderRpcOptional<StatusMessage> ErrorMessage { get; init; }
}
[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)]
internal sealed class ApiKey { public required string Value { get; init; } }
internal sealed class CredentialRequest { public required ApiKey Credential { get; init; } }
internal sealed class CredentialStatus { public required bool HasStoredCredential { get; init; } }
