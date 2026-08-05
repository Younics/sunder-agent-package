using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Provider.OpenAI.Auth;
using Sunder.Package.Agent.Provider.OpenAI.Transport;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Shared.Composition;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;

namespace Sunder.Package.Agent.Provider.OpenAI;

public sealed class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton(new ProviderCredentialAccessor(
            context.Secrets,
            OpenAiProviderConfiguration.ApiKeySecretKey));
        services.AddSingleton<ProviderCredentialRuntimeHandler>();
        services.AddSingleton(serviceProvider => new ApiKeyAuthStrategy(
            serviceProvider.GetRequiredService<ProviderCredentialAccessor>()));
        services.AddSingleton<CodexConnectedAuthStrategy>();
        services.AddSingleton(_ => CodexHttpClientFactory.CreateBackendClient());
        services.AddSingleton<CodexResponseContinuationStore>();
        services.AddSingleton<CodexConnectedTransport>();
        services.AddSingleton<OpenAiPackageAuthHandler>();
        services.AddSingletonAlias<IPackageAuthHandler, OpenAiPackageAuthHandler>();
        services.AddSingletonAlias<IPackageCallbackHandler, OpenAiPackageAuthHandler>();
        services.AddSingleton<OpenAiAuthOperationHandler>();
        services.AddSingleton<OpenAiAgentProvider>();
        services.AddSingleton<OpenAiEmbeddingProvider>();
        services.AddSingletonAlias<IAgentChatProvider, OpenAiAgentProvider>();
        services.AddSingletonAlias<IAgentEmbeddingProvider, OpenAiEmbeddingProvider>();
    }

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterSettingsSchema(OpenAiProviderConfiguration.Schema);
        registry.RegisterRpcProvider("openai.chat", AgentChatProviderRpc.CreateHandler(services.GetRequiredService<OpenAiAgentProvider>()));
        registry.RegisterRpcProvider("openai.embedding", AgentEmbeddingProviderRpc.CreateHandler(services.GetRequiredService<OpenAiEmbeddingProvider>()));
        registry.RegisterRpcProvider("openai.continuation.cleaner", AgentSessionCleanerRpc.CreateHandler(services.GetRequiredService<CodexResponseContinuationStore>()));
        registry.RegisterRuntimeOperation(
            OpenAiRuntimeOperations.Auth,
            services.GetRequiredService<OpenAiAuthOperationHandler>());
        var credentials = services.GetRequiredService<ProviderCredentialRuntimeHandler>();
        registry.RegisterRuntimeOperation(ProviderCredentialRuntimeOperations.Query, credentials);
        registry.RegisterRuntimeOperation(ProviderCredentialRuntimeOperations.Command, credentials);
    }
}

public sealed class AppPackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<OpenAiAuthPresentationService>();
        services.AddSingleton<ProviderCredentialAppRuntimeGateway>();
        services.AddTransient(serviceProvider => new OpenAiSettingsViewModel(
            context,
            serviceProvider.GetRequiredService<OpenAiAuthPresentationService>(),
            serviceProvider.GetRequiredService<ProviderCredentialAppRuntimeGateway>()));
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
        => registry.RegisterSettingsView<OpenAiSettingsView>();
}
