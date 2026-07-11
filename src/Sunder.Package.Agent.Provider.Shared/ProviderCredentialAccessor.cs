using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.Shared;

internal sealed class ProviderCredentialAccessor(IPackageSecrets secrets, string secretKey)
{
    internal string SecretKey { get; } = string.IsNullOrWhiteSpace(secretKey)
        ? throw new ArgumentException("A canonical secret key is required.", nameof(secretKey))
        : secretKey;

    internal async Task<bool> HasCredentialAsync(CancellationToken cancellationToken = default)
        => !string.IsNullOrWhiteSpace(await GetCredentialAsync(cancellationToken).ConfigureAwait(false));

    internal Task<string?> GetCredentialAsync(CancellationToken cancellationToken = default)
        => secrets.GetSecretAsync(SecretKey, cancellationToken);

    internal async Task<bool> SetCredentialIfProvidedAsync(
        string? value,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        await secrets.SetSecretAsync(SecretKey, value.Trim(), cancellationToken).ConfigureAwait(false);
        return true;
    }

    internal Task DeleteCredentialAsync(CancellationToken cancellationToken = default)
        => secrets.DeleteSecretAsync(SecretKey, cancellationToken);
}
