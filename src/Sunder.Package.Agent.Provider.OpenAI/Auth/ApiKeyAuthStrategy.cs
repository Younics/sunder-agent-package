using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.OpenAI.Auth;

public sealed class ApiKeyAuthStrategy
{
    private readonly ProviderCredentialAccessor _credentials;

    public ApiKeyAuthStrategy(IPackageContext packageContext)
        : this(new ProviderCredentialAccessor(packageContext.Secrets, OpenAiProviderConfiguration.ApiKeySecretKey))
    {
    }

    internal ApiKeyAuthStrategy(ProviderCredentialAccessor credentials)
    {
        _credentials = credentials;
    }

    public string ModeId { get; } = "api-key";

    public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default)
        => _credentials.GetCredentialAsync(cancellationToken);

    internal ProviderCredentialAccessor Credentials => _credentials;
}
