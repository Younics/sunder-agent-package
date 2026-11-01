extern alias AgentApp;
extern alias McpApp;
extern alias McpWorker;
extern alias MemoryApp;
extern alias SkillsWorker;

using AgentApp::Sunder.Package.Agent.PackageViews;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using McpWorker::Sunder.Package.Agent.Mcp.Services;
using McpWorker::Sunder.Package.Agent.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using SkillsWorker::Sunder.Package.Agent.Skills.Services;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Xunit;
using AgentMcpSettingsView = McpApp::Sunder.Package.Agent.Mcp.AgentMcpSettingsView;
using AgentMcpSettingsViewModel = McpApp::Sunder.Package.Agent.Mcp.AgentMcpSettingsViewModel;
using MemoryAppGateway = MemoryApp::Sunder.Package.Agent.Memory.Semantic.Runtime.IMemoryInspectorGateway;
using MemoryInspectorView = MemoryApp::Sunder.Package.Agent.Memory.Semantic.PackageViews.MemoryInspectorView;
using MemoryInspectorViewModel = MemoryApp::Sunder.Package.Agent.Memory.Semantic.PackageViews.MemoryInspectorViewModel;
using static Sunder.Package.Agent.Presentation.Tests.RenderedExtensionFixture;
using static Sunder.Package.Agent.Presentation.Tests.RenderedViewComposition;
using static Sunder.Package.Agent.Presentation.Tests.RenderedViewWaits;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class PackageViewDisposalTests
{
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
    public async Task ProfilesView_DisposeStopsOwnedViewModelFromReloading()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        await profileService.CreateProfileAsync("Initial profile");
        var view = RegressionTestAppViews.ProfilesView(profileService);
        var viewModel = Assert.IsType<AgentProfilesViewModel>(view.DataContext);
        await view.OnNavigatedToAsync(new PackageViewNavigationContext(
            "younics.ai.agent.profiles",
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
        var view = RegressionTestAppViews.WorkspacesView(
            services.WorkspaceService,
            services.TargetService,
            services.ExtensionCatalog);
        var viewModel = Assert.IsType<AgentWorkspacesViewModel>(view.DataContext);
        await view.OnNavigatedToAsync(new PackageViewNavigationContext(
            "younics.ai.agent.workspaces",
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
        services.ExtensionCatalog.AddRuntimeCatalog(
            new AgentRuntimeCatalog(services.SessionService, profileService, services.WorkspaceService));
        var embeddingProvider = new BlockingEmbeddingProvider();
        services.ExtensionCatalog.AddProvider(RegressionTestBindings.EmbeddingProviders, embeddingProvider);
        var inspector = CreateMemoryInspector(scope.Context, services.ExtensionCatalog);
        var viewModel = (MemoryInspectorViewModel)Activator.CreateInstance(
            typeof(MemoryInspectorViewModel),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            [AgentRoleBoundary.As<MemoryAppGateway>(inspector)],
            culture: null)!;
        var view = new MemoryInspectorView(viewModel);
        var navigation = view.OnNavigatedToAsync(new PackageViewNavigationContext(
            "younics.ai.agent.memory.semantic.inspector",
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
        await using var runtime = McpManagementGatewayFixture.Create(scope.Context, catalog, connections);
        var viewModel = new AgentMcpSettingsViewModel(runtime.Gateway);
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
        await using var runtime = SkillManagementGatewayFixture.Create(scope.Context, store, importer);
        var viewModel = new SkillSettingsViewModel(runtime.Gateway);
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
        using var catalog = new RegressionTestExtensionCatalog();
        using var gateway = new WorkerBackedSubagentManagementGateway(service, catalog);
        var viewModel = new SubagentsViewModel(gateway);
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
        services.ExtensionCatalog.AddRuntimeCatalog(
            new AgentRuntimeCatalog(services.SessionService, profileService, services.WorkspaceService));
        using var runtimeAdapter = new CatalogBackedSubsessionRuntimeAdapter(scope.Context, services.ExtensionCatalog);
        var viewModel = runtimeAdapter.CreateViewModel();
        await viewModel.InitializeAsync();
        var view = new SubsessionsView(viewModel);
        var subsessionCount = viewModel.Subsessions.Count;

        view.Dispose();
        view.Dispose();
        services.SessionService.CreateSession("Created after disposal", parentSessionId: parent.SessionId);

        Assert.Null(view.DataContext);
        Assert.Equal(subsessionCount, viewModel.Subsessions.Count);
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
}
