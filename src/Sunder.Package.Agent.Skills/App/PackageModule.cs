using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Shared.Composition;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Package.Agent.Skills.PackageViews;
using Sunder.Package.Agent.Skills.Runtime;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Skills;

public sealed class AppPackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton(provider => new SkillAppRuntimeGateway(
            provider.GetRequiredService<ISunderRpcClient>(),
            context));
        services.AddSingletonAlias<ISkillManagementGateway, SkillAppRuntimeGateway>();
        services.AddTransient(provider => new SkillSettingsViewModel(
            provider.GetRequiredService<ISkillManagementGateway>()));
        services.AddTransient(provider => new SkillSettingsView(
            provider.GetRequiredService<SkillSettingsViewModel>(),
            context,
            provider.GetRequiredService<IPackageNotificationService>()));
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
        => registry.RegisterSettingsView<SkillSettingsView>();
}
