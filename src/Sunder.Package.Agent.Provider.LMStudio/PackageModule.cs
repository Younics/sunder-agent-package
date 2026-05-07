using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.LMStudio;

public sealed class PackageModule : ISunderPackageModule
{
    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<LMStudioAgentProvider>();
        services.AddSingleton<LMStudioEmbeddingProvider>();
        services.AddSingleton<IAgentChatProvider>(serviceProvider => serviceProvider.GetRequiredService<LMStudioAgentProvider>());
        services.AddSingleton<IAgentEmbeddingProvider>(serviceProvider => serviceProvider.GetRequiredService<LMStudioEmbeddingProvider>());
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterConfigurationSchema(LMStudioProviderConfiguration.Schema);
        registry.RegisterExtension(PackageExtensionPoints.ChatProviders, services.GetRequiredService<LMStudioAgentProvider>());
        registry.RegisterExtension(PackageExtensionPoints.EmbeddingProviders, services.GetRequiredService<LMStudioEmbeddingProvider>());
    }
}
