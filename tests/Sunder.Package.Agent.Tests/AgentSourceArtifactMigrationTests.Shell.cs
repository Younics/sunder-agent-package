using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static readonly string[] ShellRpcSources = new[] { "ShellProviderValues.cs", "ExecutionClient.cs", "ShellExecutionBindings.cs",
        "ToolSourceProvider.cs", "PermissionSurfaceProvider.cs", "PromptContextProvider.cs" }
        .Select(file => "src/Sunder.Package.Agent.Tools.Shell/Rpc/" + file).ToArray();
    internal static readonly string[] ShellRelocationSources = [.. ShellRpcSources,
        "src/Sunder.Package.Agent.Tools.Shell/Runtime/ShellExecutionReference.cs",
        "src/Sunder.Package.Agent.Tools.Shell/ShellToolSource.Legacy.cs",
        "src/Sunder.Package.Agent.Tools.Shell/Runtime/ShellWorkerComposition.cs",
        "src/Sunder.Package.Agent.Tools.Shell/Runtime/ShellWorkerBindings.Legacy.cs",
        "src/Sunder.Package.Agent.Tools.Shell/PackageDependency.Legacy.cs"];

    private static async Task VerifyShellArtifact(string root, string core, string driver, string ownerArchive, string cache)
    {
        var canonical = await BuildCanonicalFamily(root, "Tools.Shell", "ShellWorker", cache, ownerArchive);
        var worker = canonical.Directory;
        var selected = File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json"));
        var owned = File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.owned.json"));
        AssertHeadlessRoleOutput(worker);
        await VerifyShellBehavior(worker, Path.Combine(root, "LocalWorker"));
        await canonical.Pack();
        Assert.Equal(selected, File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json")));
        Assert.Equal(owned, File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.owned.json")));
        using var archive = ZipFile.OpenRead(Path.Combine(worker, "bin/agent-source.sunderpkg"));
        await AssertArchiveHasNoCentralExecutionAssemblies(archive);
        using var stream = archive.GetEntry("manifest/sunder-package.json")!.Open();
        using var manifest = JsonDocument.Parse(stream);
        var value = manifest.RootElement;
        Assert.Equal("sunder.package.agent.tools.shell", value.GetProperty("id").GetString());
        Assert.Equal(19, value.GetProperty("contractBundles").GetArrayLength()); Assert.Equal(3, value.GetProperty("provides").GetArrayLength());
        var uses = value.GetProperty("usesContracts").EnumerateArray().ToArray();
        Assert.Equal(19, uses.Length);
        Assert.All(uses, use => { Assert.Equal("runtime", use.GetProperty("role").GetString()); Assert.False(use.GetProperty("required").GetBoolean()); });
        Assert.Equal(3, value.GetProperty("providerImports").GetArrayLength()); Assert.Single(value.GetProperty("packageApiImports").EnumerateArray());
        Assert.Single(value.GetProperty("targets").EnumerateArray());
        Assert.True(!value.TryGetProperty("platformImports", out var platforms) || platforms.GetArrayLength() == 0);
        Assert.Equal("required", Assert.Single(value.GetProperty("dependsOn").EnumerateArray()).GetProperty("presence").GetString());
        await ProjectCanonicalWorker(worker, driver);
        var workspace = Directory.CreateDirectory(Path.Combine(root, "ShellProcessWorkspace")).FullName;
        await using (var candidate = new PackageWorkerPeer(root, Path.Combine(worker, "bin/worker-projection"), workspace, value))
        { await candidate.Prepare(); await candidate.Shutdown(); }
        await using (var active = new PackageWorkerPeer(root, Path.Combine(worker, "bin/worker-projection"), workspace, value))
        {
            await active.Prepare(); await active.Activate();
            Assert.Equal("workspace-shell", (await active.Invoke("shell.tools", "tool-source", "describe", new { })).GetProperty("sourceId").GetString());
            Assert.Single((await active.Invoke("shell.permissions", "permission-surface", "describe", new { })).GetProperty("actions").EnumerateArray());
            await active.Shutdown();
        }
    }
}
