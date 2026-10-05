using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Settings;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Agent.Provider.Gemini;

internal sealed class ProviderSettings(IPackageContext context) : ISunderWorkerSettingsSchemaProvider
{
    internal ProviderCredentialAccessor Credentials { get; } = new(context.Secrets, GeminiProviderConfiguration.ApiKeySecretKey);
    public PackageSettingsSchema SettingsSchema => GeminiProviderConfiguration.Schema;
}
