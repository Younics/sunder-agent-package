using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Shared.Composition;
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
        services.AddSingleton<ProviderCredentialRuntimeHandler>();
        services.AddSingleton(serviceProvider => new GeminiAgentProvider(
            context,
            serviceProvider.GetRequiredService<ProviderCredentialAccessor>()));
        services.AddSingleton(serviceProvider => new GeminiEmbeddingProvider(
            context,
            serviceProvider.GetRequiredService<ProviderCredentialAccessor>()));
        services.AddSingletonAlias<IAgentChatProvider, GeminiAgentProvider>();
        services.AddSingletonAlias<IAgentEmbeddingProvider, GeminiEmbeddingProvider>();
    }

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterSettingsSchema(GeminiProviderConfiguration.Schema);
        registry.RegisterRpcProvider("gemini.chat", AgentChatProviderRpc.CreateHandler(services.GetRequiredService<GeminiAgentProvider>()));
        registry.RegisterRpcProvider("gemini.embedding", AgentEmbeddingProviderRpc.CreateHandler(services.GetRequiredService<GeminiEmbeddingProvider>()));
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
        services.AddTransient(serviceProvider => new GeminiSettingsViewModel(
            context,
            serviceProvider.GetRequiredService<ProviderCredentialAppRuntimeGateway>()));
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
        => registry.RegisterSettingsView<GeminiSettingsView>();
}
