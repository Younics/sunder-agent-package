using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Shared.Stacks;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Provider.Anthropic;

public sealed class PackageModule : ISunderPackageModule
{
    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
        services.AddTransient<AnthropicSettingsViewModel>();
        services.AddSingleton<AnthropicAgentProvider>();
        services.AddSingleton<IAgentChatProvider>(serviceProvider => serviceProvider.GetRequiredService<AnthropicAgentProvider>());
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterConfigurationSchema(AnthropicProviderConfiguration.Schema);
        registry.RegisterExtension(SunderStackExtensionPoints.StackContributors, new PackageConfigurationStackContributor(AnthropicProviderConfiguration.Schema, services.GetRequiredService<IPackageContext>()));
        registry.RegisterSettingsView<AnthropicSettingsView>();
        registry.RegisterExtension(PackageExtensionPoints.ChatProviders, services.GetRequiredService<AnthropicAgentProvider>());
    }
}
