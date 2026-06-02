using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Shared.Stacks;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Provider.Gemini;

public sealed class PackageModule : ISunderPackageModule
{
    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
        services.AddTransient<GeminiSettingsViewModel>();
        services.AddSingleton<GeminiAgentProvider>();
        services.AddSingleton<GeminiEmbeddingProvider>();
        services.AddSingleton<IAgentChatProvider>(serviceProvider => serviceProvider.GetRequiredService<GeminiAgentProvider>());
        services.AddSingleton<IAgentEmbeddingProvider>(serviceProvider => serviceProvider.GetRequiredService<GeminiEmbeddingProvider>());
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterConfigurationSchema(GeminiProviderConfiguration.Schema);
        registry.RegisterExtension(SunderStackExtensionPoints.StackContributors, new PackageConfigurationStackContributor(GeminiProviderConfiguration.Schema, services.GetRequiredService<IPackageContext>()));
        registry.RegisterSettingsView<GeminiSettingsView>();
        registry.RegisterExtension(PackageExtensionPoints.ChatProviders, services.GetRequiredService<GeminiAgentProvider>());
        registry.RegisterExtension(PackageExtensionPoints.EmbeddingProviders, services.GetRequiredService<GeminiEmbeddingProvider>());
    }
}
