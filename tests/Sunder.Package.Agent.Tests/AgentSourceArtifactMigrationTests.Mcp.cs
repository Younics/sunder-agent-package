using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Sunder.Sdk.Stacks;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static readonly string[] McpProviderSources = new[]
    { "McpProviderValues.cs", "ToolSourceProvider.cs", "ObservedExecutionProvider.cs", "SelectableCapabilityProvider.cs", "SessionCleanerProvider.cs", "McpStackProvider.cs", "McpWorkerBindings.cs" }
        .Select(file => "src/Sunder.Package.Agent.Mcp/Rpc/" + file).ToArray();
    internal static readonly string[] McpRelocationSources = [.. McpProviderSources,
        "src/Sunder.Package.Agent.Mcp/Rpc/AppComposition.cs",
        "src/Sunder.Package.Agent.Mcp/Worker/McpActivation.cs", "src/Sunder.Package.Agent.Mcp/Rpc/ProviderComposition.cs",
        "src/Sunder.Package.Agent.Mcp/Runtime/McpManagementModels.cs", "src/Sunder.Package.Agent.Mcp/Runtime/McpManagementPayload.cs",
        "src/Sunder.Package.Agent.Mcp/Runtime/McpManagementContent.cs", "src/Sunder.Package.Agent.Mcp/Services/McpManagementServiceModels.cs",
        "src/Sunder.Package.Agent.Mcp/Services/McpServerName.cs", "src/Sunder.Package.Agent.Mcp/Services/McpOAuthConstants.cs",
        "src/Sunder.Package.Agent.Mcp/Runtime/McpOwnerManagementRpcClient.Legacy.cs", "src/Sunder.Package.Agent.Mcp/Worker/McpWorkerBindings.Legacy.cs",
        "src/Sunder.Package.Agent.Mcp/PackageDependency.Legacy.cs", "src/Sunder.Package.Agent.Mcp/Runtime/McpManagementRpcProvider.Legacy.cs"];
    internal static readonly string[] McpConsumerSources =
    [
        "src/Sunder.Package.Agent.Mcp/Runtime/IMcpWorkspaceCatalog.cs",
        "src/Sunder.Package.Agent.Mcp/Runtime/LegacyMcpWorkspaceCatalog.cs",
        "src/Sunder.Package.Agent.Mcp/Services/McpSunderConfigurationSyncService.Legacy.cs",
        "src/Sunder.Package.Agent.Mcp/Rpc/RuntimeWorkspaceCatalog.cs",
    ];

    private static async Task VerifyMcpArtifact(string root, string core, string driver, string ownerArchive, string cache)
    {
        var canonical = await BuildCanonicalFamily(root, "Mcp", "McpWorker", cache, ownerArchive);
        var worker = canonical.Directory;
        var selected = File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json"));
        await VerifyGeneratedAppView(worker, "Mcp");
        await VerifyMcpRoles(worker);
        await canonical.Pack();
        Assert.Equal(selected, File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json")));
        using var archive = ZipFile.OpenRead(Path.Combine(worker, "bin/agent-source.sunderpkg"));
        await AssertArchiveHasNoCentralExecutionAssemblies(archive);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("/Sunder.Package.Agent.Mcp.Shared.dll", StringComparison.Ordinal));
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".deps.json", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(entry.Open());
            Assert.DoesNotContain("Sunder.Package.Agent.Mcp.Shared", await reader.ReadToEndAsync(), StringComparison.Ordinal);
        }
        using var manifestStream = archive.GetEntry("manifest/sunder-package.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var value = manifest.RootElement;
        Assert.Equal("sunder.package.agent.mcp", value.GetProperty("id").GetString());
        Assert.Equal(21, value.GetProperty("contractBundles").GetArrayLength());
        Assert.Equal(6, value.GetProperty("provides").GetArrayLength());
        Assert.Equal(20, value.GetProperty("usesContracts").GetArrayLength());
        Assert.DoesNotContain(value.GetProperty("usesContracts").EnumerateArray(), use => use.GetProperty("role").GetString() == "runtime" && use.GetProperty("required").GetBoolean());
        Assert.Single(value.GetProperty("platformImports").EnumerateArray());
        Assert.Equal(4, value.GetProperty("providerImports").GetArrayLength());
        Assert.Equal("required", Assert.Single(value.GetProperty("dependsOn").EnumerateArray()).GetProperty("presence").GetString());
        await ProjectCanonicalWorker(worker, driver);
        await ExerciseMcpWorkerProcess(root, Path.Combine(worker, "bin/worker-projection"), value);
    }

    private static async Task ExerciseMcpWorkerProcess(string root, string runtimeRoot, JsonElement value)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(root, "McpProcessWorkspace")).FullName;
        await using (var candidate = new PackageWorkerPeer(root, runtimeRoot, workspace, value))
        { await candidate.Prepare(); await candidate.Shutdown(); }
        await using (var active = new PackageWorkerPeer(root, runtimeRoot, workspace, value))
        {
            await active.Prepare(); await active.Activate();
            Assert.Equal("mcp", (await active.Invoke("mcp.tools", "tool-source", "describe", new { })).GetProperty("sourceId").GetString());
            Assert.Equal("mcp", (await active.Invoke("mcp.tools.observed", "tool-execution-observed", "describe", new { })).GetString());
            Assert.True((await active.Invoke("mcp.stack", "stack-contributor", "metadata", new { })).GetProperty("supportsImport").GetBoolean());
            await active.Shutdown();
        }
    }
}
