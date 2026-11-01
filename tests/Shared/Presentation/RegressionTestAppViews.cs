extern alias AgentApp;

using AgentApp::Sunder.Package.Agent.PackageViews;
using AgentApp::Sunder.Package.Agent.Runtime;
using AgentApp::Sunder.Package.Agent.Services;
using AgentApp::Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using AgentServices = AgentApp::Sunder.Generated.Packages.Younics_Ai_Agent_Services;

namespace Sunder.Package.Agent.Tests;

// Existing mixed component scenarios deliberately drive Worker services directly
// while observing App state. Adapt their actual ports, including events/delegates,
// rather than sharing the roles' CLR identities or implementing replacement services.
internal static class RegressionTestAppViews
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, List<WeakReference<AgentChatViewModel>>> ChatViews = new();

    private static AgentChatViewModel TrackChat(object sessionService, AgentChatViewModel view)
    {
        var views = ChatViews.GetOrCreateValue(sessionService);
        lock (views) views.Add(new(view));
        return view;
    }

    internal static void DrainChatViews(object sessionService)
    {
        if (!ChatViews.TryGetValue(sessionService, out var views)) return;
        AgentChatViewModel[] owned;
        lock (views)
        {
            owned = views.Select(reference => reference.TryGetTarget(out var view) ? view : null)
                .OfType<AgentChatViewModel>().ToArray();
            views.Clear();
        }
        ChatViews.Remove(sessionService);
        foreach (var view in owned) view.Dispose();
        // These mixed in-process fixtures own the backing SQLite directory too.
        // Cancellation is not drainage: wait for their actual App task scopes
        // before deleting that directory, without relaxing fault observation.
        Task.WhenAll(owned.Select(view => ((IAsyncDisposable)typeof(AgentChatViewModel)
            .GetField("_backgroundTasks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(view)!).DisposeAsync().AsTask())).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }

    internal static AgentChatView ChatView(object profileService, object workspaceService, object sessionService,
        object permissionService, object runCoordinator, AgentChatSelectionStateService selectionState,
        AgentToolPresentationService toolPresentationService, object warmupService, IPackageShellViewService shellViewService,
        object attachmentService, IPackageNotificationService notificationService, IPackageContext? packageContext = null)
        => packageContext is null
            ? new(AgentRoleBoundary.As<IAgentProfileGateway>(profileService), AgentRoleBoundary.As<IAgentWorkspaceGateway>(workspaceService),
                AgentRoleBoundary.As<IAgentSessionGateway>(sessionService), AgentRoleBoundary.As<IAgentPermissionGateway>(permissionService),
                AgentRoleBoundary.As<IAgentRunGateway>(runCoordinator), selectionState, toolPresentationService,
                AgentRoleBoundary.As<IAgentExecutionGateway>(warmupService), shellViewService,
                AgentRoleBoundary.As<IAgentAttachmentGateway>(attachmentService), notificationService)
            : new(AgentRoleBoundary.As<IAgentProfileGateway>(profileService), AgentRoleBoundary.As<IAgentWorkspaceGateway>(workspaceService),
                AgentRoleBoundary.As<IAgentSessionGateway>(sessionService), AgentRoleBoundary.As<IAgentPermissionGateway>(permissionService),
                AgentRoleBoundary.As<IAgentRunGateway>(runCoordinator), selectionState, toolPresentationService,
                AgentRoleBoundary.As<IAgentExecutionGateway>(warmupService), shellViewService,
                AgentRoleBoundary.As<IAgentAttachmentGateway>(attachmentService), notificationService, packageContext);

    internal static AgentProfilesView ProfilesView(object profileService)
        => new(AgentRoleBoundary.As<AgentServices.IAgentProfileService>(profileService), AgentRoleBoundary.As<IAgentProfileGateway>(profileService));

    internal static AgentWorkspacesView WorkspacesView(object workspaceService, object executionGateway,
        RegressionTestExtensionCatalog catalog, IPackageSettingsNavigationService? settingsNavigationService = null,
        AgentWorkspacesViewContext? context = null)
        => new(AgentRoleBoundary.As<IAgentWorkspaceGateway>(workspaceService), AgentRoleBoundary.As<IAgentExecutionGateway>(executionGateway),
            catalog.WorkspaceEditors, settingsNavigationService, context);

    internal static AgentChatViewModel Chat(object profileService, object workspaceService, object sessionService,
        object permissionService, object runCoordinator, AgentChatSelectionStateService? selectionState = null,
        AgentToolPresentationService? toolPresentationService = null, TimeSpan? activityQuietDelay = null,
        object? warmupService = null, IPackageShellViewService? shellViewService = null, object? attachmentService = null)
        => TrackChat(sessionService, new(AgentRoleBoundary.As<IAgentProfileGateway>(profileService), AgentRoleBoundary.As<IAgentWorkspaceGateway>(workspaceService),
            AgentRoleBoundary.As<IAgentSessionGateway>(sessionService), AgentRoleBoundary.As<IAgentPermissionGateway>(permissionService),
            AgentRoleBoundary.As<IAgentRunGateway>(runCoordinator), selectionState, toolPresentationService, activityQuietDelay,
            AsOptional<IAgentExecutionGateway>(warmupService), shellViewService, AsOptional<IAgentAttachmentGateway>(attachmentService)));

    internal static AgentChatViewModel Chat(object profileService, object workspaceService, object sessionService,
        object permissionService, object runCoordinator, IPresentationDispatcher uiDispatcher, AgentChatSelectionStateService? selectionState = null,
        AgentToolPresentationService? toolPresentationService = null, TimeSpan? activityQuietDelay = null,
        object? warmupService = null, IPackageShellViewService? shellViewService = null, object? attachmentService = null,
        PresentationOperationObserver? operationObserver = null)
        => TrackChat(sessionService, new(AgentRoleBoundary.As<IAgentProfileGateway>(profileService), AgentRoleBoundary.As<IAgentWorkspaceGateway>(workspaceService),
            AgentRoleBoundary.As<IAgentSessionGateway>(sessionService), AgentRoleBoundary.As<IAgentPermissionGateway>(permissionService),
            AgentRoleBoundary.As<IAgentRunGateway>(runCoordinator), uiDispatcher, selectionState, toolPresentationService, activityQuietDelay,
            AsOptional<IAgentExecutionGateway>(warmupService), shellViewService, AsOptional<IAgentAttachmentGateway>(attachmentService), operationObserver));

    internal static AgentProfilesViewModel Profiles(object profileService, IPackageSettingsNavigationService? settingsNavigationService = null)
        => new(AgentRoleBoundary.As<AgentServices.IAgentProfileService>(profileService), AgentRoleBoundary.As<IAgentProfileGateway>(profileService), settingsNavigationService);
    internal static AgentProfilesViewModel Profiles(object profileService, IPackageSettingsNavigationService? settingsNavigationService,
        IPresentationDispatcher uiDispatcher, TimeProvider? runtimeNoticeTimeProvider = null)
        => new(AgentRoleBoundary.As<AgentServices.IAgentProfileService>(profileService), AgentRoleBoundary.As<IAgentProfileGateway>(profileService), settingsNavigationService, uiDispatcher, runtimeNoticeTimeProvider);

    private static T? AsOptional<T>(object? value) where T : class => value is null ? null : AgentRoleBoundary.As<T>(value);
}
