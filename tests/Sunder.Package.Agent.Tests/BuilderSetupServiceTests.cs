extern alias BuilderApp;

using BuilderApp::Sunder.Package.Agent.Builder;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class BuilderSetupServiceTests
{
    [Fact]
    public async Task SettingsAreCold_NavigationStartsOwnedRefresh_RetainsBothCardsAndInstallationRowsWhenOffline()
    {
        await using var fixture = new BuilderUiFixture();
        await using var manager = new BuilderSdkManager(fixture.Application, fixture.Ui);
        var settings = new BuilderSettingsViewModel(manager);
        var dotnet = settings.Cards[0];
        var node = settings.Cards[1];
        Assert.Equal(0, fixture.Backend.ToolchainChecks);
        Assert.True(await settings.PrepareNavigationAsync(Navigation()));
        await settings.RefreshAsync();
        var installed = Assert.Single(dotnet.Installations);
        fixture.Backend.Toolchains = kind => new(kind, [BuilderUiFixture.Sdk(kind)], null, DateTimeOffset.UtcNow, null, "Offline");
        await settings.RefreshAsync();
        Assert.Same(dotnet, settings.Cards[0]);
        Assert.Same(node, settings.Cards[1]);
        Assert.Same(installed, Assert.Single(dotnet.Installations));
        Assert.Contains("unknown", dotnet.UpdateText, StringComparison.Ordinal);
        Assert.Contains("Offline", dotnet.UpdateText, StringComparison.Ordinal);
        Assert.Contains("Offline", dotnet.Status, StringComparison.Ordinal);
        Assert.Equal(".NET", dotnet.Glyph);
        Assert.Equal("JS", node.Glyph);
        Assert.Contains(dotnet.CurrentVersion, dotnet.VersionText, StringComparison.Ordinal);
        Assert.Empty(fixture.Backend.Starts);
    }

    [Fact]
    public async Task NavigationDoesNotWaitForSdkChecks_ReopeningAndManualRefreshJoinWork_CancelledWaiterDoesNotCancelIt()
    {
        await using var fixture = new BuilderUiFixture();
        await using var manager = new BuilderSdkManager(fixture.Application, fixture.Ui);
        var settings = new BuilderSettingsViewModel(manager);
        var entered = BuilderUiFixture.Signal();
        var release = BuilderUiFixture.Signal();
        var checks = 0;
        fixture.Backend.BeforeToolchainCheck = async (_, checkUpdates, token) =>
        {
            Assert.True(checkUpdates);
            if (Interlocked.Increment(ref checks) == 2) entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        try
        {
            using var navigation = new CancellationTokenSource();
            var preparation = settings.PrepareNavigationAsync(Navigation(), navigation.Token);
            Assert.True(preparation.IsCompletedSuccessfully);
            Assert.True(await preparation);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.All(settings.Cards, card =>
            {
                Assert.True(card.IsRefreshing);
                Assert.False(card.IsEmpty);
                Assert.Equal("Checking…", card.CurrentVersion);
                Assert.Equal("Checking SDKs…", card.Status);
            });
            navigation.Cancel();
            var reopened = new BuilderSettingsViewModel(manager);
            Assert.True(await reopened.PrepareNavigationAsync(Navigation()));
            Assert.Same(settings.Cards, reopened.Cards);
            using var waiter = new CancellationTokenSource();
            var cancelledWait = settings.RefreshAsync(waiter.Token);
            waiter.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait);
            var refresh = reopened.RefreshAsync();
            Assert.False(refresh.IsCompleted);
            Assert.Equal(2, fixture.Backend.ToolchainChecks);
            release.TrySetResult();
            await refresh.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.All(settings.Cards, card =>
            {
                Assert.False(card.IsRefreshing);
                Assert.Single(card.Installations);
                Assert.False(card.HasError);
            });
            Assert.Equal(2, fixture.Backend.ToolchainChecks);
            Assert.Empty(fixture.Backend.Starts);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task DisposingManagerCancelsAndDrainsNavigationRefresh()
    {
        await using var fixture = new BuilderUiFixture();
        await using var manager = new BuilderSdkManager(fixture.Application, fixture.Ui);
        var entered = BuilderUiFixture.Signal();
        var checks = 0;
        var stopped = 0;
        fixture.Backend.BeforeToolchainCheck = async (_, _, token) =>
        {
            if (Interlocked.Increment(ref checks) == 2) entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { Interlocked.Increment(ref stopped); }
        };
        var settings = new BuilderSettingsViewModel(manager);
        Assert.True(await settings.PrepareNavigationAsync(Navigation()));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(2, stopped);
        Assert.Empty(fixture.Backend.Starts);
    }

    [Fact]
    public async Task InitialRefreshFailureIsNotReportedAsEmpty_ConfirmedEmptyStatusCanRecover()
    {
        await using var fixture = new BuilderUiFixture();
        await using var manager = new BuilderSdkManager(fixture.Application, fixture.Ui);
        fixture.Backend.Toolchains = _ => throw new IOException("SDK discovery unavailable");
        await manager.RefreshAsync();
        Assert.All(manager.Cards, card =>
        {
            Assert.True(card.HasError);
            Assert.False(card.IsEmpty);
            Assert.Contains("unavailable", card.ErrorMessage, StringComparison.Ordinal);
            Assert.Equal("SDK discovery unavailable", card.Status);
        });
        fixture.Backend.Toolchains = kind => new(kind, [], null, null, null, null);
        await manager.RefreshAsync();
        Assert.All(manager.Cards, card =>
        {
            Assert.False(card.HasError);
            Assert.True(card.IsEmpty);
            Assert.Equal("Not installed", card.Status);
            Assert.Equal("Install SDK", card.PrimaryActionText);
        });
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

    private static PackageViewNavigationContext Navigation()
        => new("settings:younics.sunder.package.maker", new Dictionary<string, string?>());
}
