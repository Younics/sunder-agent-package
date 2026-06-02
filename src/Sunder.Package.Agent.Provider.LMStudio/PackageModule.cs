using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Shared.Stacks;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Provider.LMStudio;

public sealed class PackageModule : ISunderPackageModule
{
    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
        services.AddTransient<LMStudioSettingsViewModel>();
        services.AddSingleton<LMStudioAgentProvider>();
        services.AddSingleton<LMStudioEmbeddingProvider>();
        services.AddSingleton<IAgentChatProvider>(serviceProvider => serviceProvider.GetRequiredService<LMStudioAgentProvider>());
        services.AddSingleton<IAgentEmbeddingProvider>(serviceProvider => serviceProvider.GetRequiredService<LMStudioEmbeddingProvider>());
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterConfigurationSchema(LMStudioProviderConfiguration.Schema);
        registry.RegisterExtension(SunderStackExtensionPoints.StackContributors, new PackageConfigurationStackContributor(LMStudioProviderConfiguration.Schema, services.GetRequiredService<IPackageContext>()));
        registry.RegisterSettingsView<LMStudioSettingsView>();
        registry.RegisterExtension(PackageExtensionPoints.ChatProviders, services.GetRequiredService<LMStudioAgentProvider>());
        registry.RegisterExtension(PackageExtensionPoints.EmbeddingProviders, services.GetRequiredService<LMStudioEmbeddingProvider>());
    }
}
