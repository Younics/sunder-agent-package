using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.Gemini;

public sealed class PackageModule : ISunderPackageModule
{
    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<GeminiAgentProvider>();
        services.AddSingleton<GeminiEmbeddingProvider>();
        services.AddSingleton<IAgentChatProvider>(serviceProvider => serviceProvider.GetRequiredService<GeminiAgentProvider>());
        services.AddSingleton<IAgentEmbeddingProvider>(serviceProvider => serviceProvider.GetRequiredService<GeminiEmbeddingProvider>());
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterConfigurationSchema(GeminiProviderConfiguration.Schema);
        registry.RegisterExtension(PackageExtensionPoints.ChatProviders, services.GetRequiredService<GeminiAgentProvider>());
        registry.RegisterExtension(PackageExtensionPoints.EmbeddingProviders, services.GetRequiredService<GeminiEmbeddingProvider>());
    }
}
