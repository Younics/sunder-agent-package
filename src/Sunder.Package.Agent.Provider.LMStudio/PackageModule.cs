using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.LMStudio;

public sealed class PackageModule : ISunderPackageModule
{
    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton(new ProviderCredentialAccessor(
            context.Secrets,
            LMStudioProviderConfiguration.ApiKeyKey));
        services.AddSingleton(serviceProvider => new LMStudioConnection(
            context,
            serviceProvider.GetRequiredService<ProviderCredentialAccessor>()));
        services.AddSingleton(serviceProvider => new LMStudioModelCatalogService(
            serviceProvider.GetRequiredService<LMStudioConnection>()));
        services.AddTransient(serviceProvider => new LMStudioSettingsViewModel(
            context,
            serviceProvider.GetRequiredService<ProviderCredentialAccessor>()));
        services.AddSingleton(serviceProvider => new LMStudioAgentProvider(
            context,
            serviceProvider.GetRequiredService<LMStudioConnection>(),
            serviceProvider.GetRequiredService<LMStudioModelCatalogService>()));
        services.AddSingleton(serviceProvider => new LMStudioEmbeddingProvider(
            context,
            serviceProvider.GetRequiredService<LMStudioConnection>(),
            serviceProvider.GetRequiredService<LMStudioModelCatalogService>()));
        services.AddSingleton<IAgentChatProvider>(serviceProvider => serviceProvider.GetRequiredService<LMStudioAgentProvider>());
        services.AddSingleton<IAgentEmbeddingProvider>(serviceProvider => serviceProvider.GetRequiredService<LMStudioEmbeddingProvider>());
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterConfigurationSchema(LMStudioProviderConfiguration.Schema);
        registry.RegisterSettingsView<LMStudioSettingsView>();
        registry.RegisterExtension(PackageExtensionPoints.ChatProviders, services.GetRequiredService<LMStudioAgentProvider>());
        registry.RegisterExtension(PackageExtensionPoints.EmbeddingProviders, services.GetRequiredService<LMStudioEmbeddingProvider>());
    }
}
