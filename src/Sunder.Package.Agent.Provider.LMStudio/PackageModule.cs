using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Shared.Composition;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;

namespace Sunder.Package.Agent.Provider.LMStudio;

public sealed class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton(new ProviderCredentialAccessor(
            context.Secrets,
            LMStudioProviderConfiguration.ApiKeyKey));
        services.AddSingleton(serviceProvider => new LMStudioConnection(
            context,
            serviceProvider.GetRequiredService<ProviderCredentialAccessor>()));
        services.AddSingleton(serviceProvider => new LMStudioModelCatalogService(
            serviceProvider.GetRequiredService<LMStudioConnection>()));
        services.AddSingleton(serviceProvider => new LMStudioAgentProvider(
            context,
            serviceProvider.GetRequiredService<LMStudioConnection>(),
            serviceProvider.GetRequiredService<LMStudioModelCatalogService>()));
        services.AddSingleton(serviceProvider => new LMStudioEmbeddingProvider(
            context,
            serviceProvider.GetRequiredService<LMStudioConnection>(),
            serviceProvider.GetRequiredService<LMStudioModelCatalogService>()));
        services.AddSingletonAlias<IAgentChatProvider, LMStudioAgentProvider>();
        services.AddSingletonAlias<IAgentEmbeddingProvider, LMStudioEmbeddingProvider>();
    }

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterConfigurationSchema(LMStudioProviderConfiguration.Schema);
        registry.RegisterExtension(PackageExtensionPoints.ChatProviders, services.GetRequiredService<LMStudioAgentProvider>());
        registry.RegisterExtension(PackageExtensionPoints.EmbeddingProviders, services.GetRequiredService<LMStudioEmbeddingProvider>());
    }
}

public sealed class AppPackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
        => services.AddTransient(_ => new LMStudioSettingsViewModel(context));

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
        => registry.RegisterSettingsView<LMStudioSettingsView>();
}
