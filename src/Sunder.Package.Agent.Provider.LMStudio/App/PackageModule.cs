using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;

namespace Sunder.Package.Agent.Provider.LMStudio;

public sealed class AppPackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<ProviderCredentialOwnerRpcClient>();
        services.AddTransient(serviceProvider => new LMStudioSettingsViewModel(
            context,
            serviceProvider.GetRequiredService<ProviderCredentialOwnerRpcClient>()));
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
        => registry.RegisterSettingsView<LMStudioSettingsView>();
}
