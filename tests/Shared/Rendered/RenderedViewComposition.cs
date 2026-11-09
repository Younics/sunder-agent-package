extern alias AgentApp;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Rpc;
using AppRuntime = AgentApp::Sunder.Package.Agent.Runtime;
using IAgentOwnerRuntimeProvider = AgentApp::Sunder.Generated.Packages.Younics_Ai_Agent_Services.IAgentRuntimeService;
using static Sunder.Package.Agent.Presentation.Tests.RenderedShellFixture;

namespace Sunder.Package.Agent.Presentation.Tests;

internal static class RenderedViewComposition
{
    internal static AgentServices CreateAgentServices(RegressionTestPackageScope scope)
    {
        var extensionCatalog = new RegressionTestExtensionCatalog();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store, extensionCatalog.SessionCleaners);
        var workspaceService = new AgentWorkspaceService(store, extensionCatalog.SessionCleaners, sessionService);
        var targetService = new AgentExecutionTargetService(extensionCatalog.ExecutionTargets);
        var toolService = new AgentToolService(
            sessionService,
            workspaceService,
            targetService,
            extensionCatalog.ToolSources,
            extensionCatalog.ObservedToolExecutions);
        return new AgentServices(
            extensionCatalog,
            sessionService,
            workspaceService,
            targetService,
            extensionCatalog.CreateProfileService(store, toolService));
    }

    internal static ServiceCollection CreateHistoryAppServices(
        RegressionTestPackageScope scope,
        IAgentOwnerRuntimeProvider runtime)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(scope.Context);
        services.AddSingleton(runtime);
        services.AddSingleton<IAgentOwnerRuntimeProvider>(runtime);
        services.AddSingleton(new AppRuntime.AgentAppRuntimeGateway(runtime));
        services.AddSingleton<RegressionTestExtensionCatalog>();
        services.AddSingleton<ISunderRpcClient>(provider => provider.GetRequiredService<RegressionTestExtensionCatalog>().RpcForCaller("younics.ai.agent"));
        services.AddSingleton<IPackageShellViewService, NoOpPackageShellViewService>();
        services.AddSingleton<IPackageNotificationService>(NullPackageNotificationService.Instance);
        services.AddSingleton<IBackgroundTaskQueue, NoOpBackgroundTaskQueue>();
        new AgentApp::Sunder.Generated.Package.AppModule().ConfigureAppServices(services, scope.Context);
        return services;
    }

    internal sealed record AgentServices(
        RegressionTestExtensionCatalog ExtensionCatalog,
        AgentSessionService SessionService,
        AgentWorkspaceService WorkspaceService,
        AgentExecutionTargetService TargetService,
        AgentProfileService ProfileService);
}
