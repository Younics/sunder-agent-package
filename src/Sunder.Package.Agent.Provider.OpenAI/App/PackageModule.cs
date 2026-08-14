using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Provider.OpenAI;

public sealed class AppPackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<IOpenAiOwnerManagementClient>(provider =>
            new OpenAiOwnerManagementRpcClient(provider.GetRequiredService<ISunderRpcClient>()));
        services.AddSingleton<OpenAiAuthPresentationService>();
        services.AddSingleton<ProviderCredentialOwnerClient>();
        services.AddTransient(provider => new OpenAiSettingsViewModel(
            context,
            provider.GetRequiredService<OpenAiAuthPresentationService>(),
            provider.GetRequiredService<ProviderCredentialOwnerClient>()));
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
        => registry.RegisterSettingsView<OpenAiSettingsView>();
}
