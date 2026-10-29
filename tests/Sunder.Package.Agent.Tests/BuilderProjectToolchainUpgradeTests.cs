extern alias BuilderWorker;
extern alias BuilderApp;

using System.Text.Json;
using Sunder.Sdk.Abstractions;
using BuilderWorker::Sunder.Package.Agent.Builder;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class BuilderProjectToolchainUpgradeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sunder-project-toolchain-upgrade", Guid.NewGuid().ToString("N"));
    private static readonly SunderDevelopmentToolchain Versions = new("10.0.403", "24.22.1", "11.19.1");

    [Fact]
    public async Task WorkerAssessmentSurvivesTheBoundedOpaqueContentBoundaryIntoTheAppRole()
    {
        var entry = Dotnet();
        await File.WriteAllTextAsync(Path.Combine(root, "global.json"), "{\"sdk\":{\"version\":\"10.0.100\",\"rollForward\":\"disable\"}}");
        var project = await new BuilderProjectDiscovery(Versions).InspectAsync(entry);
        var content = BuilderContent.Create(project);
        var presented = await BuilderApp::Sunder.Package.Agent.Builder.BuilderContent
            .ReadAsync<BuilderApp::Sunder.Package.Agent.Builder.BuilderProjectInspection>(ValueTask.FromResult(content), default);
        Assert.Equal(project.PackageId, presented.PackageId);
        Assert.Equal(project.SourceRevision, presented.SourceRevision);
        Assert.True(presented.ToolchainAssessment!.RequiresUpgrade);
        Assert.True(presented.ToolchainAssessment.CanUpgrade);
        Assert.Equal(project.ToolchainAssessment!.Message, presented.ToolchainAssessment.Message);
    }

    [Theory]
    [InlineData("{\"sdk\":{\"version\":\"10.0.100\",\"rollForward\":\"disable\"}}", true, true)]
    [InlineData("{\"sdk\":{\"version\":\"10.0.100\",\"rollForward\":\"latestFeature\"}}", false, false)]
    [InlineData("{\"sdk\":{\"version\":\"10.0.403\",\"rollForward\":\"disable\"}}", false, false)]
    [InlineData("{\"sdk\":{\"version\":\"invalid\"}}", false, false)]
    public async Task DiscoveryAssessesActualDotnetRequirementsWithoutWritingOrClaimingUnknownPinsNeedUpgrade(string pin, bool needed, bool available)
    {
        var entry = Dotnet();
        await File.WriteAllTextAsync(Path.Combine(root, "global.json"), pin);
        var before = Pins();
        var project = Assert.Single(await new BuilderProjectDiscovery(Versions).DiscoverAsync(root));
        Assert.Equal(entry, project.EntryPoint);
        var assessment = Assert.IsType<BuilderProjectToolchainAssessment>(project.ToolchainAssessment);
        Assert.Equal(needed, assessment.RequiresUpgrade);
        Assert.Equal(available, assessment.CanUpgrade);
        if (!needed && !pin.Contains("invalid", StringComparison.Ordinal)) Assert.Null(assessment.Message);
        Assert.Equal(before, Pins());
        Assert.Null((await new BuilderProjectDiscovery().InspectAsync(entry)).ToolchainAssessment);
    }

    [Theory]
    [InlineData("24.18.1", "npm@11.16.0", ">=24 <25", true, true)]
    [InlineData("24.x", "npm@^11", ">=24 <25", false, false)]
    [InlineData("24.22.1", "npm@11.19.1", "<24.22.0", true, false)]
    [InlineData("lts/*", "npm@11.19.1", ">=24 <25", false, false)]
    [InlineData("24.18.1", "pnpm@10.0.0", ">=24 <25", false, false)]
    public async Task NodeAssessmentSharesUpgradePlanButNeverOffersToRewriteRuntimeRequirements(string node, string manager, string engine, bool needed, bool available)
    {
        var entry = Node(root);
        await File.WriteAllTextAsync(Path.Combine(root, ".node-version"), node + "\n");
        var path = Path.Combine(root, "package.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        json["packageManager"] = manager;
        json["engines"]!["node"] = engine;
        await File.WriteAllTextAsync(path, json.ToJsonString());
        var before = Pins();
        var project = await new BuilderProjectDiscovery(Versions).InspectAsync(entry);
        Assert.Equal(needed, project.ToolchainAssessment!.RequiresUpgrade);
        Assert.Equal(available, project.ToolchainAssessment.CanUpgrade);
        Assert.Equal(before, Pins());
        if (needed && !available)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => BuilderProjectToolchainUpgrade.UpgradeAsync(project, Tools(BuilderProjectKind.Node), default));
            Assert.Equal(before, Pins());
        }
    }

    private (string Path, string Hash, long Modified)[] Pins() => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => (path, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))),
            File.GetLastWriteTimeUtc(path).Ticks)).ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DotnetUpgradeChangesOnlyDevelopmentPinsAndPreservesTargetAndLocks(bool bom)
    {
        var entry = Dotnet();
        const string pin = "{\"sdk\":{\"version\":\"10.0.100\",\"rollForward\":\"disable\",\"paths\":[\"$host$\"]},\"unrelated\":true}";
        await File.WriteAllTextAsync(Path.Combine(root, "global.json"), pin, new System.Text.UTF8Encoding(bom));
        await File.WriteAllTextAsync(Path.Combine(root, "sunder-contracts.lock.json"), "retained lock bytes");
        var projectBytes = await File.ReadAllBytesAsync(entry);
        var source = await new BuilderProjectDiscovery().InspectAsync(entry);
        await BuilderProjectToolchainUpgrade.UpgradeAsync(source, Tools(BuilderProjectKind.Dotnet), default);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "global.json")));
        Assert.Equal("10.0.403", json.RootElement.GetProperty("sdk").GetProperty("version").GetString());
        Assert.Equal("disable", json.RootElement.GetProperty("sdk").GetProperty("rollForward").GetString());
        Assert.Equal("$host$", json.RootElement.GetProperty("sdk").GetProperty("paths")[0].GetString());
        Assert.True(json.RootElement.GetProperty("unrelated").GetBoolean());
        Assert.Equal(bom, (await File.ReadAllBytesAsync(Path.Combine(root, "global.json"))).AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        Assert.Equal(projectBytes, await File.ReadAllBytesAsync(entry));
        Assert.Equal("retained lock bytes", await File.ReadAllTextAsync(Path.Combine(root, "sunder-contracts.lock.json")));
    }

    [Fact]
    public async Task NodeUpgradeKeepsRuntimeEnginesDependenciesAndProjectMetadata()
    {
        var entry = Node(root);
        var descriptor = await File.ReadAllTextAsync(entry);
        await File.WriteAllTextAsync(Path.Combine(root, ".node-version"), "24.18.1\n");
        await File.WriteAllTextAsync(Path.Combine(root, ".tool-versions"), "python 3.14.0\nnodejs 24.18.1\n");
        var source = await new BuilderProjectDiscovery().InspectAsync(entry);
        await BuilderProjectToolchainUpgrade.UpgradeAsync(source, Tools(BuilderProjectKind.Node), default);
        Assert.Equal("24.22.1\n", await File.ReadAllTextAsync(Path.Combine(root, ".node-version")));
        Assert.Equal("python 3.14.0\nnodejs 24.22.1\n", await File.ReadAllTextAsync(Path.Combine(root, ".tool-versions")));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "package.json")));
        Assert.Equal("npm@11.19.1", json.RootElement.GetProperty("packageManager").GetString());
        Assert.Equal(">=24.18.1 <25", json.RootElement.GetProperty("engines").GetProperty("node").GetString());
        Assert.Equal("1.2.3", json.RootElement.GetProperty("dependencies").GetProperty("keep").GetString());
        Assert.Equal(descriptor, await File.ReadAllTextAsync(entry));
    }

    [Theory]
    [InlineData("runtime", "bun")]
    [InlineData("packageManager", "pnpm")]
    [InlineData("runtime", null)]
    public async Task UnsupportedDevelopmentEnginesAreNotRewritten(string key, string? name)
    {
        var entry = Node(root);
        var path = Path.Combine(root, "package.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var engine = new System.Text.Json.Nodes.JsonObject { ["name"] = name ?? "node", ["version"] = "1.0.0" };
        json["devEngines"] = new System.Text.Json.Nodes.JsonObject
            { [key] = name is null ? new System.Text.Json.Nodes.JsonArray(engine) : engine };
        await File.WriteAllTextAsync(path, json.ToJsonString());
        var bytes = await File.ReadAllBytesAsync(path);
        var source = await new BuilderProjectDiscovery().InspectAsync(entry);
        await Assert.ThrowsAsync<InvalidDataException>(() => BuilderProjectToolchainUpgrade.UpgradeAsync(source, Tools(BuilderProjectKind.Node), default));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task AnUnrelatedLocalAsdfFileDoesNotHideAnInheritedNodePin()
    {
        var child = Path.Combine(root, "child");
        var entry = Node(child);
        await File.WriteAllTextAsync(Path.Combine(root, ".tool-versions"), "nodejs 24.18.1\n");
        await File.WriteAllTextAsync(Path.Combine(child, ".tool-versions"), "python 3.14.0\n");
        var path = Path.Combine(child, "package.json");
        var bytes = await File.ReadAllBytesAsync(path);
        var source = await new BuilderProjectDiscovery().InspectAsync(entry);
        var preview = await new BuilderProjectDiscovery(Versions).InspectAsync(entry);
        Assert.True(preview.ToolchainAssessment!.RequiresUpgrade);
        Assert.False(preview.ToolchainAssessment.CanUpgrade);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BuilderProjectToolchainUpgrade.UpgradeAsync(source, Tools(BuilderProjectKind.Node), default));
        Assert.Contains("inherited toolchain pin", error.Message);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal("nodejs 24.18.1\n", await File.ReadAllTextAsync(Path.Combine(root, ".tool-versions")));
    }

    [Fact]
    public async Task ExternalPinChangesInvalidateThePreparedUpgrade()
    {
        var entry = Dotnet();
        var path = Path.Combine(root, "global.json");
        await File.WriteAllTextAsync(path, "{\"sdk\":{\"version\":\"10.0.100\"}}");
        var expected = await new BuilderProjectDiscovery().InspectAsync(entry);
        const string edited = "{\"sdk\":{\"version\":\"10.0.200\"}}";
        await File.WriteAllTextAsync(path, edited);
        await Assert.ThrowsAsync<BuilderProjectConflictException>(() => BuilderProjectToolchainUpgrade.UpgradeAsync(expected, Tools(BuilderProjectKind.Dotnet), default));
        Assert.Equal(edited, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("package.json", "{\"dependencies\":{\"keep\":\"1.0.0\"}}", "{\"dependencies\":{\"keep\":\"2.0.0\"}}")]
    [InlineData(".tool-versions", "python 3.14.0\nnodejs 24.18.1\n", "python 3.15.0\nnodejs 24.22.1\n")]
    public void RecoveryCannotChangeUnrelatedToolsOrPackageDependencies(string path, string before, string after)
        => Assert.Throws<InvalidDataException>(() => BuilderProjectToolchainUpgrade.ValidatePinChange(path,
            System.Text.Encoding.UTF8.GetBytes(before), System.Text.Encoding.UTF8.GetBytes(after)));

    private string Dotnet()
    {
        Directory.CreateDirectory(root);
        var entry = Path.Combine(root, "Package.csproj");
        File.WriteAllText(entry, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><SunderPackageProject>true</SunderPackageProject></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(root, "Package.cs"), "using Sunder.Sdk.Packaging; [assembly: SunderPackage(Id = \"test.package\", Name = \"Test\")] ");
        return entry;
    }

    private static string Node(string folder)
    {
        Directory.CreateDirectory(folder);
        var entry = Path.Combine(folder, "sunder.package.json");
        File.WriteAllText(entry, "{\"schemaVersion\":3,\"id\":\"test.package\",\"name\":\"Test\",\"version\":\"1.0.0\",\"workerCapabilities\":[],\"services\":{\"module\":\"src/services.ts\"}}");
        File.WriteAllText(Path.Combine(folder, "package.json"), "{\"name\":\"test-package\",\"packageManager\":\"npm@11.16.0\",\"engines\":{\"node\":\">=24.18.1 <25\"},\"dependencies\":{\"keep\":\"1.2.3\"}}");
        return entry;
    }

    private BuilderToolchainInstallation Tools(BuilderProjectKind kind) => new()
    {
        Kind = kind, Version = kind == BuilderProjectKind.Dotnet ? "10.0.403" : "24.22.1", NpmVersion = "11.19.1",
        RootPath = root, ExecutablePath = Path.Combine(root, "unused"),
    };

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
