using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Sunder.Sdk.Stacks;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static readonly string[] DockerModelSources =
    [
        "src/Sunder.Package.Agent.Execution.Docker/Models/DockerExecutionPresentationModels.cs",
        "src/Sunder.Package.Agent.Execution.Docker/Runtime/DockerExecutionPresentationDefaults.cs",
    ];
    internal static readonly string[] DockerProviderSources = new[] { "ExecutionValues.cs", "ExecutionTargetProvider.cs", "WorkspaceEditorProvider.cs", "WorkspacePathMigratorProvider.cs", "DockerStackProvider.cs" }
        .Select(file => "src/Sunder.Package.Agent.Execution.Docker/Rpc/" + file).ToArray();
    internal static readonly string[] DockerRelocationSources = [.. DockerModelSources, .. DockerProviderSources,
        "src/Sunder.Package.Agent.Execution.Docker/Rpc/ProviderComposition.cs",
        "src/Sunder.Package.Agent.Execution.Docker/Runtime/DockerWorkerConfiguration.cs", "src/Sunder.Package.Agent.Execution.Docker/Runtime/DockerWorkerBindings.Legacy.cs",
        "src/Sunder.Package.Agent.Execution.Docker/Rpc/DockerWorkerBindings.cs", "src/Sunder.Package.Agent.Execution.Docker/App/DockerAppBindings.Legacy.cs",
        "src/Sunder.Package.Agent.Execution.Docker/Rpc/AppComposition.cs", "src/Sunder.Package.Agent.Execution.Docker/PackageDependency.Legacy.cs"];

    private static async Task VerifyDockerArtifact(string root, string core, string driver, string ownerArchive, string cache)
    {
        var canonical = await BuildCanonicalFamily(root, "Execution.Docker", "DockerWorker", cache, ownerArchive);
        var worker = canonical.Directory;
        var selected = File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json"));
        await VerifyGeneratedAppView(worker, "Execution.Docker");
        await VerifyDockerRoles(root, worker);
        await canonical.Pack();
        Assert.Equal(selected, File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json")));
        using var archive = ZipFile.OpenRead(Path.Combine(worker, "bin/agent-source.sunderpkg"));
        await AssertArchiveHasNoCentralExecutionAssemblies(archive);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("/Sunder.Package.Agent.Execution.Docker.Protocol.dll", StringComparison.Ordinal));
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".deps.json", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(entry.Open());
            Assert.DoesNotContain("Sunder.Package.Agent.Execution.Docker.Protocol", await reader.ReadToEndAsync(), StringComparison.Ordinal);
        }
        using var manifestStream = archive.GetEntry("manifest/sunder-package.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var value = manifest.RootElement;
        Assert.Equal(5, value.GetProperty("contractBundles").GetArrayLength()); Assert.Equal(5, value.GetProperty("provides").GetArrayLength());
        var use = Assert.Single(value.GetProperty("usesContracts").EnumerateArray());
        Assert.Equal("app", use.GetProperty("role").GetString()); Assert.True(use.GetProperty("required").GetBoolean());
        Assert.Equal(3, value.GetProperty("providerImports").GetArrayLength()); Assert.Single(value.GetProperty("platformImports").EnumerateArray());
        Assert.True(!value.TryGetProperty("packageApiImports", out var apis) || apis.GetArrayLength() == 0);
        Assert.Equal("required", Assert.Single(value.GetProperty("dependsOn").EnumerateArray()).GetProperty("presence").GetString());
        await ProjectCanonicalWorker(worker, driver);
        var workspace = Directory.CreateDirectory(Path.Combine(root, "DockerProcessWorkspace")).FullName;
        await using (var candidate = new PackageWorkerPeer(root, Path.Combine(worker, "bin/worker-projection"), workspace, value))
        { await candidate.Prepare(); await candidate.Shutdown(); }
        await using (var active = new PackageWorkerPeer(root, Path.Combine(worker, "bin/worker-projection"), workspace, value))
        {
            await active.Prepare(); await active.Activate();
            Assert.True(active.StateReads >= 2);
            var descriptor = await active.Invoke("docker.execution.target", "execution-target", "describe", new { });
            Assert.Equal(8, descriptor.GetProperty("facets").GetArrayLength());
            Assert.DoesNotContain(descriptor.GetProperty("facets").EnumerateArray(), item => item.GetString() == "resource-resolution");
            Assert.True((await active.Invoke("docker.stack", "stack-contributor", "metadata", new { })).GetProperty("supportsImport").GetBoolean());
            await active.Shutdown();
        }
    }
}
