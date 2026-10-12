using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Provider.OpenAI;

[SunderService(Key = "management", ContractId = "sunder.package.agent.provider.openai.management", ServiceId = "management")]
[SunderServiceProvider(ProviderId = "openai.management")]
[SunderServiceError("openai.management.failed", Description = "The OpenAI management request failed.")]
internal sealed partial class OpenAiManagementService(OpenAiAuthOperationHandler auth, ProviderCredentialAccessor credentials)
{
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal OpenAiManagementService(OpenAiAuthOperationHandler auth, Auth.ApiKeyAuthStrategy strategy) : this(auth, strategy.Credentials) { }

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["openai.management.failed"])]
    public async ValueTask<OpenAiAuthOperationResult> GetAuthStatusAsync(CancellationToken token = default)
        => Bound(await auth.GetStatusAsync(token).ConfigureAwait(false));
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["openai.management.failed"])]
    public async ValueTask<OpenAiAuthOperationResult> DisconnectAuthAsync(CancellationToken token = default)
        => Bound(await auth.DisconnectAsync(token).ConfigureAwait(false));
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["openai.management.failed"])]
    public async ValueTask<bool> GetCredentialStatusAsync(CancellationToken token = default)
        => await credentials.HasCredentialAsync(token).ConfigureAwait(false);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["openai.management.failed"])]
    public async ValueTask<bool> SetCredentialAsync([SunderValueConstraints(MinLength = 1)] string credential, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(credential))
            throw new SunderRpcException(new(SunderRpcErrorKind.Domain, "openai.management.failed", "A non-empty OpenAI API key is required."));
        await credentials.SetCredentialAsync(credential, token).ConfigureAwait(false);
        return true;
    }
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["openai.management.failed"])]
    public async ValueTask<bool> ClearCredentialAsync(CancellationToken token = default)
    {
        await credentials.DeleteCredentialAsync(token).ConfigureAwait(false);
        return false;
    }

    private static OpenAiAuthOperationResult Bound(OpenAiAuthOperationResult value)
        => value with
        {
            ExpiresAtUtc = value.ExpiresAtUtc?.ToUniversalTime(),
            ErrorMessage = string.IsNullOrWhiteSpace(value.ErrorMessage) ? null
                : value.ErrorMessage.Length <= 512 ? value.ErrorMessage : value.ErrorMessage[..512],
        };
}
