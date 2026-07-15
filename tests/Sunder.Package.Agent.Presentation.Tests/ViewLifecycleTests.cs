using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Builder;
using Sunder.Package.Agent.Execution.Docker;
using Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.Mcp;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Package.Agent.Memory.Semantic;
using Sunder.Package.Agent.Memory.Semantic.PackageViews;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Package.Agent.Provider.Anthropic;
using Sunder.Package.Agent.Provider.Gemini;
using Sunder.Package.Agent.Provider.LMStudio;
using Sunder.Package.Agent.Provider.OpenAI;
using Sunder.Package.Agent.Skills.PackageViews;
using Sunder.Package.Agent.Skills.Services;
using Sunder.Package.Agent.Subagents.PackageViews;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Runtime;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class ViewLifecycleTests
{
    [AvaloniaFact]
    public void PresentationViews_ConstructWithCompiledXaml()
    {
        Control[] views =
        [
            new SkillSettingsView(),
            new AgentMcpSettingsView(),
            new BuilderView(),
            new AgentChatView(),
            new AgentProfilesView(),
            new AgentWorkspacesView(),
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
        services.AddSingleton<IPackageRuntimeClient>(runtime);
        services.AddSingleton<IPackageExtensionCatalog>(new RegressionTestExtensionCatalog());
        new Sunder.Package.Agent.AppPackageModule().ConfigureAppServices(services, scope.Context);
        new Sunder.Package.Agent.Memory.Semantic.AppPackageModule().ConfigureAppServices(services, scope.Context);
        await using var provider = services.BuildServiceProvider();

        var profiles = ActivatorUtilities.CreateInstance<AgentProfilesView>(provider);
        var workspaces = ActivatorUtilities.CreateInstance<AgentWorkspacesView>(provider);
        var memory = ActivatorUtilities.CreateInstance<MemoryInspectorView>(provider);

        Assert.Equal(0, runtime.InvocationCount);
        profiles.Dispose();
        workspaces.Dispose();
        memory.Dispose();
    }

    [AvaloniaFact]
    public void DisposableView_ActivatesAndReleasesOwnedSubscriptions()
    {
        using var view = new SkillSettingsView();
        var window = new Window { Content = view };

        window.Show();
        window.Close();
    }

    [AvaloniaFact]
    public void TranscriptViews_DisposeIdempotentlyAfterVisualAttachment()
    {
        var chat = new AgentChatView();
        var subsessions = new SubsessionsView();
        var window = new Window
        {
            Content = new StackPanel { Children = { chat, subsessions } },
        };

        window.Show();
        window.Close();
        chat.Dispose();
        chat.Dispose();
        subsessions.Dispose();
        subsessions.Dispose();
    }

    [AvaloniaFact]
    public async Task AgentChatView_NavigationAwaitsAppliedAndPlacedInitialTranscript()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        await profileService.CreateProfileAsync("Navigation profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Navigation workspace");
        var session = services.SessionService.CreateSession(
            "Navigation session",
            workspaceId: workspace.WorkspaceId);
        services.SessionService.AppendTextTurn(
            session.SessionId,
            AgentMessageRole.Assistant,
            "Initial response.");
        var selectionState = new AgentChatSelectionStateService(scope.Context);
        var attachmentService = new AgentAttachmentService(scope.Context);
        using var view = new AgentChatView(
            profileService,
            services.WorkspaceService,
            services.SessionService,
            NoOpPermissionGateway.Instance,
            NoOpRunGateway.Instance,
            selectionState,
            new AgentToolPresentationService(),
            new AgentExecutionTargetWarmupService(
                services.WorkspaceService,
                services.TargetService),
            null!,
            attachmentService,
            NullPackageNotificationService.Instance);
        var viewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        Assert.Empty(viewModel.Profiles);
        var transcriptNotificationsOnUi = new List<bool>();
        viewModel.Messages.CollectionChanged += (_, _) =>
            transcriptNotificationsOnUi.Add(Dispatcher.UIThread.CheckAccess());

        await Task.Run(async () => await view.OnNavigatedToAsync(new PackageViewNavigationContext(
            "sunder.package.agent.chat",
            new Dictionary<string, string?>())));

        Assert.Single(viewModel.Messages);
        Assert.Equal("Initial response.", Assert.IsType<AgentTextTranscriptRowViewModel>(
            viewModel.Messages[0]).Content);
        Assert.NotEmpty(transcriptNotificationsOnUi);
        Assert.All(transcriptNotificationsOnUi, Assert.True);
        var transcript = GetTranscriptScrollViewer(view);
        Assert.Equal(1d, transcript.Opacity);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AgentChatView_WarmNavigationLoadsSnapshotExactlyOnceWithoutResetOrSettle()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        await profileService.CreateProfileAsync("Warm profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Warm workspace");
        var session = services.SessionService.CreateSession("Warm session", workspaceId: workspace.WorkspaceId);
        services.SessionService.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, "Warm response.");
        var sessionGateway = new CountingSessionGateway(services.SessionService);
        using var view = new AgentChatView(
            profileService,
            services.WorkspaceService,
            sessionGateway,
            NoOpPermissionGateway.Instance,
            NoOpRunGateway.Instance,
            new AgentChatSelectionStateService(scope.Context),
            new AgentToolPresentationService(),
            new AgentExecutionTargetWarmupService(services.WorkspaceService, services.TargetService),
            null!,
            new AgentAttachmentService(scope.Context),
            NullPackageNotificationService.Instance);
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        var context = new PackageViewNavigationContext(
            "sunder.package.agent.chat",
            new Dictionary<string, string?>());
        await Task.Run(async () => await view.OnNavigatedToAsync(context));
        var transcript = GetTranscriptScrollViewer(view);
        var settledOperation = GetSettledScrollOperation(view);
        var opacityChanges = 0;
        transcript.PropertyChanged += (_, change) =>
        {
            if (change.Property.Name == "Opacity")
            {
                opacityChanges++;
            }
        };

        await Task.Run(async () => await view.OnNavigatedToAsync(context));

        Assert.Equal(1, sessionGateway.RecentTranscriptReadCount);
        Assert.Equal(0, opacityChanges);
        Assert.Same(settledOperation, GetSettledScrollOperation(view));
        Assert.Equal(1d, transcript.Opacity);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AgentChatView_NavigationCancellationStopsInitialLayoutPlacement()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        await profileService.CreateProfileAsync("Cancellation profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Cancellation workspace");
        var session = services.SessionService.CreateSession(
            "Cancellation session",
            workspaceId: workspace.WorkspaceId);
        for (var index = 0; index < 60; index++)
        {
            services.SessionService.AppendTextTurn(
                session.SessionId,
                AgentMessageRole.Assistant,
                $"Response {index}: {new string('x', 200)}");
        }

        using var view = new AgentChatView(
            profileService,
            services.WorkspaceService,
            services.SessionService,
            NoOpPermissionGateway.Instance,
            NoOpRunGateway.Instance,
            new AgentChatSelectionStateService(scope.Context),
            new AgentToolPresentationService(),
            new AgentExecutionTargetWarmupService(services.WorkspaceService, services.TargetService),
            null!,
            new AgentAttachmentService(scope.Context),
            NullPackageNotificationService.Instance);
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        var transcript = GetTranscriptScrollViewer(view);
        using var cancellation = new CancellationTokenSource();
        var placementStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationTriggered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var layoutUpdates = 0;
        EventHandler? layoutHandler = null;
        layoutHandler = (_, _) =>
        {
            layoutUpdates++;
            cancellationTriggered.TrySetResult();
        };
        transcript.PropertyChanged += (_, change) =>
        {
            if (change.Property.Name == "Opacity" && transcript.Opacity == 0)
            {
                transcript.LayoutUpdated += layoutHandler;
                placementStarted.TrySetResult();
            }
        };
        var context = new PackageViewNavigationContext(
            "sunder.package.agent.chat",
            new Dictionary<string, string?>());

        var cancelOnLayout = Task.Run(async () =>
        {
            await cancellationTriggered.Task;
            cancellation.Cancel();
        });
        var navigation = Task.Run(async () => await view.OnNavigatedToAsync(context, cancellation.Token));
        await placementStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await cancellationTriggered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await cancelOnLayout;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => navigation);
        await GetSettledScrollOperation(view).WaitAsync(TimeSpan.FromSeconds(3));
        transcript.LayoutUpdated -= layoutHandler;
        var updatesAfterCancellation = layoutUpdates;
        await Task.Delay(50);

        Assert.Equal(updatesAfterCancellation, layoutUpdates);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AgentChatView_NavigationPresentsRetryableStartupErrorWithoutHostFault()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        var workspaceGateway = new FailOnceWorkspaceGateway(services.WorkspaceService);
        using var view = new AgentChatView(
            profileService,
            workspaceGateway,
            services.SessionService,
            NoOpPermissionGateway.Instance,
            NoOpRunGateway.Instance,
            new AgentChatSelectionStateService(scope.Context),
            new AgentToolPresentationService(),
            new AgentExecutionTargetWarmupService(
                services.WorkspaceService,
                services.TargetService),
            null!,
            new AgentAttachmentService(scope.Context),
            NullPackageNotificationService.Instance);
        var viewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        var context = new PackageViewNavigationContext(
            "sunder.package.agent.chat",
            new Dictionary<string, string?>());
        var failureNotificationsOnUi = new List<bool>();
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName is nameof(AgentChatViewModel.SetupTitle)
                or nameof(AgentChatViewModel.SetupDescription)
                or nameof(AgentChatViewModel.StatusText))
            {
                failureNotificationsOnUi.Add(Dispatcher.UIThread.CheckAccess());
            }
        };

        var firstNavigation = Task.Run(async () => await view.OnNavigatedToAsync(context));
        await workspaceGateway.FirstInitializationStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(firstNavigation.IsCompleted);
        workspaceGateway.FailFirstInitialization.TrySetResult();
        await firstNavigation;

        Assert.Equal("Unable to load Agent Chat", viewModel.SetupTitle);
        Assert.Contains("return to retry", viewModel.SetupDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Unable to load Agent Chat", viewModel.StatusText, StringComparison.Ordinal);
        Assert.NotEmpty(failureNotificationsOnUi);
        Assert.All(failureNotificationsOnUi, Assert.True);

        await Task.Run(async () => await view.OnNavigatedToAsync(context));

        Assert.Equal(2, workspaceGateway.InitializeCount);
        Assert.Equal("Create an agent before chatting", viewModel.SetupTitle);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ProfilesView_DisposeStopsOwnedViewModelFromReloading()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        await profileService.CreateProfileAsync("Initial profile");
        var view = new AgentProfilesView(profileService);
        var viewModel = Assert.IsType<AgentProfilesViewModel>(view.DataContext);
        await view.OnNavigatedToAsync(new PackageViewNavigationContext(
            "sunder.package.agent.profiles",
            new Dictionary<string, string?>()));
        await WaitUntilAsync(() => viewModel.Profiles.Count > 0 && !viewModel.IsBusy);
        var profileCount = viewModel.Profiles.Count;

        view.Dispose();
        await profileService.CreateProfileAsync("Created after disposal");
        await Task.Delay(50);

        Assert.Null(view.DataContext);
        Assert.Equal(profileCount, viewModel.Profiles.Count);
    }

    [AvaloniaFact]
    public async Task WorkspacesView_DisposeStopsOwnedViewModelFromReloading()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        services.WorkspaceService.CreateWorkspace("Initial workspace");
        var warmup = new AgentExecutionTargetWarmupService(services.WorkspaceService, services.TargetService);
        var view = new AgentWorkspacesView(
            services.WorkspaceService,
            services.TargetService,
            services.ExtensionCatalog,
            warmup);
        var viewModel = Assert.IsType<AgentWorkspacesViewModel>(view.DataContext);
        await view.OnNavigatedToAsync(new PackageViewNavigationContext(
            "sunder.package.agent.workspaces",
            new Dictionary<string, string?>()));
        var workspaceCount = viewModel.Workspaces.Count;

        view.Dispose();
        services.WorkspaceService.CreateWorkspace("Created after disposal");

        Assert.Null(view.DataContext);
        Assert.Equal(workspaceCount, viewModel.Workspaces.Count);
    }

    [AvaloniaFact]
    public async Task MemoryInspectorView_DisposeCancelsOwnedViewModelLoad()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        var profile = await profileService.CreateProfileAsync("Memory profile");
        profileService.SaveProfile(
            profile.ProfileId,
            profile.DisplayName,
            profile.Description,
            profile.Instructions,
            profile.ChatProviderId,
            profile.ChatModelId,
            "blocking-embeddings",
            "semantic-v1");
        var workspace = services.WorkspaceService.CreateWorkspace("Memory workspace");
        services.SessionService.CreateSession(
            "Memory session",
            profileId: profile.ProfileId,
            workspaceId: workspace.WorkspaceId);
        services.ExtensionCatalog.AddExtension(
            PackageExtensionPoints.RuntimeCatalogs,
            new AgentRuntimeCatalog(services.SessionService, profileService, services.WorkspaceService));
        var embeddingProvider = new BlockingEmbeddingProvider();
        services.ExtensionCatalog.AddExtension(PackageExtensionPoints.EmbeddingProviders, embeddingProvider);
        var inspector = CreateMemoryInspector(scope.Context, services.ExtensionCatalog);
        var viewModel = (MemoryInspectorViewModel)Activator.CreateInstance(
            typeof(MemoryInspectorViewModel),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            [inspector],
            culture: null)!;
        var view = new MemoryInspectorView(viewModel);
        var navigation = view.OnNavigatedToAsync(new PackageViewNavigationContext(
            "sunder.package.agent.memory.semantic.inspector",
            new Dictionary<string, string?>())).AsTask();
        await embeddingProvider.ReadinessStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        view.Dispose();
        await embeddingProvider.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => navigation);

        Assert.Null(view.DataContext);
        Assert.Equal("Loading semantic status...", viewModel.SemanticStatusText);
    }

    [AvaloniaFact]
    public async Task McpView_DisposeStopsOwnedViewModelFromReloading()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new McpServerCatalogService(scope.Context);
        await using var connections = new McpClientConnectionManager(NullLoggerFactory.Instance);
        var viewModel = new AgentMcpSettingsViewModel(
            new McpSettingsEditorService(catalog),
            new McpConfigurationCoordinator(new McpEcosystemConfigurationImporter(catalog), syncService: null),
            new McpServerConnectionService(catalog, connections, oauthService: null),
            new McpOAuthCoordinator(oauthService: null, connections));
        Assert.Same(viewModel.InitializeAsync(), viewModel.InitializeAsync());
        await viewModel.InitializeAsync();
        var view = new AgentMcpSettingsView(viewModel);

        view.Dispose();
        view.Dispose();
        await catalog.SaveServerAsync(CreateMcpServer("created-after-disposal"), EmptyValues, EmptyValues);
        await Task.Delay(50);

        Assert.Null(view.DataContext);
        Assert.Empty(viewModel.Servers);
    }

    [AvaloniaFact]
    public async Task SkillsView_DisposeStopsOwnedViewModelFromReloading()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new SkillStore(scope.Context);
        var importer = new SkillImportService(store, new NoOpGitHubSkillClient(), scope.Context);
        var viewModel = new SkillSettingsViewModel(store, importer);
        Assert.Same(viewModel.InitializeAsync(), viewModel.InitializeAsync());
        await viewModel.InitializeAsync();
        var view = new SkillSettingsView(viewModel);

        view.Dispose();
        view.Dispose();
        store.SaveSkill(CreateSkill("created-after-disposal"));

        Assert.Null(view.DataContext);
        Assert.Empty(viewModel.Skills);
    }

    [AvaloniaFact]
    public async Task SubagentsView_DisposeStopsOwnedViewModelFromReloading()
    {
        using var scope = RegressionTestPackageScope.Create();
        var service = new SubagentService(new SubagentStore(scope.Context));
        service.CreateSubagent("Initial subagent");
        var viewModel = new SubagentsViewModel(service, new RegressionTestExtensionCatalog());
        Assert.Same(viewModel.InitializeAsync(), viewModel.InitializeAsync());
        await viewModel.InitializeAsync();
        var view = new SubagentsView(viewModel);
        var subagentCount = viewModel.Subagents.Count;

        view.Dispose();
        view.Dispose();
        service.CreateSubagent("Created after disposal");

        Assert.Null(view.DataContext);
        Assert.Equal(subagentCount, viewModel.Subagents.Count);
    }

    [AvaloniaFact]
    public async Task SubsessionsView_DisposeStopsOwnedViewModelFromReloading()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        var workspace = services.WorkspaceService.CreateWorkspace("Subsession workspace");
        var parent = services.SessionService.CreateSession("Parent", workspaceId: workspace.WorkspaceId);
        services.SessionService.CreateSession("Initial child", parentSessionId: parent.SessionId);
        services.ExtensionCatalog.AddExtension(
            PackageExtensionPoints.RuntimeCatalogs,
            new AgentRuntimeCatalog(services.SessionService, profileService, services.WorkspaceService));
        var viewModel = new SubsessionsViewModel(services.ExtensionCatalog);
        Assert.Same(viewModel.InitializeAsync(), viewModel.InitializeAsync());
        await viewModel.InitializeAsync();
        var view = new SubsessionsView(viewModel);
        var subsessionCount = viewModel.Subsessions.Count;

        view.Dispose();
        view.Dispose();
        services.SessionService.CreateSession("Created after disposal", parentSessionId: parent.SessionId);

        Assert.Null(view.DataContext);
        Assert.Equal(subsessionCount, viewModel.Subsessions.Count);
    }

    [AvaloniaFact]
    public async Task SkillsInitialization_MalformedIndexIsPresented()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new SkillStore(scope.Context);
        await File.WriteAllTextAsync(
            scope.Context.Storage.RoleLocalWorkspace.GetLocalPath("skills/skills.json"),
            "{ malformed");
        var viewModel = new SkillSettingsViewModel(
            store,
            new SkillImportService(store, new NoOpGitHubSkillClient(), scope.Context));

        await viewModel.InitializeAsync();

        Assert.Equal(SkillStatusKind.Error, viewModel.StatusKind);
        Assert.Contains("malformed", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        viewModel.Dispose();
    }

    [AvaloniaFact]
    public async Task SubagentInitialization_MalformedIndexIsPresented()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new SubagentStore(scope.Context);
        await File.WriteAllTextAsync(
            scope.Context.Storage.RoleLocalWorkspace.GetLocalPath("subagents/subagents.json"),
            "{ malformed");
        var viewModel = new SubagentsViewModel(
            new SubagentService(store),
            new RegressionTestExtensionCatalog());

        await viewModel.InitializeAsync();

        Assert.Equal(SubagentStatusKind.Error, viewModel.StatusKind);
        Assert.Contains("subagent", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        viewModel.Dispose();
    }

    [AvaloniaFact]
    public async Task SubsessionInitialization_CatalogFailureIsPresented()
    {
        var viewModel = new SubsessionsViewModel(new ThrowingExtensionCatalog());

        await viewModel.InitializeAsync();

        Assert.Contains("Injected catalog failure", viewModel.StatusText, StringComparison.Ordinal);
        viewModel.Dispose();
    }

    [AvaloniaFact]
    public void SecretFieldStyle_MasksProviderApiKeyInput()
    {
        var view = new OpenAiSettingsView();
        var secretField = new TextBox();
        secretField.Classes.Add("secret-field");
        view.Content = secretField;
        var window = new Window { Content = view };

        window.Show();
        Assert.Equal('*', secretField.PasswordChar);
        window.Close();
    }

    [AvaloniaFact]
    public void OpenAiSettingsView_HasBoundAuthorizeButton()
    {
        using var scope = RegressionTestPackageScope.Create();
        var viewModel = new OpenAiSettingsViewModel(scope.Context, NullPackageRuntimeClient.Instance);
        using var view = new OpenAiSettingsView(viewModel);

        var button = view.FindControl<Button>("AuthorizeButton");

        Assert.NotNull(button);
        Assert.Same(viewModel.AuthorizeCommand, button.Command);
        Assert.Equal("Authorize with ChatGPT Plus/Pro", button.Content);
    }

    [AvaloniaFact]
    public async Task OpenAiSettingsView_InitializesPersistedRuntimeAuthStatus()
    {
        using var scope = RegressionTestPackageScope.Create();
        var runtime = new ConnectedOpenAiRuntimeClient();
        var viewModel = new OpenAiSettingsViewModel(scope.Context, runtime);
        using var view = new OpenAiSettingsView(viewModel);

        await WaitUntilAsync(() => viewModel.IsCodexConnected);

        Assert.True(runtime.InvocationCount > 0);
        Assert.Equal("Connected, active", viewModel.CodexStatusLabel);
        Assert.True(viewModel.CanDisconnectAction);
        Assert.Equal("Reauthorize with ChatGPT Plus/Pro", viewModel.AuthorizationButtonLabel);
    }

    private static AgentServices CreateAgentServices(RegressionTestPackageScope scope)
    {
        var extensionCatalog = new RegressionTestExtensionCatalog();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store, extensionCatalog);
        var workspaceService = new AgentWorkspaceService(store, extensionCatalog, sessionService);
        var targetService = new AgentExecutionTargetService(extensionCatalog);
        var toolService = new AgentToolService(
            new InstalledPackageToolSource(extensionCatalog),
            sessionService,
            workspaceService,
            targetService,
            extensionCatalog);
        return new AgentServices(
            extensionCatalog,
            sessionService,
            workspaceService,
            targetService,
            new AgentProfileService(store, toolService, extensionCatalog));
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyValues =
        new Dictionary<string, string>();

    private static ConfiguredMcpServerRecord CreateMcpServer(string id) => new()
    {
        ServerId = id,
        Name = id,
        DisplayName = id,
        IsEnabled = true,
        TransportType = ConfiguredMcpTransportType.HttpSse,
        EndpointUrl = $"https://{id}.example/mcp",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static InstalledSkillRecord CreateSkill(string id)
    {
        var now = DateTimeOffset.UtcNow;
        return new InstalledSkillRecord(
            id,
            $"skills/{id}",
            id,
            null,
            null,
            null,
            "local",
            null,
            null,
            null,
            id,
            now,
            now,
            new Dictionary<string, string>(),
            []);
    }

    private static MemoryInspectorService CreateMemoryInspector(
        IPackageContext context,
        IPackageExtensionCatalog extensionCatalog)
    {
        var store = new MemoryLocalStore(context);
        var settings = new MemorySemanticSettingsService(context);
        var metrics = new SemanticMemoryMetricsService();
        var resolver = new SemanticModelRuntimeResolver(extensionCatalog, settings);
        var retrieval = new SemanticMemoryRetrievalBackend(store, resolver, settings);
        var worker = new SemanticMemoryIndexingBackgroundService(store, resolver, settings, retrieval, metrics);
        return new MemoryInspectorService(store, retrieval, worker, resolver, metrics);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= timeout)
            {
                throw new TimeoutException("Timed out waiting for view-model state.");
            }

            await Task.Delay(10);
        }
    }

    private sealed record AgentServices(
        RegressionTestExtensionCatalog ExtensionCatalog,
        AgentSessionService SessionService,
        AgentWorkspaceService WorkspaceService,
        AgentExecutionTargetService TargetService,
        AgentProfileService ProfileService);

    private sealed class ConnectedOpenAiRuntimeClient : IPackageRuntimeClient
    {
        private readonly DateTimeOffset _expiresAtUtc = DateTimeOffset.UtcNow.AddHours(1);

        public bool IsAvailable => true;

        public int InvocationCount { get; private set; }

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("openai.auth.v1", operation.OperationId);
            InvocationCount++;
            var response = Activator.CreateInstance(
                typeof(TResponse),
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic,
                binder: null,
                args: [true, true, _expiresAtUtc, null],
                culture: null);
            return ValueTask.FromResult(Assert.IsType<TResponse>(response));
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FailOnceWorkspaceGateway(IAgentWorkspaceGateway inner)
        : IAgentWorkspaceGateway
    {
        private int _initializeCount;

        public int InitializeCount => Volatile.Read(ref _initializeCount);
        public TaskCompletionSource FirstInitializationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FailFirstInitialization { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action? WorkspacesChanged
        {
            add => inner.WorkspacesChanged += value;
            remove => inner.WorkspacesChanged -= value;
        }
        public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces() => inner.ListWorkspaces();
        public AgentWorkspaceRecord? GetWorkspace(string workspaceId) => inner.GetWorkspace(workspaceId);
        public AgentWorkspaceRecord CreateWorkspace(string displayName) => inner.CreateWorkspace(displayName);
        public void SaveWorkspace(string workspaceId, string displayName, string? description)
            => inner.SaveWorkspace(workspaceId, displayName, description);
        public void SaveWorkspaceAggregate(
            string workspaceId,
            string displayName,
            string? description,
            IReadOnlyList<AgentWorkspacePathRecord> paths,
            IReadOnlyList<AgentWorkspaceDocumentRecord> documents,
            string? executionTargetId)
            => inner.SaveWorkspaceAggregate(
                workspaceId,
                displayName,
                description,
                paths,
                documents,
                executionTargetId);
        public void DeleteWorkspace(string workspaceId) => inner.DeleteWorkspace(workspaceId);
        public IReadOnlyList<AgentWorkspaceBindingRecord> ListBindings(string workspaceId)
            => inner.ListBindings(workspaceId);
        public AgentWorkspaceBindingRecord SavePrimaryExecutionBinding(
            string workspaceId,
            string contributionId,
            string displayRole = AgentWorkspaceBindingRoles.PrimaryExecutionTarget)
            => inner.SavePrimaryExecutionBinding(workspaceId, contributionId, displayRole);
        public void RemovePrimaryExecutionBinding(string workspaceId)
            => inner.RemovePrimaryExecutionBinding(workspaceId);

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _initializeCount) == 1)
            {
                FirstInitializationStarted.TrySetResult();
                await FailFirstInitialization.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("Injected startup failure.");
            }

            await inner.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class CountingSessionGateway(IAgentSessionGateway inner) : IAgentSessionGateway
    {
        public int RecentTranscriptReadCount { get; private set; }

        public event Action<Guid>? SessionChanged
        {
            add => inner.SessionChanged += value;
            remove => inner.SessionChanged -= value;
        }

        public event Action<Guid, AgentTurnRecord>? TurnChanged
        {
            add => inner.TurnChanged += value;
            remove => inner.TurnChanged -= value;
        }

        public event Action<Guid>? TranscriptReset
        {
            add => inner.TranscriptReset += value;
            remove => inner.TranscriptReset -= value;
        }

        public event Action<Guid, AgentRunActivityUpdate>? RunActivityChanged
        {
            add => inner.RunActivityChanged += value;
            remove => inner.RunActivityChanged -= value;
        }

        public IReadOnlyList<AgentSessionRecord> ListSessions() => inner.ListSessions();
        public IReadOnlyList<AgentSessionRecord> ListSessionsForWorkspace(string workspaceId)
            => inner.ListSessionsForWorkspace(workspaceId);
        public AgentSessionRecord CreateSession(string title, Guid? parentSessionId = null, Guid? rootSessionId = null,
            Guid? parentRunId = null, long? parentRunRevision = null, string? parentToolCallId = null,
            string? taskId = null, string? profileId = null, string? behaviorLoopId = null,
            string? agentKind = null, string? workspaceId = null)
            => inner.CreateSession(title, parentSessionId, rootSessionId, parentRunId, parentRunRevision,
                parentToolCallId, taskId, profileId, behaviorLoopId, agentKind, workspaceId);
        public AgentSessionRecord? GetSession(Guid sessionId) => inner.GetSession(sessionId);
        public void UpdateSession(AgentSessionRecord session) => inner.UpdateSession(session);
        public void DeleteSession(Guid sessionId) => inner.DeleteSession(sessionId);
        public IReadOnlyList<AgentTurnRecord> ListTurns(Guid sessionId) => inner.ListTurns(sessionId);
        public IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit)
        {
            RecentTranscriptReadCount++;
            return inner.ListRecentTurns(sessionId, limit);
        }
        public IReadOnlyList<AgentTurnRecord> ListTurnsBefore(
            Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit)
            => inner.ListTurnsBefore(sessionId, beforeCreatedAtUtc, beforeTurnId, limit);
        public IReadOnlyList<AgentTurnRecord> ListTurnsAfter(
            Guid sessionId, DateTimeOffset afterCreatedAtUtc, Guid afterTurnId, int limit)
            => inner.ListTurnsAfter(sessionId, afterCreatedAtUtc, afterTurnId, limit);
        public AgentTurnRecord? GetTurn(Guid turnId) => inner.GetTurn(turnId);
        public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId) => inner.GetLatestCheckpoint(sessionId);
    }

    private sealed class BlockingRuntimeClient : IPackageRuntimeClient
    {
        private int _invocationCount;

        public bool IsAvailable => true;
        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            Interlocked.Increment(ref _invocationCount);
            return new ValueTask<TResponse>(WaitForCancellationAsync<TResponse>(cancellationToken));
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        private static async Task<T> WaitForCancellationAsync<T>(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Cancellation was expected.");
        }
    }

    private static Task GetSettledScrollOperation(AgentChatView view)
    {
        var behavior = typeof(AgentChatView)
            .GetField("_transcriptBehavior", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(view)!;
        var coordinator = behavior.GetType()
            .GetField("_scrollCoordinator", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(behavior)!;
        return Assert.IsAssignableFrom<Task>(coordinator.GetType()
            .GetField("_settledScrollOperation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator));
    }

    private static ScrollViewer GetTranscriptScrollViewer(AgentChatView view)
        => Assert.IsType<ScrollViewer>(view.FindControl<ScrollViewer>("TranscriptScrollViewer"));

    private sealed class NoOpPermissionGateway : IAgentPermissionGateway
    {
        public static NoOpPermissionGateway Instance { get; } = new();
        public AgentSessionPermissionState GetSessionState(Guid sessionId) => new(sessionId, false);
        public void SetSessionUnrestrictedMode(Guid sessionId, bool isEnabled) { }
        public IReadOnlyList<AgentPermissionActionDescriptor> ListActions() => [];
        public IReadOnlyList<AgentPermissionOverride> ListOverrides() => [];
        public void SaveOverride(string actionId, string boundaryId, AgentPermissionDecision decision) { }
        public void DeleteOverride(string actionId, string boundaryId) { }
        public IReadOnlyList<AgentPendingPermissionRequestRecord> ListPendingRequestsForSessionTree(Guid sessionId) => [];
        public void SaveSessionApproval(Guid sessionId, string actionId, string boundaryId) { }
    }

    private sealed class NoOpRunGateway : IAgentRunGateway
    {
        public static NoOpRunGateway Instance { get; } = new();
        public Task<AgentRunCheckpointRecord> QueueUserMessageAsync(Guid sessionId, string profileId,
            string userMessage, string workspaceId, IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentRunCheckpointRecord> RollbackAndQueueUserMessageAsync(Guid sessionId,
            Guid rollbackAnchorTurnId, string profileId, string userMessage, string workspaceId,
            IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentRunCheckpointRecord?> StopAsync(Guid sessionId,
            CancellationToken cancellationToken = default) => Task.FromResult<AgentRunCheckpointRecord?>(null);
        public Task<AgentRunCheckpointRecord?> ApprovePendingPermissionAsync(Guid sessionId, string requestId,
            CancellationToken cancellationToken = default) => Task.FromResult<AgentRunCheckpointRecord?>(null);
        public Task<AgentRunCheckpointRecord?> DenyPendingPermissionAsync(Guid sessionId, string requestId,
            CancellationToken cancellationToken = default) => Task.FromResult<AgentRunCheckpointRecord?>(null);
    }

    private sealed class BlockingEmbeddingProvider : IAgentEmbeddingProvider
    {
        public AgentEmbeddingProviderDescriptor Descriptor { get; } = new(
            "blocking-embeddings",
            "Blocking Embeddings",
            []);

        public TaskCompletionSource ReadinessStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> GetAvailableModelsAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentEmbeddingModelDescriptor>>([]);

        public async ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(
            CancellationToken cancellationToken = default)
        {
            ReadinessStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }

            return new AgentEmbeddingProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.Ready,
                "Ready.");
        }

        public ValueTask<AgentEmbeddingGenerationResult?> GenerateEmbeddingAsync(
            string modelId,
            string text,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentEmbeddingGenerationResult?>(null);

        public ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateEmbeddingsAsync(
            string modelId,
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentEmbeddingGenerationResult?>>([]);
    }

    private sealed class NoOpGitHubSkillClient : IGitHubSkillClient
    {
        public Task<string?> TryGetDefaultBranchAsync(
            string owner,
            string repo,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<GitHubSkillFolder?> TryGetFolderAsync(
            GitHubSkillFolderRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<GitHubSkillFolder?>(null);

        public Task<GitHubSkillFolder?> TryGetSkillFolderAsync(
            GitHubSkillFolderRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<GitHubSkillFolder?>(null);

        public Task<IReadOnlyList<GitHubSkillFile>> ListFilesAsync(
            GitHubSkillFolder folder,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GitHubSkillFile>>([]);

        public Task<byte[]> ReadFileAsync(
            GitHubSkillFolder folder,
            GitHubSkillFile file,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<byte>());
    }

    private sealed class ThrowingExtensionCatalog : IPackageExtensionCatalog
    {
        public IReadOnlyList<TContract> GetExtensions<TContract>(
            PackageExtensionPoint<TContract> extensionPoint)
            => throw new InvalidOperationException("Injected catalog failure.");

        public IReadOnlyList<PackageExtensionContribution<TContract>> GetExtensionContributions<TContract>(
            PackageExtensionPoint<TContract> extensionPoint)
            => throw new InvalidOperationException("Injected catalog failure.");
    }
}
