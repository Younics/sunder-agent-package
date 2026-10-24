using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyBuilderArtifact(string root, string core, string driver, string ownerArchive, string cache)
    {
        var canonical = await BuildCanonicalFamily(root, "Builder", "BuilderWorker", cache, ownerArchive);
        var worker = canonical.Directory;
        var selected = File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json"));
        using (var locked = JsonDocument.Parse(selected))
        {
            var artifact = Assert.Single(locked.RootElement.GetProperty("packageArtifacts").EnumerateObject());
            Assert.Equal("younics.ai.agent", artifact.Name);
            var api = Assert.Single(locked.RootElement.GetProperty("packageImports").EnumerateObject());
            Assert.Equal("younics.ai.agent", api.Name);
            Assert.Equal("optional", api.Value.GetProperty("presence").GetString());
            Assert.Empty(api.Value.GetProperty("requiredContracts").EnumerateArray());
            var providers = locked.RootElement.GetProperty("providerImports").EnumerateObject().ToArray();
            Assert.Equal(new[] { "permissions", "tools" }, providers.Select(provider => provider.Name).Order(StringComparer.Ordinal));
            Assert.All(providers, provider => Assert.Equal("optional", provider.Value.GetProperty("presence").GetString()));
            Assert.Empty(locked.RootElement.GetProperty("platformImports").EnumerateObject());
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
        Assert.Equal(19, value.GetProperty("contractBundles").GetArrayLength());
        Assert.Equal(new[] { "builder.runtime", "maker.permissions", "maker.tools" },
            value.GetProperty("provides").EnumerateArray().Select(provider => provider.GetProperty("providerId").GetString()).Order(StringComparer.Ordinal));
        var uses = value.GetProperty("usesContracts").EnumerateArray().ToArray();
        var use = Assert.Single(uses, entry => entry.GetProperty("role").GetString() == "app");
        Assert.Equal("app", use.GetProperty("role").GetString()); Assert.True(use.GetProperty("required").GetBoolean());
        Assert.Equal("younics.sunder.package.maker.runtime", use.GetProperty("contractId").GetString());
        var runtimeUses = uses.Where(entry => entry.GetProperty("role").GetString() == "runtime").ToArray();
        Assert.Equal(19, uses.Length);
        Assert.Equal(CurrentPublicRpcDescriptors.ContractIds.Order(StringComparer.Ordinal),
            runtimeUses.Select(entry => entry.GetProperty("contractId").GetString()).Order(StringComparer.Ordinal));
        Assert.All(runtimeUses, entry => Assert.False(entry.GetProperty("required").GetBoolean()));
        var agentDependency = Assert.Single(value.GetProperty("dependsOn").EnumerateArray());
        Assert.Equal("younics.ai.agent", agentDependency.GetProperty("packageId").GetString());
        Assert.Equal(">=2.0.0 <3.0.0", agentDependency.GetProperty("versionRange").GetString());
        Assert.Equal("optional", agentDependency.GetProperty("presence").GetString());
        var import = Assert.Single(value.GetProperty("packageApiImports").EnumerateArray());
        Assert.Equal("younics.ai.agent", import.GetProperty("packageId").GetString());
        Assert.Equal("optional", import.GetProperty("presence").GetString());
        Assert.Equal(2, value.GetProperty("targets").GetArrayLength());
        var providerImports = value.GetProperty("providerImports").EnumerateArray().ToArray();
        Assert.Equal(new[] { "younics.ai.agent.permission.surface", "younics.ai.agent.tool.source" },
            providerImports.Select(provider => provider.GetProperty("contractId").GetString()).Order(StringComparer.Ordinal));
        Assert.All(providerImports, provider => Assert.Equal("optional", provider.GetProperty("presence").GetString()));
        Assert.True(!value.TryGetProperty("platformImports", out var platforms) || platforms.GetArrayLength() == 0);
        await ProjectCanonicalWorker(worker, driver);
        var workspace = Directory.CreateDirectory(Path.Combine(root, "BuilderProcessWorkspace")).FullName;
        await using (var candidate = new PackageWorkerPeer(root, Path.Combine(worker, "bin/worker-projection"), workspace, value))
        { await candidate.Prepare(); await candidate.Shutdown(); }
        await using (var active = new PackageWorkerPeer(root, Path.Combine(worker, "bin/worker-projection"), workspace, value))
        { await active.Prepare(); await active.Activate(); await active.Shutdown(); }
    }
}
