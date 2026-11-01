using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static readonly string[] LocalModelSources =
    [
        "src/Sunder.Package.Agent.Execution.Local/Models/LocalExecutionPresentationModels.cs",
        "src/Sunder.Package.Agent.Execution.Local/Runtime/LocalExecutionPresentationDefaults.cs",
    ];
    internal static readonly string[] LocalProviderSources = new[] { "ExecutionValues.cs", "ExecutionTargetProvider.cs", "WorkspaceEditorProvider.cs" }
        .Select(file => "src/Sunder.Package.Agent.Execution.Local/Rpc/" + file).ToArray();
    internal static readonly string[] LocalRelocationSources = [.. LocalModelSources, .. LocalProviderSources,
        "src/Sunder.Package.Agent.Execution.Local/Runtime/LocalWorkerConfiguration.cs",
        "src/Sunder.Package.Agent.Execution.Local/Runtime/LocalWorkerBindings.Legacy.cs",
        "src/Sunder.Package.Agent.Execution.Local/App/LocalAppBindings.Legacy.cs",
        "src/Sunder.Package.Agent.Execution.Local/Rpc/AppComposition.cs",
        "src/Sunder.Package.Agent.Execution.Local/Rpc/ProviderComposition.cs",
        "src/Sunder.Package.Agent.Execution.Local/PackageDependency.Legacy.cs"];

    private static async Task VerifyLocalArtifact(string root, string core, string driver, string ownerArchive, string cache)
    {
        var canonical = await BuildCanonicalFamily(root, "Execution.Local", "LocalWorker", cache, ownerArchive);
        var worker = canonical.Directory;
        var selected = File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json"));
        await VerifyGeneratedAppView(worker, "Execution.Local");
        await VerifyLocalRoles(worker);
        await canonical.Pack();
        Assert.Equal(selected, File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json")));
        using var archive = ZipFile.OpenRead(Path.Combine(worker, "bin/agent-source.sunderpkg"));
        await AssertArchiveHasNoCentralExecutionAssemblies(archive);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("/Sunder.Package.Agent.Execution.Local.Protocol.dll", StringComparison.Ordinal));
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".deps.json", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(entry.Open());
            Assert.DoesNotContain("Sunder.Package.Agent.Execution.Local.Protocol", await reader.ReadToEndAsync(), StringComparison.Ordinal);
        }
        using var manifestStream = archive.GetEntry("manifest/sunder-package.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var value = manifest.RootElement;
        Assert.Equal("younics.ai.agent.execution.local", value.GetProperty("id").GetString());
        Assert.Equal(3, value.GetProperty("contractBundles").GetArrayLength());
        Assert.Equal(3, value.GetProperty("provides").GetArrayLength());
        var use = Assert.Single(value.GetProperty("usesContracts").EnumerateArray());
        Assert.Equal("app", use.GetProperty("role").GetString()); Assert.True(use.GetProperty("required").GetBoolean());
        Assert.Equal(2, value.GetProperty("providerImports").GetArrayLength());
        Assert.True(!value.TryGetProperty("packageApiImports", out var apis) || apis.GetArrayLength() == 0);
        Assert.True(!value.TryGetProperty("platformImports", out var platforms) || platforms.GetArrayLength() == 0);
        Assert.Equal("required", Assert.Single(value.GetProperty("dependsOn").EnumerateArray()).GetProperty("presence").GetString());
        await ProjectCanonicalWorker(worker, driver);
        var workspace = Directory.CreateDirectory(Path.Combine(root, "LocalProcessWorkspace")).FullName;
        await using (var candidate = new PackageWorkerPeer(root, Path.Combine(worker, "bin/worker-projection"), workspace, value))
        { await candidate.Prepare(); await candidate.Shutdown(); }
        await using (var active = new PackageWorkerPeer(root, Path.Combine(worker, "bin/worker-projection"), workspace, value))
        {
            await active.Prepare();
            Assert.Equal(JsonValueKind.Object, active.Contributions.GetProperty("settingsSchema").ValueKind);
            await active.Activate();
            var descriptor = await active.Invoke("local.execution.target", "execution-target", "describe", new { });
            Assert.Equal("local", descriptor.GetProperty("targetId").GetString());
            Assert.Equal(9, descriptor.GetProperty("facets").GetArrayLength());
            Assert.Equal("younics.ai.agent.execution.local.workspace-editor", (await active.Invoke("local.workspace.editor", "workspace-editor", "describe", new { })).GetProperty("id").GetString());
            await active.Shutdown();
        }
    }
}
