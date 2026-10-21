using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyBuilderArtifact(string root, string core, string driver, string ownerArchive, string cache)
    {
        var canonical = await BuildCanonicalFamily(root, "Builder", "BuilderWorker", cache);
        var worker = canonical.Directory;
        var selected = File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json"));
        using (var locked = JsonDocument.Parse(selected))
        {
            foreach (var section in new[] { "packageArtifacts", "packageImports", "providerImports", "platformImports" })
                Assert.Empty(locked.RootElement.GetProperty(section).EnumerateObject());
        }
        await VerifyGeneratedAppView(worker, "Builder");
        await VerifyBuilderRoles(worker);
        await canonical.Pack();
        Assert.Equal(selected, File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json")));
        using var archive = ZipFile.OpenRead(Path.Combine(worker, "bin/agent-source.sunderpkg"));
        await AssertArchiveHasNoCentralExecutionAssemblies(archive);
        Assert.Contains(archive.Entries, entry => entry.FullName.EndsWith("/assets/builder-icon.png", StringComparison.Ordinal));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("/Sunder.Package.Agent.Builder.Protocol.dll", StringComparison.Ordinal));
        foreach (var dependency in new[] { "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll" })
            Assert.Contains(archive.Entries, entry => entry.FullName.StartsWith("payload/runtime/", StringComparison.Ordinal) && entry.FullName.EndsWith("/" + dependency, StringComparison.Ordinal));
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".deps.json", StringComparison.Ordinal)))
        { using var reader = new StreamReader(entry.Open()); Assert.DoesNotContain("Sunder.Package.Agent.Builder.Protocol", await reader.ReadToEndAsync(), StringComparison.Ordinal); }
        using var manifestStream = archive.GetEntry("manifest/sunder-package.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var value = manifest.RootElement;
        Assert.Equal("younics.sunder.package.maker", value.GetProperty("id").GetString());
        Assert.Single(value.GetProperty("contractBundles").EnumerateArray()); Assert.Single(value.GetProperty("provides").EnumerateArray());
        var use = Assert.Single(value.GetProperty("usesContracts").EnumerateArray());
        Assert.Equal("app", use.GetProperty("role").GetString()); Assert.True(use.GetProperty("required").GetBoolean());
        Assert.Equal("younics.sunder.package.maker.runtime", use.GetProperty("contractId").GetString());
        Assert.Empty(value.GetProperty("dependsOn").EnumerateArray());
        Assert.True(!value.TryGetProperty("packageApiImports", out var imports) || imports.GetArrayLength() == 0);
        Assert.Equal(2, value.GetProperty("targets").GetArrayLength());
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
