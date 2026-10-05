using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Settings;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Agent.Provider.Anthropic;

internal sealed class ProviderSettings(IPackageContext context) : ISunderWorkerSettingsSchemaProvider
{
    internal ProviderCredentialAccessor Credentials { get; } = new(context.Secrets, AnthropicProviderConfiguration.ApiKeySecretKey);
    public PackageSettingsSchema SettingsSchema => AnthropicProviderConfiguration.Schema;
}
