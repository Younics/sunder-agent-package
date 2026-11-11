extern alias BuilderApp;

using BuilderApp::Sunder.Package.Agent.Builder;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class BuilderPathIdentityTests
{
    [FileSystemTheory(TestFileSystemCapability.CaseSensitiveDirectories,
        "Case-distinct sibling containment requires a case-sensitive test directory.")]
    [InlineData(false)]
    [InlineData(true)]
    public void CaseDistinctSiblingIsNotContainedEvenWhenTheTargetDoesNotExist(bool exists)
    {
        using var temporary = TemporaryDirectory.Create("builder-path-identity");
        TestFileSystemCapabilities.EnableCaseSensitiveDirectory(temporary.Path);
        var allowed = temporary.GetPath("Workspace");
        var outside = temporary.GetPath("workspace");
        Directory.CreateDirectory(allowed);
        Directory.CreateDirectory(outside);
        var target = Path.Combine(outside, "project.csproj");
        if (exists) File.WriteAllText(target, "outside");
        var paths = new BuilderPathService();

        Assert.Throws<InvalidOperationException>(() => paths.ResolveContainedHostPath(target, allowed));
        Assert.Equal(Path.Combine(allowed, "new", "project.csproj"),
            paths.ResolveContainedHostPath(Path.Combine(allowed, "new", "project.csproj"), allowed));
        Assert.Equal(exists, File.Exists(target));
        Assert.Empty(Directory.EnumerateFileSystemEntries(allowed));
    }

    [FileSystemFact(TestFileSystemCapability.CaseSensitiveDirectories,
        "Distinct source identities require a case-sensitive test directory.")]
    public void CaseDistinctExistingEntryPointsDoNotAcquireEachOthersIdentity()
    {
        using var temporary = TemporaryDirectory.Create("builder-path-entry");
        TestFileSystemCapabilities.EnableCaseSensitiveDirectory(temporary.Path);
        var first = temporary.GetPath("Project.csproj");
        var second = temporary.GetPath("project.csproj");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");
        var paths = new BuilderPathService();

        Assert.False(paths.SameEntryPoint(first, second));
        Assert.NotEqual(paths.CanonicalEntryPoint(first), paths.CanonicalEntryPoint(second));
        Assert.Equal("first", File.ReadAllText(first));
        Assert.Equal("second", File.ReadAllText(second));
    }

    [Fact]
    public void ExistingCaseAliasUsesTheFilesystemSpellingRatherThanAnOsWideFold()
    {
        using var temporary = TemporaryDirectory.Create("builder-path-alias");
        var original = temporary.GetPath("MixedCase.csproj");
        var alternate = temporary.GetPath("mixedcase.csproj");
        File.WriteAllText(original, "source");
        var paths = new BuilderPathService();
        Assert.Equal(paths.CanonicalEntryPoint(original), paths.ResolveContainedHostPath(original, Path.GetPathRoot(original)!));

        if (File.Exists(alternate))
        {
            Assert.Equal(paths.CanonicalEntryPoint(original), paths.CanonicalEntryPoint(alternate));
            Assert.True(paths.SameEntryPoint(original, alternate));
            Assert.Equal(paths.CanonicalEntryPoint(original), paths.ResolveContainedHostPath(alternate, temporary.Path));
        }
        else
        {
            File.WriteAllText(alternate, "distinct");
            Assert.NotEqual(paths.CanonicalEntryPoint(original), paths.CanonicalEntryPoint(alternate));
            Assert.False(paths.SameEntryPoint(original, alternate));
        }
        Assert.Equal("source", File.ReadAllText(original));
    }
}
