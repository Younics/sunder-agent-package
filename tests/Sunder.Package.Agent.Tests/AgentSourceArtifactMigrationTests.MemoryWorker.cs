using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static readonly string[] MemoryWorkerSources =
    [
        "src/Sunder.Package.Agent.Memory.Semantic/Runtime/MemoryWorkerComposition.cs",
        "src/Sunder.Package.Agent.Memory.Semantic/Runtime/MemoryWorkerBindings.Legacy.cs",
        "src/Sunder.Package.Agent.Memory.Semantic/PackageDependency.Legacy.cs",
        "src/Sunder.Package.Agent.Memory.Semantic/MemoryLocalStore.Composition.cs",
        "src/Sunder.Package.Agent.Memory.Semantic/Rpc/ProviderComposition.cs",
    ];

    private static async Task VerifyMemoryWorkerArtifact(string root, string core, string driver, string cache)
    {
        var canonical = await BuildCanonicalFamily(root, "Memory.Semantic", "MemoryWorker", cache, Path.Combine(root, "Worker/bin/agent-source.sunderpkg"));
        var worker = canonical.Directory;
        var selected = File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json"));
        var baseline = File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.owned.json"));
        Assert.DoesNotContain(Directory.EnumerateFiles(SingleProjectRoleOutput(worker, "app"), "*", SearchOption.AllDirectories),
            file => Path.GetFileName(file).Contains("sqlite", StringComparison.OrdinalIgnoreCase));
        using (var plan = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(worker, "obj/Debug/net10.0/sunder-analysis", RuntimeInformation.RuntimeIdentifier,
            "sunder-rpc/package-role-plan.v1.json"))))
        {
            Assert.True(plan.RootElement.GetProperty("hasApp").GetBoolean());
            var services = plan.RootElement.GetProperty("composition").GetProperty("worker").EnumerateArray().ToArray();
            const string prefix = "global::Sunder.Package.Agent.Memory.Semantic.";
            var store = Assert.Single(services, service => service.GetProperty("type").GetString() == prefix + "MemoryLocalStore");
            Assert.True(store.GetProperty("activation").GetBoolean());
            Assert.False(store.GetProperty("lifecycle").GetBoolean());
            Assert.Equal("packageContext", Assert.Single(store.GetProperty("arguments").EnumerateArray()).GetProperty("name").GetString());
            Assert.Equal(new[] { prefix + "MemoryLocalStore", prefix + "Rpc.SemanticMemoryRpcCatalog", prefix + "Services.SemanticMemoryIndexingBackgroundService" },
                services.Where(service => service.GetProperty("activation").GetBoolean() || service.GetProperty("lifecycle").GetBoolean())
                    .Select(service => service.GetProperty("type").GetString()));
        }
        await VerifyGeneratedAppView(worker, "Memory.Semantic");
        await VerifyFullMemoryApp(worker);
        await canonical.Pack();
        Assert.Equal(selected, File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json")));
        Assert.Equal(baseline, File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.owned.json")));
        var archive = Path.Combine(worker, "bin/agent-source.sunderpkg");
        using (var contents = ZipFile.OpenRead(archive))
        {
            await AssertArchiveHasNoCentralExecutionAssemblies(contents);
            Assert.Contains(contents.Entries, entry => entry.FullName.EndsWith("/assets/memory-icon.png", StringComparison.Ordinal));
            Assert.DoesNotContain(contents.Entries, entry => entry.FullName.EndsWith("/Sunder.Package.Agent.Memory.Semantic.Protocol.dll", StringComparison.Ordinal));
            foreach (var entry in contents.Entries.Where(entry => entry.FullName.EndsWith(".deps.json", StringComparison.Ordinal)))
            {
                using var reader = new StreamReader(entry.Open());
                Assert.DoesNotContain("Sunder.Package.Agent.Memory.Semantic.Protocol", await reader.ReadToEndAsync(), StringComparison.Ordinal);
            }
        }
        var extracted = Path.Combine(root, "MemoryExtracted");
        ZipFile.ExtractToDirectory(archive, extracted);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(extracted, "manifest/sunder-package.json")));
        var value = manifest.RootElement;
        Assert.Equal("sunder.package.agent.memory.semantic", value.GetProperty("id").GetString());
        Assert.Equal(20, value.GetProperty("contractBundles").GetArrayLength());
        Assert.Equal(5, value.GetProperty("provides").GetArrayLength());
        Assert.Equal(20, value.GetProperty("usesContracts").GetArrayLength());
        Assert.Equal(4, value.GetProperty("providerImports").GetArrayLength());
        var dependency = Assert.Single(value.GetProperty("dependsOn").EnumerateArray());
        Assert.Equal("sunder.package.agent", dependency.GetProperty("packageId").GetString());
        Assert.Equal(">=2.0.0 <3.0.0", dependency.GetProperty("versionRange").GetString());
        Assert.Equal("required", dependency.GetProperty("presence").GetString());
        Assert.Equal(2, value.GetProperty("targets").GetArrayLength());
        await ProjectCanonicalWorker(worker, driver);
        var runtimeRoot = Path.Combine(worker, "bin/worker-projection");
        Assert.True(File.Exists(Path.Combine(runtimeRoot, "lib/Sunder.Package.Agent.Memory.Semantic.dll")));
        Assert.True(File.Exists(Path.Combine(runtimeRoot, "lib/Sunder.Package.Agent.Memory.Semantic.runtimeconfig.json")));
        Assert.True(File.Exists(Path.Combine(runtimeRoot, "lib/Sunder.Sdk.Worker.dll")));
        await ExerciseMemoryWorkerProcess(root, runtimeRoot, value);
    }
}
