using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    [AgentPublicSourceMigrationFact]
    public async Task CompleteOwnerArtifact_DerivesConsumerAndProviderLocksFromFreshSourcePayload()
    {
        var repository = BuilderSourceMigrationTests.FindRepository();
        var core = Path.GetFullPath(Path.Combine(repository, "..", "sunder-core"));
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var driver = Path.Combine(core, "languages/csharp/dotnet/tests/Sunder.Package.Build.Tests/bin", configuration, "net10.0/Sunder.Package.Build.Tests.dll");
        Assert.True(File.Exists(driver), "Build Core Package.Build.Tests before the required artifact source lane.");
        var root = Directory.CreateTempSubdirectory("sunder-agent-source-artifact-").FullName;
        try
        {
            var cache = Path.Combine(root, "fixture-cache");
            var canonical = await BuildCanonicalFamily(root, "Agent", "Worker", cache);
            var worker = canonical.Directory;
            var selected = File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json"));
            var owned = File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.owned.json"));
            await VerifyGeneratedAppView(worker, "Agent");
            await VerifyOwnerAppRole(worker);
            var archive = Path.Combine(worker, "bin/agent-source.sunderpkg");
            // Pack independently rechecks the actual canonical source and selections.
            await canonical.Pack();
            Assert.True(File.Exists(archive));
            var before = File.ReadAllBytes(archive);
            VerifyArchive(archive);
            await ProjectCanonicalWorker(worker, driver);
            await VerifyOwnerProcess(root, worker, archive);
            await VerifyImports(root, core, driver, archive, cache);
            await VerifyAgentWorkspaceAndPermissionConsumers(root);
            VerifyFamilyPolicies(root, repository, core);
            await canonical.Pack();
            Assert.Equal(before, File.ReadAllBytes(archive));
            Assert.Equal(selected, File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json")));
            Assert.Equal(owned, File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.owned.json")));
            Assert.True(Directory.Exists(Path.Combine(worker, "bin/Debug/net10.0/sunder-dev")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void VerifyArchive(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        using var manifestStream = Assert.Single(archive.Entries, entry => entry.FullName == "manifest/sunder-package.json").Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var root = manifest.RootElement;
        Assert.Equal("sunder.package.agent", root.GetProperty("id").GetString());
        Assert.Equal(21, root.GetProperty("contractBundles").GetArrayLength());
        Assert.Equal(11, root.GetProperty("provides").GetArrayLength());
        Assert.Equal(19, root.GetProperty("usesContracts").GetArrayLength());
        Assert.Equal(2, root.GetProperty("targets").GetArrayLength());
        Assert.Contains(archive.Entries, entry => entry.FullName.EndsWith("/Sunder.Package.Agent.dll", StringComparison.Ordinal));
        Assert.Contains(archive.Entries, entry => entry.FullName.EndsWith("/Sunder.Package.Agent.App.dll", StringComparison.Ordinal));
        var appAssembly = Assert.Single(archive.Entries, entry => entry.FullName.EndsWith("/Sunder.Package.Agent.App.dll", StringComparison.Ordinal));
        var appDirectory = appAssembly.FullName[..appAssembly.FullName.LastIndexOf('/')];
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName == appDirectory + "/Sunder.Package.Agent.Protocol.dll");
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("/Sunder.Package.Agent.OwnerProtocol.dll", StringComparison.Ordinal)
            || entry.FullName.EndsWith("/Sunder.Package.Agent.Protocol.dll", StringComparison.Ordinal));
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".deps.json", StringComparison.Ordinal)))
        {
            using var dependencies = new StreamReader(entry.Open());
            var text = dependencies.ReadToEnd();
            Assert.DoesNotContain("Sunder.Package.Agent.OwnerProtocol", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Sunder.Package.Agent.Protocol", text, StringComparison.Ordinal);
        }
        Assert.Contains(archive.Entries, entry => entry.FullName.Contains("sqlite", StringComparison.OrdinalIgnoreCase) && entry.FullName.EndsWith(OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so", StringComparison.Ordinal));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("/Sunder.Package.Build.dll", StringComparison.Ordinal));
        // Ordinary Debug App composition includes its own managed symbols;
        // Worker publication strips symbols from every runtime payload.
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith("payload/runtime/", StringComparison.Ordinal)
            && (entry.FullName.EndsWith(".pdb", StringComparison.Ordinal) || entry.FullName.EndsWith(".mdb", StringComparison.Ordinal)));
        Assert.All(archive.Entries.Where(entry => entry.FullName.EndsWith(".pdb", StringComparison.Ordinal)), entry =>
            Assert.Equal(appAssembly.FullName[..^4] + ".pdb", entry.FullName));
    }

    private static Task Run(string root, params string[] arguments) => BuilderSourceMigrationTests.Run(root, arguments);
    private static string Escape(string value) => System.Security.SecurityElement.Escape(value)!;
}
