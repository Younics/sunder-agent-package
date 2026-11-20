extern alias BuilderWorker;

using BuilderWorker::Sunder.Package.Agent.Builder;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class BuilderLocalBackendTests
{
    [Fact]
    public async Task BuildCannotAdoptChangedPhysicalSpellingAfterTheExpectedInspectionWasCaptured()
    {
        using var fixture = new Fixture();
        var first = fixture.WriteDotnet("Workspace", "Package.csproj");
        var expected = await new BuilderProjectDiscovery().InspectAsync(first);
        var originalFolder = Path.GetDirectoryName(first)!;
        var renamedFolder = fixture.PathOf("workspace");
        var staging = fixture.PathOf("renaming-project");
        Directory.Move(originalFolder, staging);
        Directory.Move(staging, renamedFolder);
        var renamed = Path.Combine(renamedFolder, "Package.csproj");
        var current = await new BuilderProjectDiscovery().InspectAsync(renamed);
        Assert.Equal(expected.SourceRevision, current.SourceRevision);
        Assert.NotEqual(expected.EntryPoint, current.EntryPoint);
        await using var jobs = fixture.Jobs();

        var result = await TerminalAsync(jobs, jobs.Start(Request("build", first, BuilderProjectKind.Dotnet) with { Expected = expected }));

        Assert.Equal("failed", result.State);
        Assert.Empty(fixture.Process.Requests);
        Assert.Empty(fixture.Process.Evaluations);
        Assert.Equal(0, fixture.Tools.Selections);
        Assert.Equal(current.SourceRevision, (await new BuilderProjectDiscovery().InspectAsync(renamed)).SourceRevision);
    }

    [FileSystemTheory(TestFileSystemCapability.CaseSensitiveDirectories,
        "Case-distinct expected projects require a case-sensitive test directory.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpectedInspectionCannotAuthorizeACaseDistinctEntryOrProjectFolder(bool ancestor)
    {
        using var fixture = new Fixture();
        TestFileSystemCapabilities.EnableCaseSensitiveDirectory(fixture.Root);
        var first = fixture.WriteDotnet("Workspace", "Package.csproj");
        TestFileSystemCapabilities.EnableCaseSensitiveDirectory(Path.GetDirectoryName(first)!);
        var second = fixture.WriteDotnet(ancestor ? "workspace" : "Workspace", ancestor ? "Package.csproj" : "package.csproj");
        var expected = await new BuilderProjectDiscovery().InspectAsync(first);
        var request = Request("build", second, BuilderProjectKind.Dotnet) with { Expected = expected };

        Assert.Throws<ArgumentException>(() => BuilderJobValidation.Normalize(request));

        Assert.Empty(fixture.Process.Requests);
        Assert.Equal(0, fixture.Tools.Selections);
        Assert.Equal(System.IO.File.ReadAllBytes(first), System.IO.File.ReadAllBytes(second));
    }
}
