using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Shared.Stacks;
using Sunder.Package.Agent.Provider.OpenAI.Auth;
using Sunder.Package.Agent.Provider.OpenAI.Transport;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Configuration;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Provider.OpenAI;

public sealed class PackageModule : ISunderPackageModule
{
    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<ApiKeyAuthStrategy>();
        services.AddSingleton<CodexConnectedAuthStrategy>();
        services.AddSingleton(_ => CodexHttpClientFactory.CreateBackendClient());
        services.AddSingleton<CodexResponseContinuationStore>();
        services.AddSingleton<CodexConnectedTransport>();
        services.AddTransient<OpenAiSettingsViewModel>();
        services.AddSingleton<OpenAiPackageAuthHandler>();
        services.AddSingleton<IPackageAuthHandler>(serviceProvider => serviceProvider.GetRequiredService<OpenAiPackageAuthHandler>());
        services.AddSingleton<IPackageCallbackHandler>(serviceProvider => serviceProvider.GetRequiredService<OpenAiPackageAuthHandler>());
        services.AddSingleton<OpenAiAgentProvider>();
        services.AddSingleton<OpenAiEmbeddingProvider>();
        services.AddSingleton<IAgentChatProvider>(serviceProvider => serviceProvider.GetRequiredService<OpenAiAgentProvider>());
        services.AddSingleton<IAgentEmbeddingProvider>(serviceProvider => serviceProvider.GetRequiredService<OpenAiEmbeddingProvider>());
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterConfigurationSchema(OpenAiProviderConfiguration.Schema);
        registry.RegisterExtension(SunderStackExtensionPoints.StackContributors, new PackageConfigurationStackContributor(OpenAiProviderConfiguration.Schema, services.GetRequiredService<IPackageContext>()));
        registry.RegisterSettingsView<OpenAiSettingsView>();
        registry.RegisterExtension(PackageExtensionPoints.ChatProviders, services.GetRequiredService<OpenAiAgentProvider>());
        registry.RegisterExtension(PackageExtensionPoints.EmbeddingProviders, services.GetRequiredService<OpenAiEmbeddingProvider>());
        registry.RegisterExtension(PackageExtensionPoints.SessionDataCleaners, services.GetRequiredService<CodexResponseContinuationStore>());
    }
}
