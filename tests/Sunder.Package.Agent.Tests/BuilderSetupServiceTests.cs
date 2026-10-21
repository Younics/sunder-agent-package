extern alias BuilderApp;

using BuilderApp::Sunder.Package.Agent.Builder;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class BuilderSetupServiceTests
{
    [Fact]
    public async Task SettingsAreCold_CheckOnNavigation_RefreshRetainsBothCardsAndInstallationRowsWhenOffline()
    {
        await using var fixture = new BuilderUiFixture();
        await using var manager = new BuilderSdkManager(fixture.Application, fixture.Ui);
        var settings = new BuilderSettingsViewModel(manager);
        var dotnet = settings.Cards[0];
        var node = settings.Cards[1];
        Assert.Equal(0, fixture.Backend.ToolchainChecks);
        await settings.PrepareNavigationAsync(new("settings:younics.sunder.package.maker", new Dictionary<string, string?>()));
        var installed = Assert.Single(dotnet.Installations);
        fixture.Backend.Toolchains = kind => new(kind, [BuilderUiFixture.Sdk(kind)], null, DateTimeOffset.UtcNow, null, "Offline");
        await settings.RefreshAsync();
        Assert.Same(dotnet, settings.Cards[0]);
        Assert.Same(node, settings.Cards[1]);
        Assert.Same(installed, Assert.Single(dotnet.Installations));
        Assert.Contains("unknown", dotnet.UpdateText, StringComparison.Ordinal);
        Assert.Contains("Offline", dotnet.UpdateText, StringComparison.Ordinal);
        Assert.Empty(fixture.Backend.Starts);
    }

    [Fact]
    public async Task SettingsInstallRepairRemoveUseBackendJobs_ExternalRowsReadOnly_InUseRemovalErrorStaysVisible()
    {
        await using var fixture = new BuilderUiFixture();
        await using var manager = new BuilderSdkManager(fixture.Application, fixture.Ui);
        var settings = new BuilderSettingsViewModel(manager);
        fixture.Backend.Toolchains = kind => new(kind, [BuilderUiFixture.Sdk(kind) with { IsManaged = kind == BuilderProjectKind.Node }], "recommended", null, null, null);
        await settings.RefreshAsync();
        Assert.False(Assert.Single(settings.Cards[0].Installations).CanManage);
        var node = settings.Cards[1];
        var installation = Assert.Single(node.Installations).Installation;
        await settings.InstallAsync(BuilderProjectKind.Node);
        await settings.RepairAsync(installation);
        fixture.Backend.Result = request => request.Operation == "remove-sdk"
            ? BuilderUiBackend.Snapshot(request, "failed") with { Error = "This SDK is in use by a source watcher." }
            : fixture.Backend.DefaultResult(request);
        await settings.RemoveAsync(installation);
        Assert.Equal(["ensure-sdk", "repair-sdk", "remove-sdk"], fixture.Backend.Starts.Select(request => request.Operation).ToArray());
        Assert.Contains("in use", node.ErrorMessage, StringComparison.Ordinal);
        Assert.Single(node.Installations);
    }

    [Fact]
    public async Task SdkWizardFailureCanRetryWithoutCreatingAnyProjectBookmark()
    {
        await using var fixture = new BuilderUiFixture();
        fixture.Backend.Result = request => BuilderUiBackend.Snapshot(request, "failed") with { Error = "Catalog unavailable" };
        await fixture.Model.CreateProjectAsync();
        fixture.Model.Creation.DisplayName = "My retained input";
        await fixture.Model.SelectTechnologyAsync(BuilderProjectKind.Node);
        Assert.True(fixture.Model.Creation.CanRetrySdk);
        Assert.True(fixture.Model.Creation.HasError);
        Assert.Empty(fixture.Model.Projects);
        fixture.Backend.Result = null;
        await fixture.Model.RetryCreationSdkAsync();
        Assert.True(fixture.Model.Creation.IsDetailsStep);
        Assert.Equal("My retained input", fixture.Model.Creation.DisplayName);
        Assert.All(fixture.Backend.Starts, request => { Assert.Equal("ensure-sdk", request.Operation); Assert.Equal(BuilderProjectKind.Node, request.Kind); });
        Assert.Empty(fixture.Store.Saves);
    }
}
