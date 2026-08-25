using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Shared.Composition;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Package.Agent.Subagents.PackageViews;
using Sunder.Package.Agent.Subagents.Runtime;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Subagents;

public sealed class AppPackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton(provider => new SubagentAppRuntimeGateway(
            provider.GetRequiredService<ISunderRpcClient>(),
            context.Logging.LoggerFactory));
        services.AddSingletonAlias<ISubagentManagementGateway, SubagentAppRuntimeGateway>();
        services.AddSingletonAlias<ISubsessionSessionReader, SubagentAppRuntimeGateway>();
        services.AddSingletonAlias<ISubsessionCheckpointReader, SubagentAppRuntimeGateway>();
        services.AddSingletonAlias<ISubsessionTranscriptPageReader, SubagentAppRuntimeGateway>();
        services.AddSingletonAlias<ISubsessionChangeNotifications, SubagentAppRuntimeGateway>();
        services.AddTransient(provider => new SubagentsViewModel(
            provider.GetRequiredService<ISubagentManagementGateway>(),
            provider.GetService<IPackageSettingsNavigationService>()));
        services.AddTransient(provider => new SubsessionsViewModel(
            provider.GetRequiredService<ISubsessionSessionReader>(),
            provider.GetRequiredService<ISubsessionCheckpointReader>(),
            provider.GetRequiredService<ISubsessionTranscriptPageReader>(),
            provider.GetRequiredService<ISubsessionChangeNotifications>(),
            PresentationDispatcher.Capture(),
            new PresentationOperationObserver(
                context,
                provider.GetRequiredService<IPackageNotificationService>(),
                "Subsessions")));
        services.AddTransient(provider => new SubsessionsView(
            provider.GetRequiredService<SubsessionsViewModel>(),
            context,
            provider.GetRequiredService<IPackageNotificationService>()));
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterPackageView<SubsessionsView>(new PackageViewRegistration(
            SubagentConstants.SubsessionsViewId,
            "Subsessions",
            "assets/sub-session-icon.png",
            defaultPlacement: PackageViewPlacement.LeftTop));
        registry.RegisterPackageView<SubagentsView>(new PackageViewRegistration(
            "sunder.package.agent.subagents",
            "Subagents",
            "assets/sub-profile-icon.png",
            defaultPlacement: PackageViewPlacement.RightTop));
    }
}
