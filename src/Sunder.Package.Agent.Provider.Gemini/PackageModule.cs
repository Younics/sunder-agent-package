using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;

namespace Sunder.Package.Agent.Provider.Gemini;

public sealed class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton(new ProviderCredentialAccessor(
            context.Secrets,
            GeminiProviderConfiguration.ApiKeySecretKey));
        services.AddTransient(serviceProvider => new GeminiSettingsViewModel(
            context,
            serviceProvider.GetRequiredService<ProviderCredentialAccessor>()));
        services.AddSingleton(serviceProvider => new GeminiAgentProvider(
            context,
            serviceProvider.GetRequiredService<ProviderCredentialAccessor>()));
        services.AddSingleton(serviceProvider => new GeminiEmbeddingProvider(
            context,
            serviceProvider.GetRequiredService<ProviderCredentialAccessor>()));
        services.AddSingleton<IAgentChatProvider>(serviceProvider => serviceProvider.GetRequiredService<GeminiAgentProvider>());
        services.AddSingleton<IAgentEmbeddingProvider>(serviceProvider => serviceProvider.GetRequiredService<GeminiEmbeddingProvider>());
    }

    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
        => ConfigureRuntimeServices(services, context);

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterConfigurationSchema(GeminiProviderConfiguration.Schema);
        registry.RegisterExtension(PackageExtensionPoints.ChatProviders, services.GetRequiredService<GeminiAgentProvider>());
        registry.RegisterExtension(PackageExtensionPoints.EmbeddingProviders, services.GetRequiredService<GeminiEmbeddingProvider>());
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterSettingsView<GeminiSettingsView>();
        registry.RegisterExtension(PackageExtensionPoints.ChatProviders, services.GetRequiredService<GeminiAgentProvider>());
        registry.RegisterExtension(PackageExtensionPoints.EmbeddingProviders, services.GetRequiredService<GeminiEmbeddingProvider>());
    }
}

public sealed class AppPackageModule : ISunderAppPackageModule
{
    private readonly PackageModule _module = new();

    public void ConfigureAppServices(IServiceCollection services, IPackageContext context) => _module.ConfigureAppServices(services, context);
    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services) => _module.RegisterAppContributions(registry, services);
}
