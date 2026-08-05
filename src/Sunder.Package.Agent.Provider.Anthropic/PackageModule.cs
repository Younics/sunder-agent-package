using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Shared.Composition;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;

namespace Sunder.Package.Agent.Provider.Anthropic;

public sealed class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton(new ProviderCredentialAccessor(
            context.Secrets,
            AnthropicProviderConfiguration.ApiKeySecretKey));
        services.AddSingleton<ProviderCredentialRuntimeHandler>();
        services.AddSingleton(serviceProvider => new AnthropicAgentProvider(
            context,
            serviceProvider.GetRequiredService<ProviderCredentialAccessor>()));
        services.AddSingletonAlias<IAgentChatProvider, AnthropicAgentProvider>();
    }

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterSettingsSchema(AnthropicProviderConfiguration.Schema);
        registry.RegisterRpcProvider("anthropic.chat", AgentChatProviderRpc.CreateHandler(services.GetRequiredService<AnthropicAgentProvider>()));
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
        services.AddTransient(serviceProvider => new AnthropicSettingsViewModel(
            context,
            serviceProvider.GetRequiredService<ProviderCredentialAppRuntimeGateway>()));
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
        => registry.RegisterSettingsView<AnthropicSettingsView>();
}
