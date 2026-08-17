using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Notifications;
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
            new PackageCallbackFlowRunner(context.Callbacks),
            provider.GetRequiredService<ProviderCredentialOwnerClient>(),
            new PresentationOperationObserver(
                context,
                provider.GetService<IPackageNotificationService>()
                    ?? NullPackageNotificationService.Instance,
                "OpenAI settings")));
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
        => registry.RegisterSettingsView<OpenAiSettingsView>();
}
