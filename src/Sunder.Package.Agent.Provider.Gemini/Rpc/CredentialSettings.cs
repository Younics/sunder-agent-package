using System.Text.Json.Serialization;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Provider.Gemini.Rpc;

[SunderRpcService(SunderRpcExposure.Package, ContractId = "sunder.package.agent.provider.gemini.credentials",
    ServiceId = "credential-settings", IncludeSchemaDialect = true)]
[SunderRpcProvider(ProviderId = "gemini.credentials")]
[SunderRpcDomainError("provider.credential.command-invalid", Description = "The credential command kind is not supported.")]
[SunderRpcDomainError("provider.credential.required", Description = "A set command requires a non-empty credential.")]
[SunderRpcDomainError("provider.credential.unexpected", Description = "A clear command must not contain a credential.")]
internal sealed partial class CredentialSettingsService(ProviderCredentialAccessor credentials)
{
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public async ValueTask<CredentialSnapshot> QueryAsync(CredentialQuery request, CancellationToken cancellationToken = default)
        => new() { HasStoredCredential = await credentials.HasCredentialAsync(cancellationToken).ConfigureAwait(false) };

    [SunderRpcMethod(Errors = ["provider.credential.command-invalid", "provider.credential.required", "provider.credential.unexpected"])]
    public async ValueTask<CredentialSnapshot> CommandAsync(CredentialCommand request, CancellationToken cancellationToken = default)
    {
        switch (request.Kind)
        {
            case CredentialOperation.Set:
                var value = request.Credential.IsPresent ? request.Credential.Value.Value : null;
                if (string.IsNullOrWhiteSpace(value)) throw Domain("provider.credential.required", "A non-empty credential is required for a set command.");
                await credentials.SetCredentialAsync(value, cancellationToken).ConfigureAwait(false);
                break;
            case CredentialOperation.Clear:
                if (request.Credential.IsPresent) throw Domain("provider.credential.unexpected", "A clear command must not contain a credential.");
                await credentials.DeleteCredentialAsync(cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw Domain("provider.credential.command-invalid", "Unknown credential command.");
        }
        return new() { HasStoredCredential = await credentials.HasCredentialAsync(cancellationToken).ConfigureAwait(false) };
    }

    private static SunderRpcException Domain(string code, string message) => new(new(SunderRpcErrorKind.Domain, code, message));
}

internal enum CredentialOperation
{
    [JsonStringEnumMemberName("set")] Set,
    [JsonStringEnumMemberName("clear")] Clear,
}

[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)]
internal sealed class CredentialValue
{
    public required string Value { get; init; }
}

internal sealed class CredentialCommand
{
    public SunderRpcOptional<CredentialValue> Credential { get; init; }
    public required CredentialOperation Kind { get; init; }
}

internal sealed class CredentialQuery;

internal sealed class CredentialSnapshot
{
    public required bool HasStoredCredential { get; init; }
}
