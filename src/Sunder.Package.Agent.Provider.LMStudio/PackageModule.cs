using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Protocol;
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
        services.AddSingleton<ProviderCredentialRuntimeHandler>();
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
        registry.RegisterSettingsSchema(LMStudioProviderConfiguration.Schema);
        registry.RegisterRpcProvider("lmstudio.chat", AgentChatProviderRpc.CreateHandler(services.GetRequiredService<LMStudioAgentProvider>()));
        registry.RegisterRpcProvider("lmstudio.embedding", AgentEmbeddingProviderRpc.CreateHandler(services.GetRequiredService<LMStudioEmbeddingProvider>()));
        var credentials = services.GetRequiredService<ProviderCredentialRuntimeHandler>();
        registry.RegisterRuntimeOperation(ProviderCredentialRuntimeOperations.Query, credentials);
        registry.RegisterRuntimeOperation(ProviderCredentialRuntimeOperations.Command, credentials);
    }
}

public sealed class AppPackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<ProviderCredentialAppRuntimeGateway>();
        services.AddTransient(serviceProvider => new LMStudioSettingsViewModel(
            context,
            serviceProvider.GetRequiredService<ProviderCredentialAppRuntimeGateway>()));
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
        => registry.RegisterSettingsView<LMStudioSettingsView>();
}
