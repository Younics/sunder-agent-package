using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Provider.Shared;
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

    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
        => ConfigureRuntimeServices(services, context);

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterConfigurationSchema(LMStudioProviderConfiguration.Schema);
        registry.RegisterExtension(PackageExtensionPoints.ChatProviders, services.GetRequiredService<LMStudioAgentProvider>());
        registry.RegisterExtension(PackageExtensionPoints.EmbeddingProviders, services.GetRequiredService<LMStudioEmbeddingProvider>());
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterSettingsView<LMStudioSettingsView>();
        registry.RegisterExtension(PackageExtensionPoints.ChatProviders, services.GetRequiredService<LMStudioAgentProvider>());
        registry.RegisterExtension(PackageExtensionPoints.EmbeddingProviders, services.GetRequiredService<LMStudioEmbeddingProvider>());
    }
}

public sealed class AppPackageModule : ISunderAppPackageModule
{
    private readonly PackageModule _module = new();

    public void ConfigureAppServices(IServiceCollection services, IPackageContext context) => _module.ConfigureAppServices(services, context);
    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services) => _module.RegisterAppContributions(registry, services);
}
