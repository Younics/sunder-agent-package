using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Settings;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Agent.Provider.OpenAI.Auth;

public sealed class ApiKeyAuthStrategy : ISunderWorkerSettingsSchemaProvider
{
    private readonly ProviderCredentialAccessor _credentials;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public ApiKeyAuthStrategy(IPackageContext packageContext)
        : this(new ProviderCredentialAccessor(packageContext.Secrets, OpenAiProviderConfiguration.ApiKeySecretKey))
    {
    }

    internal ApiKeyAuthStrategy(ProviderCredentialAccessor credentials)
    {
        _credentials = credentials;
    }

    public string ModeId { get; } = "api-key";

    public PackageSettingsSchema SettingsSchema => OpenAiProviderConfiguration.Schema;

    public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default)
        => _credentials.GetCredentialAsync(cancellationToken);

    internal ProviderCredentialAccessor Credentials => _credentials;
}
