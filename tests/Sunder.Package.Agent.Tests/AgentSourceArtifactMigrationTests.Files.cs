using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static readonly string[] FilesRpcSources = new[] { "FilesExecutionFacets.cs", "FilesProviderValues.cs", "ExecutionValues.cs", "ExecutionClient.cs", "FilesExecutionBindings.cs",
        "ToolSourceProvider.cs", "PermissionSurfaceProvider.cs", "PromptContextProvider.cs", "SessionCleanerProvider.cs" }
        .Select(file => "src/Sunder.Package.Agent.Tools.Files/Rpc/" + file).ToArray();
    internal static readonly string[] FilesRelocationSources = [.. FilesRpcSources,
        "src/Sunder.Package.Agent.Tools.Files/Runtime/FileExecutionReference.cs",
        "src/Sunder.Package.Agent.Tools.Files/FilesToolSource.Legacy.cs",
        "src/Sunder.Package.Agent.Tools.Files/Runtime/FilesExecutionFacets.Legacy.cs",
        "src/Sunder.Package.Agent.Tools.Files/Runtime/FilesWorkerComposition.cs",
        "src/Sunder.Package.Agent.Tools.Files/Runtime/FilesWorkerBindings.Legacy.cs",
        "src/Sunder.Package.Agent.Tools.Files/PackageDependency.Legacy.cs"];

    private static async Task VerifyFilesArtifact(string root, string core, string driver, string ownerArchive, string cache)
    {
        var canonical = await BuildCanonicalFamily(root, "Tools.Files", "FilesWorker", cache, ownerArchive);
        var worker = canonical.Directory;
        var selected = File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json"));
        var owned = CanonicalPackageRoles.ReadOwnedBaseline(worker);
        AssertHeadlessRoleOutput(worker);
        await VerifyFilesBehavior(worker, Path.Combine(root, "LocalWorker"));
        await canonical.Pack();
        Assert.Equal(selected, File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json")));
        Assert.Equal(owned, CanonicalPackageRoles.ReadOwnedBaseline(worker));
        using var archive = ZipFile.OpenRead(Path.Combine(worker, "bin/agent-source.sunderpkg"));
        await AssertArchiveHasNoCentralExecutionAssemblies(archive);
        using var stream = archive.GetEntry("manifest/sunder-package.json")!.Open();
        using var manifest = JsonDocument.Parse(stream);
        var value = manifest.RootElement;
        Assert.Equal("younics.ai.agent.tools.files", value.GetProperty("id").GetString());
        Assert.Equal(18, value.GetProperty("contractBundles").GetArrayLength()); Assert.Equal(4, value.GetProperty("provides").GetArrayLength());
        var uses = value.GetProperty("usesContracts").EnumerateArray().ToArray();
        Assert.Equal(18, uses.Length);
        Assert.All(uses, use => { Assert.Equal("runtime", use.GetProperty("role").GetString()); Assert.False(use.GetProperty("required").GetBoolean()); });
        Assert.Single(uses, use => use.GetProperty("contractId").GetString() == "younics.ai.agent.execution.target");
        Assert.Equal(4, value.GetProperty("providerImports").GetArrayLength());
        Assert.Single(value.GetProperty("packageApiImports").EnumerateArray());
        Assert.Single(value.GetProperty("targets").EnumerateArray());
        Assert.True(!value.TryGetProperty("platformImports", out var platforms) || platforms.GetArrayLength() == 0);
        Assert.Equal("required", Assert.Single(value.GetProperty("dependsOn").EnumerateArray()).GetProperty("presence").GetString());
        await ProjectCanonicalWorker(worker, driver);
        var workspace = Directory.CreateDirectory(Path.Combine(root, "FilesProcessWorkspace")).FullName;
        await using (var candidate = new PackageWorkerPeer(root, Path.Combine(worker, "bin/worker-projection"), workspace, value))
        { await candidate.Prepare(); await candidate.Shutdown(); }
        await using (var active = new PackageWorkerPeer(root, Path.Combine(worker, "bin/worker-projection"), workspace, value))
        {
            await active.Prepare(); await active.Activate();
            Assert.Equal("workspace-files", (await active.Invoke("files.tools", "tool-source", "describe", new { })).GetProperty("sourceId").GetString());
            Assert.Equal(3, (await active.Invoke("files.permissions", "permission-surface", "describe", new { })).GetProperty("actions").GetArrayLength());
            await active.Shutdown();
        }
    }
}
