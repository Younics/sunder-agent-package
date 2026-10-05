using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Settings;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Agent.Provider.LMStudio;

internal sealed class ProviderSettings(IPackageContext context) : ISunderWorkerSettingsSchemaProvider
{
    internal ProviderCredentialAccessor Credentials { get; } = new(context.Secrets, LMStudioProviderConfiguration.ApiKeyKey);
    public PackageSettingsSchema SettingsSchema => LMStudioProviderConfiguration.Schema;
}
