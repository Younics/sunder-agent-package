extern alias BuilderApp;

using BuilderApp::Sunder.Package.Agent.Builder;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class BuilderPackageOperationsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdenticalOutputIsUnchangedOnlyWhileItsAppliedOwnerIsHealthy(bool faulted)
    {
        await using var fixture = new BuilderUiFixture();
        using var package = RegressionTestPackageScope.Create();
        var root = new BuilderPathService().ResolveContainedHostPath(package.RootPath, Path.GetTempPath());
        var project = BuilderUiFixture.Project("apply-health") with { ProjectFolder = root, EntryPoint = Path.Combine(root, "Package.csproj") };
        var folder = Path.Combine(root, "sunder-dev");
        var identity = await BuilderTestDevelopmentOutput.WriteAsync(project, folder);
        fixture.Development.PackageIdResolver = _ => project.PackageId;
        var owner = await fixture.PackageOperations.LoadAsync(project, folder, default);
        var handle = Assert.Single(fixture.Development.Sessions);
        handle.Status = handle.Status with { AppliedRevision = new(Guid.NewGuid(), 1, identity), AppReadiness = faulted ? PackageDevelopmentAppReadiness.Quarantined : PackageDevelopmentAppReadiness.Activated };
        handle.OnReload = (_, _, _) =>
        {
            var revision = new PackageDevelopmentRevision(Guid.NewGuid(), 5, identity);
            handle.Status = handle.Status with { AppliedRevision = revision, RuntimeRevision = revision, AppReadiness = PackageDevelopmentAppReadiness.Activated };
            return Task.FromResult(revision);
        };
        var selection = await fixture.PackageOperations.SelectApplyAsync(project, null, default);
        var result = await fixture.PackageOperations.ApplyBuiltAsync(selection, Guid.NewGuid().ToString("N"), default);
        Assert.Equal(faulted ? "reloaded" : "unchanged", result.Effect);
        Assert.Equal(faulted ? 1 : 0, handle.Reloads.Count);
        Assert.Same(owner, fixture.PackageOperations.GetSession(project.EntryPoint));
        Assert.Single(fixture.Development.Loads);
        Assert.Empty(fixture.Backend.Starts);
    }

    [Fact]
    public async Task SharedOperationsWorkWithoutOpeningMakerAndUiBorrowsTheSameOwner()
    {
        await using var fixture = new BuilderUiFixture();
        var project = BuilderUiFixture.Project("headless-maker");
        fixture.Backend.Projects[project.EntryPoint] = project;
        var result = await fixture.PackageOperations.RunJobAsync(new() { Operation = "build", Kind = project.Kind,
            EntryPoint = project.EntryPoint, ProjectFolder = project.ProjectFolder });
        Assert.Empty(fixture.Development.Loads);
        var owner = await fixture.PackageOperations.LoadAsync(project, result.DevelopmentFolder!, default);
        Assert.True(owner.HasHandle);
        Assert.Empty(fixture.Model.Projects);
        var row = await fixture.OpenAsync("headless-maker");
        await fixture.Model.LoadSelectedProjectAsync();
        Assert.Single(fixture.Development.Loads);
        Assert.Single(fixture.Backend.Starts);
        await fixture.Model.UnloadSelectedProjectAsync();
        Assert.True(Assert.Single(fixture.Development.Sessions).Disposed);
        Assert.False(row.HasSession);
    }

    [Fact]
    public async Task ShellOutputReloadRejectsCorruptOutputWithoutBuildingOrRetiringTheExistingOwner()
    {
        await using var fixture = new BuilderUiFixture();
        using var package = RegressionTestPackageScope.Create();
        var folderRoot = new BuilderPathService().ResolveContainedHostPath(package.RootPath, Path.GetTempPath());
        var project = BuilderUiFixture.Project("shell-output-reload") with { ProjectFolder = folderRoot, EntryPoint = Path.Combine(folderRoot, "Package.csproj") };
        var folder = Path.Combine(project.ProjectFolder, "sunder-dev");
        await BuilderTestDevelopmentOutput.WriteAsync(project, folder);
        fixture.Development.PackageIdResolver = _ => project.PackageId;
        var owner = await fixture.PackageOperations.LoadAsync(project, folder, default);
        var handle = Assert.Single(fixture.Development.Sessions);
        await File.AppendAllTextAsync(Path.Combine(folder, "payload", "runtime", BuilderToolchainPlatform.Current.DotnetRid, "worker.js"), "corrupt");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.PackageOperations.ReloadBuiltOutputAsync(project, Guid.NewGuid().ToString("N")));
        Assert.Empty(handle.Reloads);
        Assert.Null(owner.PendingHotreload);
        Assert.False(handle.Disposed);
        var identity = await BuilderTestDevelopmentOutput.WriteAsync(project, folder);
        var id = Guid.NewGuid().ToString("N");
        Assert.Equal(identity, (await fixture.PackageOperations.ReloadBuiltOutputAsync(project, id)).SourceContentIdentity);
        Assert.Equal((id, identity), Assert.Single(handle.Reloads));
        Assert.Single(fixture.Development.Loads);
        Assert.Empty(fixture.Backend.Starts);
    }

    [Fact]
    public async Task HotreloadBuildsOnceAppliesTheExactOutputAndRetainsTheSameDevelopmentOwner()
    {
        await using var fixture = new BuilderUiFixture();
        var row = await fixture.OpenAsync("explicit-hotreload");
        await fixture.Model.LoadSelectedProjectAsync();
        var handle = Assert.Single(fixture.Development.Sessions);
        await fixture.Model.HotreloadSelectedProjectAsync();
        var build = Assert.Single(fixture.Backend.Starts, request => request.Operation == "build");
        var reload = Assert.Single(handle.Reloads);
        Assert.Equal(build.Id, reload.OperationId);
        Assert.Equal(fixture.Backend.Jobs[build.Id!].DevelopmentContentIdentity, reload.Identity);
        Assert.Single(fixture.Development.Loads);
        Assert.False(handle.Disposed);
        Assert.False(row.HotreloadPending);
        Assert.Contains("Hotreload applied revision", row.StatusText);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task UnsuccessfulBuildCannotStartReloadOrRetireTheLastGoodOwner(string state)
    {
        await using var fixture = new BuilderUiFixture();
        var row = await fixture.OpenAsync("failed-hotreload");
        await fixture.Model.LoadSelectedProjectAsync();
        var handle = Assert.Single(fixture.Development.Sessions);
        fixture.Backend.Result = request => fixture.Backend.DefaultResult(request) with { State = state, DevelopmentContentIdentity = null };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PackageOperations.HotreloadAsync(row.Inspection!));
        Assert.Empty(handle.Reloads);
        Assert.False(handle.Disposed);
        Assert.Null(fixture.PackageOperations.GetSession(row.EntryPoint)!.PendingHotreload);
    }

    [Fact]
    public async Task LostReloadAcknowledgementBlocksOtherMutationsAndExplicitResolutionNeverRebuilds()
    {
        await using var fixture = new BuilderUiFixture();
        var row = await fixture.OpenAsync("lost-hotreload");
        await fixture.Model.LoadSelectedProjectAsync();
        var handle = Assert.Single(fixture.Development.Sessions);
        var revision = new PackageDevelopmentRevision(Guid.NewGuid(), 7, new('c', 64));
        handle.OnReload = (_, _, _) => throw new IOException("Commit response lost.");
        await Assert.ThrowsAsync<IOException>(() => fixture.PackageOperations.HotreloadAsync(row.Inspection!));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PackageOperations.RunJobAsync(new()
        { Operation = "build", Kind = row.Kind!.Value, EntryPoint = row.EntryPoint, ProjectFolder = row.ProjectFolder }));
        handle.OnReload = (_, _, _) => { handle.Status = handle.Status with { RuntimeRevision = revision, AppliedRevision = revision }; return Task.FromResult(revision); };
        Assert.Equal(revision, await fixture.PackageOperations.HotreloadAsync(row.Inspection!));
        Assert.Single(fixture.Backend.Starts, request => request.Operation == "build");
        Assert.Equal(2, handle.Reloads.Count);
        Assert.Single(handle.Reloads.Distinct());
    }

    [Fact]
    public async Task CancelledAppliedObservationResumesOnlyTheRecordedOutcomeAndRejectsAnUnrelatedRevision()
    {
        await using var fixture = new BuilderUiFixture();
        var row = await fixture.OpenAsync("observe-hotreload");
        await fixture.Model.LoadSelectedProjectAsync();
        var handle = Assert.Single(fixture.Development.Sessions);
        var committed = new PackageDevelopmentRevision(Guid.NewGuid(), 7, new('c', 64));
        var entered = BuilderUiFixture.Signal();
        handle.OnReload = (_, _, _) =>
        {
            handle.Status = handle.Status with { AppliedRevision = new(committed.RuntimeInstanceId, committed.SessionGeneration, new('d', 64)) };
            entered.TrySetResult(); return Task.FromResult(committed);
        };
        using var cancel = new CancellationTokenSource();
        var operation = fixture.PackageOperations.HotreloadAsync(row.Inspection!, cancellationToken: cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(operation.IsCompleted);
        cancel.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(cancel.Token, error.CancellationToken);
        handle.Status = handle.Status with { AppliedRevision = committed };
        Assert.Equal(committed, await fixture.PackageOperations.HotreloadAsync(row.Inspection!));
        Assert.Single(handle.Reloads);
        Assert.Single(fixture.Backend.Starts, request => request.Operation == "build");
    }
}
