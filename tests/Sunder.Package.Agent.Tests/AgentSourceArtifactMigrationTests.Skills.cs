using System.IO.Compression;
using System.Text.Json;
using Sunder.Sdk.Stacks;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static readonly string[] SkillsModelSources =
    [
        "src/Sunder.Package.Agent.Skills/Services/InstalledSkillRecord.cs",
        "src/Sunder.Package.Agent.Skills/Runtime/SkillRuntimeAbstractions.cs",
        "src/Sunder.Package.Agent.Skills/Runtime/SkillRuntimeContracts.cs",
    ];
    internal static readonly string[] SkillsProviderSources = new[]
    {
        "SkillsProviderValues.cs", "SelectableCapabilityProvider.cs", "PromptContextProvider.cs", "ToolSourceProvider.cs", "SkillStackProvider.cs",
    }.Select(file => "src/Sunder.Package.Agent.Skills/Rpc/" + file).ToArray();
    internal static readonly string[] SkillsRelocationSources = [.. SkillsModelSources, .. SkillsProviderSources,
        "src/Sunder.Package.Agent.Skills/Runtime/SkillsActivation.cs",
        "src/Sunder.Package.Agent.Skills/Rpc/ProviderComposition.cs",
        "src/Sunder.Package.Agent.Skills/Rpc/AppComposition.cs",
        "src/Sunder.Package.Agent.Skills/Runtime/SkillOwnerRpc.Legacy.cs", "src/Sunder.Package.Agent.Skills/Runtime/SkillsWorkerComposition.cs",
        "src/Sunder.Package.Agent.Skills/Runtime/SkillsWorkerBindings.Legacy.cs",
        "src/Sunder.Package.Agent.Skills/PackageDependency.Legacy.cs"];
    internal static readonly string[] SkillsConsumerSources =
    [
        "src/Sunder.Package.Agent.Skills/Runtime/ISkillsRuntimeBindings.cs",
        "src/Sunder.Package.Agent.Skills/Runtime/LegacySkillsRuntimeBindings.cs",
        "src/Sunder.Package.Agent.Skills/Services/SkillsFeature.Legacy.cs",
        "src/Sunder.Package.Agent.Skills/Rpc/SkillsRuntimeBindings.cs",
    ];

    private static async Task VerifySkillsArtifact(string root, string core, string driver, string ownerArchive, string cache)
    {
        var canonical = await BuildCanonicalFamily(root, "Skills", "SkillsWorker", cache, ownerArchive);
        var worker = canonical.Directory;
        var selected = File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json"));
        await VerifyGeneratedAppView(worker, "Skills");
        await VerifySkillsRoles(worker);
        await canonical.Pack();
        Assert.Equal(selected, File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json")));
        using var archive = ZipFile.OpenRead(Path.Combine(worker, "bin/agent-source.sunderpkg"));
        await AssertArchiveHasNoCentralExecutionAssemblies(archive);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("/Sunder.Package.Agent.Skills.Protocol.dll", StringComparison.Ordinal));
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".deps.json", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(entry.Open());
            Assert.DoesNotContain("Sunder.Package.Agent.Skills.Protocol", await reader.ReadToEndAsync(), StringComparison.Ordinal);
        }
        using var manifestStream = archive.GetEntry("manifest/sunder-package.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var value = manifest.RootElement;
        Assert.Equal("sunder.package.agent.skills", value.GetProperty("id").GetString());
        Assert.Equal(21, value.GetProperty("contractBundles").GetArrayLength());
        Assert.Equal(5, value.GetProperty("provides").GetArrayLength());
        Assert.Equal(20, value.GetProperty("usesContracts").GetArrayLength());
        Assert.Single(value.GetProperty("platformImports").EnumerateArray());
        Assert.Equal(3, value.GetProperty("providerImports").GetArrayLength());
        var dependency = Assert.Single(value.GetProperty("dependsOn").EnumerateArray());
        Assert.Equal("sunder.package.agent", dependency.GetProperty("packageId").GetString());
        Assert.Equal("required", dependency.GetProperty("presence").GetString());
        await ProjectCanonicalWorker(worker, driver);
        await VerifySkillsProcess(root, Path.Combine(worker, "bin/worker-projection"), value);
    }

    private static async Task VerifySkillsProcess(string root, string projected, JsonElement manifest)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(root, "SkillsProcessWorkspace")).FullName;
        var staging = Directory.CreateDirectory(Path.Combine(workspace, "skill-transfer")).FullName;
        var orphan = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".zip");
        await File.WriteAllBytesAsync(orphan, [1, 2, 3]);
        await using (var candidate = new PackageWorkerPeer(root, projected, workspace, manifest))
        {
            await candidate.Prepare();
            Assert.Equal(JsonValueKind.Null, candidate.Contributions.GetProperty("settingsSchema").ValueKind);
            Assert.True(File.Exists(orphan));
            Assert.False(Directory.Exists(Path.Combine(workspace, "skills")));
            await candidate.Shutdown();
        }
        await using (var active = new PackageWorkerPeer(root, projected, workspace, manifest))
        {
            await active.Prepare();
            await active.Activate();
            Assert.False(File.Exists(orphan));
            Assert.True(Directory.Exists(Path.Combine(workspace, "skills")));
            var description = await active.Invoke("skills.selectable.capabilities", "selectable-capability-provider", "describe", new { });
            Assert.Equal("sunder.package.agent.skills", description.GetProperty("id").GetString());
            var stack = await active.Invoke("skills.stack", "stack-contributor", "metadata", new { });
            Assert.True(stack.GetProperty("supportsExport").GetBoolean());
            Assert.True(stack.GetProperty("supportsImport").GetBoolean());
            await active.Shutdown();
        }
    }
}
