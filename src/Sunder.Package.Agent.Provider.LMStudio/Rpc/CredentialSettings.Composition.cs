namespace Sunder.Package.Agent.Provider.LMStudio.Rpc;

internal sealed partial class CredentialSettingsService
{
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal CredentialSettingsService(ProviderSettings settings) : this(settings.Credentials) { }
}
