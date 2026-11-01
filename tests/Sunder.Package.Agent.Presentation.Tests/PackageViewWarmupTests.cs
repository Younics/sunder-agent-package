extern alias AgentApp;
extern alias AnthropicApp;
extern alias BuilderApp;
extern alias DockerApp;
extern alias GeminiApp;
extern alias LMStudioApp;
extern alias LocalApp;
extern alias McpApp;
extern alias MemoryApp;
extern alias OpenAiApp;

using AgentApp::Sunder.Package.Agent.PackageViews;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using BuilderApp::Sunder.Package.Agent.Builder;
using DockerApp::Sunder.Package.Agent.Execution.Docker;
using LocalApp::Sunder.Package.Agent.Execution.Local;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Rpc;
using Xunit;
using AgentMcpSettingsView = McpApp::Sunder.Package.Agent.Mcp.AgentMcpSettingsView;
using AnthropicSettingsView = AnthropicApp::Sunder.Package.Agent.Provider.Anthropic.AnthropicSettingsView;
using AppRuntime = AgentApp::Sunder.Package.Agent.Runtime;
using GeminiSettingsView = GeminiApp::Sunder.Package.Agent.Provider.Gemini.GeminiSettingsView;
using IAgentOwnerRuntimeProvider = AgentApp::Sunder.Generated.Packages.Younics_Ai_Agent_Services.IAgentRuntimeService;
using LMStudioSettingsView = LMStudioApp::Sunder.Package.Agent.Provider.LMStudio.LMStudioSettingsView;
using MemoryAppGateway = MemoryApp::Sunder.Package.Agent.Memory.Semantic.Runtime.IMemoryInspectorGateway;
using MemoryAppPackageModule = MemoryApp::Sunder.Generated.Package.AppModule;
using MemoryInspectorView = MemoryApp::Sunder.Package.Agent.Memory.Semantic.PackageViews.MemoryInspectorView;
using MemoryInspectorViewModel = MemoryApp::Sunder.Package.Agent.Memory.Semantic.PackageViews.MemoryInspectorViewModel;
using OpenAiSettingsView = OpenAiApp::Sunder.Package.Agent.Provider.OpenAI.OpenAiSettingsView;
using static Sunder.Package.Agent.Presentation.Tests.RenderedBuilderFixture;
using static Sunder.Package.Agent.Presentation.Tests.RenderedExtensionFixture;
using static Sunder.Package.Agent.Presentation.Tests.RenderedShellFixture;
using static Sunder.Package.Agent.Presentation.Tests.RenderedTranscriptGeometry;
using static Sunder.Package.Agent.Presentation.Tests.RenderedViewComposition;
using static Sunder.Package.Agent.Presentation.Tests.RenderedWorkspaceFixture;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class PackageViewWarmupTests
{
    [AvaloniaFact]
    public void PresentationViews_ConstructWithCompiledXaml()
    {
        Control[] views =
        [
            new SkillSettingsView(),
            new AgentMcpSettingsView(),
            new BuilderView(),
            new BuilderSettingsView(),
            new AgentChatView(),
            new AgentProfilesView(),
            new AgentWorkspacesView(),
            new AgentPermissionsView(),
            new AgentHistorySearchView(),
            new SubagentsView(),
            new SubsessionsView(),
            new LocalExecutionSettingsView(),
            new DockerExecutionSettingsView(),
            new MemoryInspectorView(),
            new OpenAiSettingsView(),
            new AnthropicSettingsView(),
            new GeminiSettingsView(),
            new LMStudioSettingsView(),
        ];

        foreach (var view in views.OfType<IDisposable>())
        {
            view.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task RuntimeBackedViews_ConstructWithoutWaitingForBlockedRuntime()
    {
        using var scope = RegressionTestPackageScope.Create();
        var runtime = new BlockingRuntimeClient();
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(scope.Context);
        services.AddSingleton<IAgentOwnerRuntimeProvider>(runtime);
        services.AddSingleton(new AppRuntime.AgentAppRuntimeGateway(runtime));
        services.AddSingleton<ISunderRpcClient>(new ThrowingRpcClient());
        services.AddSingleton<IPackageShellViewService, NoOpPackageShellViewService>();
        services.AddSingleton<IPackageNotificationService>(NullPackageNotificationService.Instance);
        services.AddSingleton<IBackgroundTaskQueue, NoOpBackgroundTaskQueue>();
        services.AddSingleton<IPackageSettingsNavigationService>(NullPackageSettingsNavigationService.Instance);
        services.AddSingleton<IPackageDevelopmentService>(new ColdBuilderServices());
        services.AddSingleton<IPackageLogNavigationService>(new ColdBuilderServices());
        services.AddSingleton<IPackageAppStateUpdates>(new ColdBuilderServices());
        services.AddSingleton<IPackageWorkerExecutionObservations>(new ColdBuilderServices());
        new AgentApp::Sunder.Generated.Package.AppModule().ConfigureAppServices(services, scope.Context);
        new MemoryAppPackageModule().ConfigureAppServices(services, scope.Context);
        new SubagentsAppPackageModule().ConfigureAppServices(services, scope.Context);
        new BuilderApp::Sunder.Generated.Package.AppModule().ConfigureAppServices(services, scope.Context);
        await using var provider = services.BuildServiceProvider();

        var chat = provider.GetRequiredService<AgentChatView>();
        var profiles = provider.GetRequiredService<AgentProfilesView>();
        var workspaces = provider.GetRequiredService<AgentWorkspacesView>();
        var subagents = provider.GetRequiredService<SubagentsView>();
        var subsessions = provider.GetRequiredService<SubsessionsView>();
        var memory = provider.GetRequiredService<MemoryInspectorView>();
        var builder = provider.GetRequiredService<BuilderView>();
        var builderViewModel = Assert.IsType<BuilderViewModel>(builder.DataContext);

        Assert.Equal(0, runtime.InvocationCount);
        Assert.Equal(0, runtime.SubscriptionCount);
        chat.Dispose();
        profiles.Dispose();
        workspaces.Dispose();
        subagents.Dispose();
        subsessions.Dispose();
        memory.Dispose();
        builder.Dispose();
        var replacementBuilder = provider.GetRequiredService<BuilderView>();
        Assert.Same(builderViewModel, replacementBuilder.DataContext);
        Assert.False(GetPrivateBoolean(builderViewModel, "_disposed"));
        replacementBuilder.Dispose();
    }

    [AvaloniaFact]
    public async Task ParameterFreeViews_WarmupAndRepeatNavigationApplyOneSnapshot()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        await profileService.CreateProfileAsync("Warmup profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Warmup workspace");
        var subagentService = new SubagentService(new SubagentStore(scope.Context));
        subagentService.CreateSubagent("Warmup subagent");
        var parent = services.SessionService.CreateSession(
            "Warmup parent",
            workspaceId: workspace.WorkspaceId);
        var firstChild = services.SessionService.CreateSession("First child", parentSessionId: parent.SessionId);
        var secondChild = services.SessionService.CreateSession("Second child", parentSessionId: parent.SessionId);
        services.ExtensionCatalog.AddRuntimeCatalog(
            new AgentRuntimeCatalog(services.SessionService, profileService, services.WorkspaceService));
        using var subagentGateway = new WorkerBackedSubagentManagementGateway(
            subagentService,
            services.ExtensionCatalog);
        using var subsessionRuntime = new CatalogBackedSubsessionRuntimeAdapter(subagentGateway);

        using var profiles = RegressionTestAppViews.ProfilesView(profileService);
        using var workspaces = RegressionTestAppViews.WorkspacesView(
            services.WorkspaceService,
            services.TargetService,
            services.ExtensionCatalog);
        using var subagents = new SubagentsView(new SubagentsViewModel(subagentGateway));
        using var subsessions = new SubsessionsView(subsessionRuntime.CreateViewModel());
        var inspector = CreateMemoryInspector(scope.Context, services.ExtensionCatalog);
        var memoryViewModel = (MemoryInspectorViewModel)Activator.CreateInstance(
            typeof(MemoryInspectorViewModel),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            [AgentRoleBoundary.As<MemoryAppGateway>(inspector)],
            culture: null)!;
        using var memory = new MemoryInspectorView(memoryViewModel);

        Assert.Empty(Assert.IsType<AgentProfilesViewModel>(profiles.DataContext).Profiles);
        Assert.Empty(Assert.IsType<AgentWorkspacesViewModel>(workspaces.DataContext).Workspaces);
        Assert.Empty(Assert.IsType<SubagentsViewModel>(subagents.DataContext).Subagents);
        Assert.Empty(Assert.IsType<SubsessionsViewModel>(subsessions.DataContext).Subsessions);
        Assert.Empty(memoryViewModel.Sessions);

        await Assert.IsAssignableFrom<IPackageViewWarmupTarget>(profiles).WarmupAsync();
        await Assert.IsAssignableFrom<IPackageViewWarmupTarget>(workspaces).WarmupAsync();
        await Assert.IsAssignableFrom<IPackageViewWarmupTarget>(subagents).WarmupAsync();
        await Assert.IsAssignableFrom<IPackageViewWarmupTarget>(subsessions).WarmupAsync();
        await Assert.IsAssignableFrom<IPackageViewWarmupTarget>(memory).WarmupAsync();

        var profilesViewModel = Assert.IsType<AgentProfilesViewModel>(profiles.DataContext);
        var workspacesViewModel = Assert.IsType<AgentWorkspacesViewModel>(workspaces.DataContext);
        var subagentsViewModel = Assert.IsType<SubagentsViewModel>(subagents.DataContext);
        var subsessionViewModel = Assert.IsType<SubsessionsViewModel>(subsessions.DataContext);
        var profileSnapshot = profilesViewModel.Profiles.ToArray();
        var workspaceSnapshot = workspacesViewModel.Workspaces.ToArray();
        var subagentSnapshot = subagentsViewModel.Subagents.ToArray();
        var subsessionSnapshot = subsessionViewModel.Subsessions.ToArray();
        var memorySnapshot = memoryViewModel.Sessions.ToArray();
        var profileChanges = 0;
        var workspaceChanges = 0;
        var subagentChanges = 0;
        var subsessionChanges = 0;
        var memoryChanges = 0;
        profilesViewModel.Profiles.CollectionChanged += (_, _) => profileChanges++;
        workspacesViewModel.Workspaces.CollectionChanged += (_, _) => workspaceChanges++;
        subagentsViewModel.Subagents.CollectionChanged += (_, _) => subagentChanges++;
        subsessionViewModel.Subsessions.CollectionChanged += (_, _) => subsessionChanges++;
        memoryViewModel.Sessions.CollectionChanged += (_, _) => memoryChanges++;
        var emptyContext = new PackageViewNavigationContext("test", new Dictionary<string, string?>());

        await profiles.OnNavigatedToAsync(emptyContext);
        await profiles.OnNavigatedToAsync(emptyContext);
        await workspaces.OnNavigatedToAsync(emptyContext);
        await workspaces.OnNavigatedToAsync(emptyContext);
        await subagents.OnNavigatedToAsync(emptyContext);
        await subagents.OnNavigatedToAsync(emptyContext);
        await memory.OnNavigatedToAsync(emptyContext);
        await memory.OnNavigatedToAsync(emptyContext);

        Assert.Equal(profileSnapshot, profilesViewModel.Profiles);
        Assert.Equal(workspaceSnapshot, workspacesViewModel.Workspaces);
        Assert.Equal(subagentSnapshot, subagentsViewModel.Subagents);
        Assert.Equal(memorySnapshot, memoryViewModel.Sessions);
        Assert.Equal(0, profileChanges);
        Assert.Equal(0, workspaceChanges);
        Assert.Equal(0, subagentChanges);
        Assert.Equal(0, memoryChanges);

        var targetContext = new PackageViewNavigationContext(
            SubagentConstants.SubsessionsViewId,
            new Dictionary<string, string?>
            {
                [SubagentConstants.SubsessionNavigationSessionIdKey] = secondChild.SessionId.ToString("D"),
            });
        await subsessions.OnNavigatedToAsync(targetContext);
        var selectedSubsession = subsessionViewModel.SelectedSubsession;
        await subsessions.OnNavigatedToAsync(targetContext);

        Assert.Equal(subsessionSnapshot, subsessionViewModel.Subsessions);
        Assert.Contains(subsessionSnapshot, item => item.SessionId == firstChild.SessionId);
        Assert.Contains(subsessionSnapshot, item => item.SessionId == secondChild.SessionId);
        Assert.Equal(secondChild.SessionId, selectedSubsession?.SessionId);
        Assert.Same(selectedSubsession, subsessionViewModel.SelectedSubsession);
        Assert.Equal(0, subsessionChanges);
    }

    [AvaloniaFact]
    public async Task ParameterFreeWorkspaceView_RetriesTransientWarmupFailure()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        services.WorkspaceService.CreateWorkspace("Retry workspace");
        var workspaceGateway = new FailOnceWorkspaceGateway(services.WorkspaceService);
        using var view = RegressionTestAppViews.WorkspacesView(
            workspaceGateway,
            new AgentExecutionTargetWarmupService(
                services.WorkspaceService,
                services.TargetService),
            services.ExtensionCatalog);

        var firstWarmup = view.WarmupAsync().AsTask();
        await workspaceGateway.FirstInitializationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        workspaceGateway.FailFirstInitialization.TrySetResult();
        await firstWarmup;
        await view.WarmupAsync();

        Assert.Equal(2, workspaceGateway.InitializeCount);
        var viewModel = Assert.IsType<AgentWorkspacesViewModel>(view.DataContext);
        Assert.Single(viewModel.Workspaces);
        Assert.DoesNotContain("unavailable", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class BlockingRuntimeClient : OwnerServiceStub
    {
        private int _invocationCount;
        private int _subscriptionCount;

        public int InvocationCount => Volatile.Read(ref _invocationCount);
        public int SubscriptionCount => Volatile.Read(ref _subscriptionCount);

        public override ValueTask<TResponse> RespondAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            Interlocked.Increment(ref _invocationCount);
            return new ValueTask<TResponse>(WaitForCancellationAsync<TResponse>(cancellationToken));
        }

        public override async IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(
            TRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            Interlocked.Increment(ref _subscriptionCount);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        private static async Task<T> WaitForCancellationAsync<T>(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Cancellation was expected.");
        }
    }
}
