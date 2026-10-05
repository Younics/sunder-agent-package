namespace Sunder.Package.Agent.Provider.Gemini.Rpc;

internal sealed partial class CredentialSettingsService
{
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal CredentialSettingsService(ProviderSettings settings) : this(settings.Credentials) { }
}
