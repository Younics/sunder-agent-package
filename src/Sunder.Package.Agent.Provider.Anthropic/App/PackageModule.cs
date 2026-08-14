using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;

namespace Sunder.Package.Agent.Provider.Anthropic;

public sealed class AppPackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<ProviderCredentialOwnerRpcClient>();
        services.AddTransient(serviceProvider => new AnthropicSettingsViewModel(
            context,
            serviceProvider.GetRequiredService<ProviderCredentialOwnerRpcClient>()));
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
        => registry.RegisterSettingsView<AnthropicSettingsView>();
}
