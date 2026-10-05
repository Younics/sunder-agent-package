using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Provider.OpenAI;

internal sealed class OpenAiRuntimeManagementClient(OpenAiAuthOperationHandler auth, ProviderCredentialAccessor credentials) : IOpenAiOwnerManagementClient
{
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal OpenAiRuntimeManagementClient(OpenAiAuthOperationHandler auth, Auth.ApiKeyAuthStrategy strategy) : this(auth, strategy.Credentials) { }

    public ValueTask<OpenAiAuthOperationResult> GetAuthStatusAsync(CancellationToken token = default) => auth.GetStatusAsync(token);
    public ValueTask<OpenAiAuthOperationResult> DisconnectAuthAsync(CancellationToken token = default) => auth.DisconnectAsync(token);
    public async ValueTask<bool> HasCredentialAsync(CancellationToken token = default) => await credentials.HasCredentialAsync(token).ConfigureAwait(false);
    public async ValueTask<bool> SetCredentialAsync(string credential, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(credential))
            throw new SunderRpcException(new(SunderRpcErrorKind.Domain, "openai.management.failed", "A non-empty OpenAI API key is required."));
        await credentials.SetCredentialAsync(credential, token).ConfigureAwait(false);
        return true;
    }
    public async ValueTask<bool> ClearCredentialAsync(CancellationToken token = default)
    {
        await credentials.DeleteCredentialAsync(token).ConfigureAwait(false);
        return false;
    }
}
