using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static readonly string[] BuilderRelocationSources =
    [
        "src/Sunder.Package.Agent.Builder/Runtime/BuilderRpcContracts.cs",
        "src/Sunder.Package.Agent.Builder/App/Runtime/BuilderRpcExecutionTargetProxy.cs",
        "src/Sunder.Package.Agent.Builder/Runtime/BuilderRuntimeOperations.cs",
        "src/Sunder.Package.Agent.Builder/Rpc/BuilderRuntimeService.Legacy.cs",
        "src/Sunder.Package.Agent.Builder/App/BuilderWorkspaceExecutionService.Legacy.cs",
        "src/Sunder.Package.Agent.Builder/Rpc/AppComposition.cs",
        "src/Sunder.Package.Agent.Builder/Runtime/BuilderWorkerComposition.cs",
        "src/Sunder.Package.Agent.Builder/Runtime/BuilderWorkerBindings.Legacy.cs",
        "src/Sunder.Package.Agent.Builder/Rpc/BuilderExecutionValues.cs",
        "src/Sunder.Package.Agent.Builder/Rpc/BuilderExecutionBindings.cs",
        "src/Sunder.Package.Agent.Builder/PackageDependency.Legacy.cs",
    ];

    private static async Task VerifyBuilderArtifact(string root, string core, string driver, string ownerArchive, string cache)
    {
        var canonical = await BuildCanonicalFamily(root, "Builder", "BuilderWorker", cache, ownerArchive);
        var worker = canonical.Directory;
        var selected = File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json"));
        using (var locked = JsonDocument.Parse(selected))
        {
            var import = locked.RootElement.GetProperty("packageImports").GetProperty("sunder.package.agent");
            Assert.Equal("required", import.GetProperty("presence").GetString());
            Assert.Equal("sunder.agent.workspace.execution.resolver", Assert.Single(import.GetProperty("requiredContracts").EnumerateArray()).GetString());
        }
        await VerifyGeneratedAppView(worker, "Builder");
        await VerifyBuilderRoles(root, worker);
        await canonical.Pack();
        Assert.Equal(selected, File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json")));
        using var archive = ZipFile.OpenRead(Path.Combine(worker, "bin/agent-source.sunderpkg"));
        await AssertArchiveHasNoCentralExecutionAssemblies(archive);
        Assert.Contains(archive.Entries, entry => entry.FullName.EndsWith("/assets/builder-icon.png", StringComparison.Ordinal));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("/Sunder.Package.Agent.Builder.Protocol.dll", StringComparison.Ordinal));
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".deps.json", StringComparison.Ordinal)))
        { using var reader = new StreamReader(entry.Open()); Assert.DoesNotContain("Sunder.Package.Agent.Builder.Protocol", await reader.ReadToEndAsync(), StringComparison.Ordinal); }
        using var manifestStream = archive.GetEntry("manifest/sunder-package.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var value = manifest.RootElement;
        Assert.Equal("sunder.package.agent.builder", value.GetProperty("id").GetString());
        Assert.Equal(20, value.GetProperty("contractBundles").GetArrayLength()); Assert.Single(value.GetProperty("provides").EnumerateArray());
        var uses = value.GetProperty("usesContracts").EnumerateArray().ToArray(); Assert.Equal(20, uses.Length);
        Assert.True(Assert.Single(uses, use => use.GetProperty("role").GetString() == "app").GetProperty("required").GetBoolean());
        Assert.Equal("sunder.agent.workspace.execution.resolver", Assert.Single(uses,
            use => use.GetProperty("role").GetString() == "runtime" && use.GetProperty("required").GetBoolean()).GetProperty("contractId").GetString());
        Assert.Single(value.GetProperty("packageApiImports").EnumerateArray()); Assert.Equal(2, value.GetProperty("targets").GetArrayLength());
        Assert.True(!value.TryGetProperty("providerImports", out var providers) || providers.GetArrayLength() == 0);
        Assert.True(!value.TryGetProperty("platformImports", out var platforms) || platforms.GetArrayLength() == 0);
        await ProjectCanonicalWorker(worker, driver);
        var workspace = Directory.CreateDirectory(Path.Combine(root, "BuilderProcessWorkspace")).FullName;
        await using (var candidate = new PackageWorkerPeer(root, Path.Combine(worker, "bin/worker-projection"), workspace, value))
        { await candidate.Prepare(); await candidate.Shutdown(); }
        await using (var active = new PackageWorkerPeer(root, Path.Combine(worker, "bin/worker-projection"), workspace, value))
        { await active.Prepare(); await active.Activate(); await active.Shutdown(); }
    }
}
