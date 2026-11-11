extern alias BuilderApp;
extern alias BuilderWorker;

using System.Text.Json;
using BuilderApp::Sunder.Package.Agent.Builder;
using Sunder.Sdk.Rpc;
using Xunit;
using Worker = BuilderWorker::Sunder.Package.Agent.Builder;

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

    [FileSystemTheory(TestFileSystemCapability.CaseSensitiveDirectories,
        "Case-only preflight redirection requires a case-sensitive directory and symbolic links.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturedPhysicalPreflightRejectsCaseOnlyFileOrAncestorRedirectionBeforeBackendInspection(bool ancestor)
    {
        using var temporary = TemporaryDirectory.Create("builder-preflight-path");
        TestFileSystemCapabilities.EnableCaseSensitiveDirectory(temporary.Path);
        await using var fixture = new BuilderUiFixture();
        var folder = temporary.GetPath("Workspace");
        Directory.CreateDirectory(folder);
        TestFileSystemCapabilities.EnableCaseSensitiveDirectory(folder);
        var original = Path.Combine(folder, "Package.csproj");
        WriteDotnetProject(original);
        var project = await InspectWorkerAsync(original);
        var redirectedFolder = ancestor ? temporary.GetPath("workspace") : folder;
        Directory.CreateDirectory(redirectedFolder);
        var redirected = Path.Combine(redirectedFolder, ancestor ? "Package.csproj" : "package.csproj");
        WriteDotnetProject(redirected);
        var redirectedBytes = await File.ReadAllBytesAsync(redirected);
        var inspections = 0;
        fixture.Backend.Inspection = (entry, _) =>
        {
            Interlocked.Increment(ref inspections);
            return Task.FromResult(project with { ProjectFolder = redirectedFolder, EntryPoint = entry });
        };
        if (ancestor)
        {
            Directory.Move(folder, temporary.GetPath("captured-source"));
            Directory.CreateSymbolicLink(folder, redirectedFolder);
        }
        else
        {
            File.Move(original, Path.Combine(folder, "captured-source.txt"));
            File.CreateSymbolicLink(original, redirected);
        }
        await using var service = new BuilderDevelopmentService(fixture.PackageOperations, fixture.Application, null!);
        var request = JsonSerializer.Serialize(new { entryPoint = original, roots = new[] { temporary.Path }, expectedSourceRevision = project.SourceRevision });

        await Assert.ThrowsAsync<SunderRpcException>(async () =>
        {
            using var response = await BuilderContent.ReadAsync<JsonDocument>(service.GetInfoAsync(request), default);
        });

        Assert.Equal(0, inspections);
        Assert.Empty(fixture.Backend.Starts);
        Assert.Empty(fixture.Development.Loads);
        // Physical spelling capture must not silently adopt linked source input.
        await Assert.ThrowsAsync<InvalidDataException>(() => new Worker.BuilderProjectDiscovery().InspectAsync(original));
        Assert.Equal(redirectedBytes, await File.ReadAllBytesAsync(redirected));
    }

    [Fact]
    public async Task CapturedPreflightRejectsAChangedWorkerPhysicalIdentityEvenWithTheSameSourceRevision()
    {
        using var temporary = TemporaryDirectory.Create("builder-preflight-capture");
        await using var fixture = new BuilderUiFixture();
        var original = temporary.GetPath("first", "Package.csproj");
        var other = temporary.GetPath("second", "Package.csproj");
        WriteDotnetProject(original);
        WriteDotnetProject(other);
        var captured = await InspectWorkerAsync(original);
        var replacement = await InspectWorkerAsync(other);
        Assert.Equal(captured.SourceRevision, replacement.SourceRevision);
        Assert.NotEqual(captured.EntryPoint, replacement.EntryPoint);
        var inspections = 0;
        fixture.Backend.Inspection = (_, _) =>
        {
            Interlocked.Increment(ref inspections);
            return Task.FromResult(replacement);
        };
        await using var service = new BuilderDevelopmentService(fixture.PackageOperations, fixture.Application, null!);
        var request = JsonSerializer.Serialize(new { entryPoint = captured.EntryPoint, roots = new[] { temporary.Path }, expectedSourceRevision = captured.SourceRevision });

        var rejected = await Assert.ThrowsAsync<SunderRpcException>(async () =>
        {
            using var response = await BuilderContent.ReadAsync<JsonDocument>(service.GetInfoAsync(request), default);
        });

        Assert.Equal("maker.development.invalid-request", rejected.Error.Code);
        Assert.Equal(1, inspections);
        Assert.Empty(fixture.Backend.Starts);
        Assert.Empty(fixture.Development.Loads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerPreflightCapturesFilesystemCaseAliasesBeforeOrdinalAppAdmission(bool node)
    {
        using var temporary = TemporaryDirectory.Create("builder-preflight-alias");
        await using var fixture = new BuilderUiFixture();
        var original = temporary.GetPath("Workspace", node ? "sunder.package.json" : "MixedCase.csproj");
        var alternate = temporary.GetPath("workspace", node ? "SUNDER.PACKAGE.JSON" : "mixedcase.csproj");
        if (node)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
            File.WriteAllText(original, """
                {"schemaVersion":3,"id":"example.preflight","name":"Preflight source","version":"1.0.0","workerCapabilities":[],"services":{"module":"src/services.ts"}}
                """);
        }
        else WriteDotnetProject(original);
        var before = Directory.GetFiles(temporary.Path, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => (Path: path, Bytes: Convert.ToBase64String(File.ReadAllBytes(path)))).ToArray();
        var physical = new BuilderPathService().CanonicalEntryPoint(original);
        var candidate = File.Exists(alternate) ? alternate : original;
        // Exercise real Worker inspection directly as well as the App boundary;
        // a fake inspection must not fabricate successful alias normalization.
        var worker = await new Worker.BuilderProjectDiscovery().InspectAsync(candidate);
        var project = await BuilderContent.ReadAsync<BuilderProjectInspection>(ValueTask.FromResult(Worker.BuilderContent.Create(worker)), default);
        Assert.Equal(physical, project.EntryPoint);
        Assert.Equal(Path.GetDirectoryName(physical), project.ProjectFolder);
        // A direct job can still arrive through the OS-owned temporary alias,
        // even though its original inspection now carries physical spelling.
        var jobEntry = OperatingSystem.IsMacOS() && (physical.StartsWith("/private/var/", StringComparison.Ordinal)
            || physical.StartsWith("/private/tmp/", StringComparison.Ordinal)) ? physical[8..] : physical;
        var job = Worker.BuilderJobValidation.Normalize(new Worker.BuilderJobRequest
        {
            Operation = "build", Kind = worker.Kind, EntryPoint = jobEntry,
            ProjectFolder = Path.GetDirectoryName(jobEntry), Expected = worker,
        });
        Assert.Equal(physical, job.EntryPoint);
        Assert.Equal(project.ProjectFolder, job.ProjectFolder);
        fixture.Backend.Inspection = InspectWorkerAsync;
        await using var service = new BuilderDevelopmentService(fixture.PackageOperations, fixture.Application, null!);
        using var preflight = await BuilderContent.ReadAsync<JsonDocument>(service.GetInfoAsync(JsonSerializer.Serialize(new
        {
            entryPoint = candidate, roots = new[] { temporary.Path }, expectedSourceRevision = (string?)null,
        })), default);
        var captured = preflight.RootElement.GetProperty("project");
        Assert.Equal(physical, captured.GetProperty("entryPoint").GetString());
        var request = JsonSerializer.Serialize(new { entryPoint = captured.GetProperty("entryPoint").GetString(),
            roots = new[] { temporary.Path }, expectedSourceRevision = captured.GetProperty("sourceRevision").GetString() });

        using var response = await BuilderContent.ReadAsync<JsonDocument>(service.GetInfoAsync(request), default);

        Assert.Equal(physical, response.RootElement.GetProperty("project").GetProperty("entryPoint").GetString());
        Assert.Equal(project.SourceRevision, response.RootElement.GetProperty("project").GetProperty("sourceRevision").GetString());
        Assert.Empty(fixture.Backend.Starts);
        Assert.Empty(fixture.Development.Loads);
        Assert.Equal(before, Directory.GetFiles(temporary.Path, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => (Path: path, Bytes: Convert.ToBase64String(File.ReadAllBytes(path)))).ToArray());
    }

    private static void WriteDotnetProject(string entryPoint)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(entryPoint)!);
        File.WriteAllText(entryPoint, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><SunderPackageProject>true</SunderPackageProject></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(entryPoint)!, "PackageMetadata.cs"),
            "using Sunder.Sdk.Packaging;\n[assembly: SunderPackage(Id = \"example.preflight\", Name = \"Preflight source\")]\n");
    }

    private static async Task<BuilderProjectInspection> InspectWorkerAsync(string entryPoint, CancellationToken token = default)
        => await BuilderContent.ReadAsync<BuilderProjectInspection>(ValueTask.FromResult(
            Worker.BuilderContent.Create(await new Worker.BuilderProjectDiscovery().InspectAsync(entryPoint, token))), token);
}
