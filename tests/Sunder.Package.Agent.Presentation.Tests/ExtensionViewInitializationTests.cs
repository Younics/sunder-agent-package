extern alias AgentApp;
extern alias SkillsWorker;

using AgentApp::Sunder.Package.Agent.PackageViews;
using Avalonia.Headless.XUnit;
using SkillsWorker::Sunder.Package.Agent.Skills.Services;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Xunit;
using AppRuntime = AgentApp::Sunder.Package.Agent.Runtime;
using static Sunder.Package.Agent.Presentation.Tests.RenderedExtensionFixture;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class ExtensionViewInitializationTests
{
    [AvaloniaFact]
    public async Task AgentPermissionsViewModel_EmptySuccessfulRetryClearsUnavailableStatus()
    {
        var gateway = new TogglePermissionGateway();
        using var viewModel = new AgentPermissionsViewModel(AgentRoleBoundary.As<AppRuntime.IAgentPermissionGateway>(gateway));
        await viewModel.PrepareNavigationAsync(new PackageViewNavigationContext(
            "settings:younics.ai.agent.permissions",
            new Dictionary<string, string?>()));
        Assert.Contains("unavailable", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);

        gateway.IsAvailable = true;
        var reload = Assert.IsAssignableFrom<Task<bool>>(typeof(AgentPermissionsViewModel)
            .GetMethod(
                "TryReloadAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(viewModel, [CancellationToken.None]));
        var reloaded = await reload;

        Assert.True(reloaded);
        Assert.Empty(viewModel.Rows);
        Assert.Equal(string.Empty, viewModel.StatusText);
    }

    [AvaloniaFact]
    public async Task SkillsInitialization_MalformedIndexIsPresented()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new SkillStore(scope.Context);
        var indexPath = scope.Context.Storage.RoleLocalWorkspace.GetLocalPath("skills/skills.json");
        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
        await File.WriteAllTextAsync(indexPath, "{ malformed");
        await using var runtime = SkillManagementGatewayFixture.Create(scope.Context, store,
            new SkillImportService(store, new NoOpGitHubSkillClient(), scope.Context));
        var viewModel = new SkillSettingsViewModel(runtime.Gateway);

        await viewModel.InitializeAsync();

        Assert.Equal(SkillStatusKind.Error, viewModel.StatusKind);
        var failure = await runtime.QueryFailureAsync();
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, failure.Error.Kind);
        Assert.Equal("rpc.provider.handler-fault", failure.Error.Code);
        Assert.Equal("The process provider handler failed.", viewModel.StatusText);
        viewModel.Dispose();
    }

    [AvaloniaFact]
    public async Task SubagentInitialization_MalformedIndexIsPresented()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new SubagentStore(scope.Context);
        var indexPath = scope.Context.Storage.RoleLocalWorkspace.GetLocalPath("subagents/subagents.json");
        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
        await File.WriteAllTextAsync(indexPath, "{ malformed");
        using var catalog = new RegressionTestExtensionCatalog();
        using var gateway = new WorkerBackedSubagentManagementGateway(
            new SubagentService(store),
            catalog);
        var viewModel = new SubagentsViewModel(gateway);

        await viewModel.InitializeAsync();

        Assert.Equal(SubagentStatusKind.Error, viewModel.StatusKind);
        Assert.Contains("subagent", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        viewModel.Dispose();
    }

    [AvaloniaFact]
    public async Task SubsessionInitialization_CatalogFailureIsPresented()
    {
        using var runtimeAdapter = new CatalogBackedSubsessionRuntimeAdapter(new ThrowingRpcClient());
        var viewModel = runtimeAdapter.CreateViewModel();

        await viewModel.InitializeAsync();

        Assert.Contains("Injected catalog failure", viewModel.StatusText, StringComparison.Ordinal);
        viewModel.Dispose();
    }

    private sealed class TogglePermissionGateway : IAgentPermissionGateway
    {
        public bool IsAvailable { get; set; }

        public AgentSessionPermissionState GetSessionState(Guid sessionId)
            => new(sessionId, false);

        public void SetSessionUnrestrictedMode(Guid sessionId, bool isEnabled)
        {
        }

        public Task<IReadOnlyList<AgentPermissionActionDescriptor>> ListActionsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfUnavailable();
            return Task.FromResult<IReadOnlyList<AgentPermissionActionDescriptor>>([]);
        }

        public IReadOnlyList<AgentPermissionOverride> ListOverrides()
        {
            ThrowIfUnavailable();
            return [];
        }

        public void SaveOverride(
            string actionId,
            string boundaryId,
            AgentPermissionDecision decision)
        {
        }

        public void DeleteOverride(string actionId, string boundaryId)
        {
        }

        public IReadOnlyList<AgentPendingPermissionRequestRecord> ListPendingRequestsForSessionTree(
            Guid sessionId) => [];

        private void ThrowIfUnavailable()
        {
            if (!IsAvailable)
            {
                throw new InvalidOperationException("Runtime unavailable.");
            }
        }
    }
}
