using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.Shared;

internal sealed class ProviderCredentialAccessor(IPackageSecrets secrets, string secretKey)
{
    internal string SecretKey { get; } = string.IsNullOrWhiteSpace(secretKey)
        ? throw new ArgumentException("A canonical secret key is required.", nameof(secretKey))
        : secretKey;

    internal bool HasCredential => !string.IsNullOrWhiteSpace(GetCredential());

    internal string? GetCredential() => secrets.GetSecret(SecretKey);

    internal bool SetCredentialIfProvided(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        secrets.SetSecret(SecretKey, value.Trim());
        return true;
    }

    internal void DeleteCredential() => secrets.DeleteSecret(SecretKey);
}
