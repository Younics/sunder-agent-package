using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
using Sunder.Package.Agent.PackageViews;
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
        var view = new AgentProfilesView(profileService);
        var viewModel = Assert.IsType<AgentProfilesViewModel>(view.DataContext);
        await WaitUntilAsync(() => viewModel.Profiles.Count > 0 && !viewModel.IsBusy);
        var profileCount = viewModel.Profiles.Count;

        view.Dispose();
        await profileService.CreateProfileAsync("Created after disposal");
        await Task.Delay(50);

        Assert.Null(view.DataContext);
        Assert.Equal(profileCount, viewModel.Profiles.Count);
    }

    [AvaloniaFact]
    public void WorkspacesView_DisposeStopsOwnedViewModelFromReloading()
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
        var viewModel = new MemoryInspectorViewModel(inspector);
        var view = new MemoryInspectorView(viewModel);
        await embeddingProvider.ReadinessStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        view.Dispose();
        await embeddingProvider.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

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
            scope.Context.Storage.LocalWorkspace.GetLocalPath("skills/skills.json"),
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
            scope.Context.Storage.LocalWorkspace.GetLocalPath("subagents/subagents.json"),
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
    }
}
