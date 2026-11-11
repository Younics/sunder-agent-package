extern alias BuilderApp;

using BuilderApp::Sunder.Package.Agent.Builder;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class BuilderPackagePathIdentityTests
{
    [FileSystemTheory(TestFileSystemCapability.CaseSensitiveDirectories,
        "Case-distinct development owners require a case-sensitive test directory.")]
    [InlineData("lookup")]
    [InlineData("release")]
    [InlineData("view")]
    [InlineData("logs")]
    public async Task CaseDistinctProjectCannotBorrowOrReleaseAnotherProjectsDevelopmentOwner(string boundary)
    {
        using var temporary = TemporaryDirectory.Create("builder-owner-case");
        TestFileSystemCapabilities.EnableCaseSensitiveDirectory(temporary.Path);
        await using var fixture = new BuilderUiFixture();
        var first = Project(temporary.GetPath("Workspace", "Package.csproj"));
        var second = Project(temporary.GetPath("workspace", "Package.csproj"));
        fixture.Development.PackageIdResolver = _ => first.PackageId;
        var owner = await fixture.PackageOperations.LoadAsync(first, Path.Combine(first.ProjectFolder, "sunder-dev"), default);
        var handle = Assert.Single(fixture.Development.Sessions);

        switch (boundary)
        {
            case "lookup":
                Assert.Null(fixture.PackageOperations.GetSession(second.EntryPoint));
                break;
            case "release":
                if (fixture.PackageOperations.GetSession(second.EntryPoint) is { } selected)
                    await fixture.PackageOperations.ReleaseAsync(selected);
                break;
            case "view":
                await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PackageOperations.GetViewsAsync(second, default));
                break;
            case "logs":
                await Assert.ThrowsAsync<Sunder.Sdk.Rpc.SunderRpcException>(() => fixture.PackageOperations.GetLogsAsync(second, new PackageDevelopmentLogQuery(), default));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(boundary));
        }

        Assert.False(handle.Disposed);
        Assert.Same(owner, fixture.PackageOperations.GetSession(first.EntryPoint));
        Assert.Single(fixture.PackageOperations.Sessions);
        Assert.Empty(fixture.Backend.Starts);
    }

    [Fact]
    public async Task ExistingCaseAliasesShareOneOwnerButCaseDistinctFilesLoadIndependentOwners()
    {
        using var temporary = TemporaryDirectory.Create("builder-owner-alias");
        await using var fixture = new BuilderUiFixture();
        var first = Project(temporary.GetPath("MixedCase.csproj"));
        var alternate = first with { EntryPoint = temporary.GetPath("mixedcase.csproj") };
        fixture.Development.PackageIdResolver = _ => first.PackageId;
        var owner = await fixture.PackageOperations.LoadAsync(first, Path.Combine(first.ProjectFolder, "sunder-dev"), default);

        if (File.Exists(alternate.EntryPoint))
        {
            Assert.Same(owner, fixture.PackageOperations.GetSession(alternate.EntryPoint));
            Assert.Same(owner, await fixture.PackageOperations.LoadAsync(alternate, Path.Combine(first.ProjectFolder, "sunder-dev"), default));
            Assert.Single(fixture.PackageOperations.Sessions);
            Assert.Single(fixture.Development.Loads);
        }
        else
        {
            File.WriteAllText(alternate.EntryPoint, "distinct");
            Assert.Null(fixture.PackageOperations.GetSession(alternate.EntryPoint));
            var other = await fixture.PackageOperations.LoadAsync(alternate, Path.Combine(first.ProjectFolder, "other-dev"), default);
            Assert.NotSame(owner, other);
            Assert.Equal(2, fixture.PackageOperations.Sessions.Count);
            await fixture.PackageOperations.ReleaseAsync(other);
            Assert.Same(owner, fixture.PackageOperations.GetSession(first.EntryPoint));
            Assert.Single(fixture.PackageOperations.Sessions);
        }
    }

    [Fact]
    public async Task ReleasingCapturedOwnerAfterSourceSpellingChangesRemovesOnlyThatOwner()
    {
        using var temporary = TemporaryDirectory.Create("builder-owner-renamed");
        await using var fixture = new BuilderUiFixture();
        var project = Project(temporary.GetPath("MixedCase.csproj"));
        fixture.Development.PackageIdResolver = _ => project.PackageId;
        var owner = await fixture.PackageOperations.LoadAsync(project, Path.Combine(project.ProjectFolder, "sunder-dev"), default);
        File.Move(project.EntryPoint, temporary.GetPath("renaming.csproj"));
        File.Move(temporary.GetPath("renaming.csproj"), temporary.GetPath("mixedcase.csproj"));

        await fixture.PackageOperations.ReleaseAsync(owner);

        Assert.True(Assert.Single(fixture.Development.Sessions).Disposed);
        Assert.Empty(fixture.PackageOperations.Sessions);
        Assert.Equal("source", await File.ReadAllTextAsync(temporary.GetPath("mixedcase.csproj")));
    }

    [Fact]
    public async Task AliasedLoadWaitsForTheOriginalOwnersActualLoadRatherThanBorrowingItsPendingHandle()
    {
        using var temporary = TemporaryDirectory.Create("builder-owner-gate");
        await using var fixture = new BuilderUiFixture();
        var project = Project(temporary.GetPath("MixedCase.csproj"));
        var alternate = temporary.GetPath("mixedcase.csproj");
        var alias = File.Exists(alternate) ? alternate : Path.Combine(temporary.Path, ".", "MixedCase.csproj");
        fixture.Development.PackageIdResolver = _ => project.PackageId;
        var entered = BuilderUiFixture.Signal();
        var release = BuilderUiFixture.Signal();
        fixture.Development.BeforeLoad = async (_, _, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        var first = fixture.PackageOperations.LoadAsync(project, Path.Combine(project.ProjectFolder, "sunder-dev"), default);
        Task? second = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var waiting = fixture.PackageOperations.LoadAsync(project with { EntryPoint = alias }, Path.Combine(project.ProjectFolder, "sunder-dev"), default);
            second = waiting;
            Assert.False(waiting.IsCompleted);
            release.TrySetResult();
            var owner = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(owner, await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(owner.HasHandle);
            Assert.Single(fixture.Development.Loads);
        }
        finally
        {
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static BuilderProjectInspection Project(string entryPoint)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(entryPoint)!);
        File.WriteAllText(entryPoint, "source");
        return BuilderUiFixture.Project("shared-package-id") with
        {
            ProjectFolder = Path.GetDirectoryName(entryPoint)!, EntryPoint = entryPoint,
        };
    }
}
