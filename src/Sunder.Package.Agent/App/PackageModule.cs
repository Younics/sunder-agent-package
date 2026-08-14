using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Shared.Composition;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent;

public sealed class AppPackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        services.TryAddSingleton<AgentRpcCatalog>();
        services.AddSingleton(provider => provider.GetService<IAgentOwnerClient>() is { } ownerClient
            ? new AgentAppRuntimeGateway(ownerClient)
            : new AgentAppRuntimeGateway(provider.GetRequiredService<ISunderRpcClient>()));
        services.AddSingletonAlias<IAgentRuntimeAvailability, AgentAppRuntimeGateway>();
        services.AddSingletonAlias<IAgentProfileGateway, AgentAppRuntimeGateway>();
        services.AddSingletonAlias<IAgentWorkspaceGateway, AgentAppRuntimeGateway>();
        services.AddSingletonAlias<IAgentSessionGateway, AgentAppRuntimeGateway>();
        services.AddSingletonAlias<IAgentPermissionGateway, AgentAppRuntimeGateway>();
        services.AddSingletonAlias<IAgentRunGateway, AgentAppRuntimeGateway>();
        services.AddSingletonAlias<IAgentAttachmentGateway, AgentAppRuntimeGateway>();
        services.AddSingletonAlias<IAgentExecutionGateway, AgentAppRuntimeGateway>();
        services.AddSingletonAlias<IAgentHistorySearchGateway, AgentAppRuntimeGateway>();
        services.AddSingletonAlias<IAgentTranscriptAnchorGateway, AgentAppRuntimeGateway>();
        services.AddSingleton<AgentChatSelectionStateService>();
        services.AddSingleton<AgentToolPresentationService>();
        services.AddTransient<AgentWorkspacesViewContext>();
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterPackageView<AgentChatView>(new PackageViewRegistration(
            "sunder.package.agent.chat", "Agent Chat", "Assets/chat-icon.png",
            defaultPlacement: PackageViewPlacement.Middle));
        registry.RegisterPackageView<AgentWorkspacesView>(new PackageViewRegistration(
            "sunder.package.agent.workspaces", "Workspaces", "Assets/workspace-icon.png",
            defaultPlacement: PackageViewPlacement.RightTop));
        registry.RegisterPackageView<AgentProfilesView>(new PackageViewRegistration(
            "sunder.package.agent.profiles", "Agents", "Assets/profile-icon.png",
            defaultPlacement: PackageViewPlacement.RightTop));
        registry.RegisterPackageView<AgentHistorySearchView>(new PackageViewRegistration(
            "sunder.package.agent.history", "History", "Assets/session-icon.png",
            defaultPlacement: PackageViewPlacement.RightTop));
        registry.RegisterSettingsView<AgentPermissionsView>();
    }
}
