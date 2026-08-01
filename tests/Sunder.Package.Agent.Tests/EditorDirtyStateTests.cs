using Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Package.Agent.Subagents.PackageViews;
using Sunder.Package.Agent.Subagents.Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class EditorDirtyStateTests
{
    [Fact]
    public async Task AgentProfilesViewModel_SelectionPreservesDirtyDraftPerProfile()
    {
        using var scope = RegressionTestPackageScope.Create();
        var extensionCatalog = new RegressionTestExtensionCatalog();
        using var profileService = CreateProfileService(scope, extensionCatalog);
        var first = await profileService.CreateProfileAsync("Alpha");
        var second = await profileService.CreateProfileAsync("Beta");
        using var viewModel = new AgentProfilesViewModel(profileService);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedProfile is not null && !viewModel.IsBusy);

        viewModel.SelectedProfile = viewModel.Profiles.Single(item => item.ProfileId == first.ProfileId);
        await WaitUntilAsync(() => viewModel.DisplayName == "Alpha" && !viewModel.IsBusy);
        viewModel.DisplayName = "Alpha draft";

        Assert.True(viewModel.IsDirty);

        viewModel.SelectedProfile = viewModel.Profiles.Single(item => item.ProfileId == second.ProfileId);
        await WaitUntilAsync(() => viewModel.DisplayName == "Beta" && !viewModel.IsBusy);

        Assert.False(viewModel.IsDirty);

        viewModel.SelectedProfile = viewModel.Profiles.Single(item => item.ProfileId == first.ProfileId);
        await WaitUntilAsync(() => viewModel.DisplayName == "Alpha draft" && !viewModel.IsBusy);

        Assert.True(viewModel.IsDirty);
        Assert.Equal("Alpha", profileService.GetProfile(first.ProfileId)?.DisplayName);
    }

    [Fact]
    public async Task SubagentsViewModel_SelectionPreservesDirtyDraftPerSubagent()
    {
        using var scope = RegressionTestPackageScope.Create();
        var service = new SubagentService(new SubagentStore(scope.Context));
        var first = service.CreateSubagent("Alpha");
        var second = service.CreateSubagent("Beta");
        using var viewModel = new SubagentsViewModel(service, new RegressionTestExtensionCatalog());
        await viewModel.InitializeAsync();

        viewModel.SelectedSubagent = viewModel.Subagents.Single(item => item.SubagentId == first.SubagentId);
        await WaitUntilAsync(() => viewModel.DisplayName == "Alpha");
        viewModel.DisplayName = "Alpha draft";

        Assert.True(viewModel.IsDirty);

        viewModel.SelectedSubagent = viewModel.Subagents.Single(item => item.SubagentId == second.SubagentId);
        await WaitUntilAsync(() => viewModel.DisplayName == "Beta");

        Assert.False(viewModel.IsDirty);

        viewModel.SelectedSubagent = viewModel.Subagents.Single(item => item.SubagentId == first.SubagentId);
        await WaitUntilAsync(() => viewModel.DisplayName == "Alpha draft");

        Assert.True(viewModel.IsDirty);
        Assert.Equal("Alpha", service.GetSubagent(first.SubagentId)?.DisplayName);
    }

    private static AgentProfileService CreateProfileService(
        RegressionTestPackageScope scope,
        RegressionTestExtensionCatalog extensionCatalog)
    {
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store, extensionCatalog);
        var workspaceService = new AgentWorkspaceService(store, extensionCatalog, sessionService);
        var toolService = new AgentToolService(
            sessionService,
            workspaceService,
            new AgentExecutionTargetService(extensionCatalog),
            extensionCatalog);
        return new AgentProfileService(store, toolService, extensionCatalog, extensionCatalog.BehaviorLoops);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= timeout)
            {
                throw new TimeoutException("Timed out waiting for editor state.");
            }

            await Task.Delay(10);
        }
    }
}
