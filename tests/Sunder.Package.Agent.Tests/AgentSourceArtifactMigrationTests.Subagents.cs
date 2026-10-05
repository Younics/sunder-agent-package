using System.IO.Compression;
using System.Text.Json;
using Sunder.Sdk.Stacks;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static readonly string[] SubagentsModelSources =
    [
        "src/Sunder.Package.Agent.Subagents/Services/SubagentRecord.cs",
        "src/Sunder.Package.Agent.Subagents/Runtime/SubagentRuntimeContracts.cs",
        "src/Sunder.Package.Agent.Subagents/Runtime/TranscriptPageCursor.cs",
    ];
    internal static readonly string[] SubagentsProviderSources = new[]
    {
        "SubagentProviderValues.cs", "SelectableCapabilityProvider.cs", "PromptContextProvider.cs", "ToolSourceProvider.cs", "BehaviorLoopProvider.cs", "SubagentStackProvider.cs", "RunControlClient.cs",
    }.Select(file => "src/Sunder.Package.Agent.Subagents/Rpc/" + file).ToArray();
    internal static readonly string[] SubagentsRelocationSources = [.. SubagentsModelSources, .. SubagentsProviderSources,
        "src/Sunder.Package.Agent.Subagents/Runtime/SubagentsActivation.cs",
        "src/Sunder.Package.Agent.Subagents/Rpc/ProviderComposition.cs",
        "src/Sunder.Package.Agent.Subagents/Rpc/AppComposition.cs",
        "src/Sunder.Package.Agent.Subagents/Runtime/SubagentOwnerRpc.Legacy.cs", "src/Sunder.Package.Agent.Subagents/Runtime/SubagentsWorkerComposition.cs",
        "src/Sunder.Package.Agent.Subagents/Runtime/SubagentsWorkerBindings.Legacy.cs",
        "src/Sunder.Package.Agent.Subagents/PackageDependency.Legacy.cs"];
    internal static readonly string[] SubagentsConsumerSources =
    [
        "src/Sunder.Package.Agent.Subagents/Runtime/ISubagentRuntimeBindings.cs",
        "src/Sunder.Package.Agent.Subagents/Runtime/LegacySubagentRuntimeBindings.cs",
        "src/Sunder.Package.Agent.Subagents/Services/SubagentFeature.Legacy.cs",
        "src/Sunder.Package.Agent.Subagents/Services/SubagentDescriptorSchema.Legacy.cs",
        "src/Sunder.Package.Agent.Subagents/Services/SubagentChildRunCoordinator.Legacy.cs",
        "src/Sunder.Package.Agent.Subagents/Runtime/SubagentRuntimeHandler.Legacy.cs",
        "src/Sunder.Package.Agent.Subagents/Runtime/SubagentRuntimeChangeStream.Legacy.cs",
        "src/Sunder.Package.Agent.Subagents/Runtime/SubsessionLocalRuntimeAdapter.Legacy.cs",
        "src/Sunder.Package.Agent.Subagents/Rpc/SubagentRuntimeBindings.cs",
        "src/Sunder.Package.Agent.Subagents/Rpc/RuntimeCatalogClient.cs",
        "src/Sunder.Package.Agent.Subagents/Rpc/ChildRunClient.cs",
    ];
    internal static readonly string[] SubagentsCatalogSources =
    [
        "src/Sunder.Package.Agent.Subagents/Runtime/SubagentCatalogContracts.cs",
        "src/Sunder.Package.Agent.Subagents/Services/SubagentPermissionStatusAdapter.Legacy.cs",
        "src/Sunder.Package.Agent.Subagents/Services/SubagentStackContributor.Legacy.cs",
        "src/Sunder.Package.Agent.Subagents/Runtime/SubagentLocalManagementGateway.Legacy.cs",
        "src/Sunder.Package.Agent.Subagents/Services/SubagentEditorCapabilityCatalog.Legacy.cs",
        "src/Sunder.Package.Agent.Subagents/Rpc/SubagentRuntimeBindings.Catalogs.cs",
        "src/Sunder.Package.Agent.Subagents/Rpc/CatalogClientInvocation.cs",
        "src/Sunder.Package.Agent.Subagents/Rpc/ChatCatalogClient.cs",
        "src/Sunder.Package.Agent.Subagents/Rpc/ToolCatalogClient.cs",
        "src/Sunder.Package.Agent.Subagents/Rpc/BehaviorCatalogClient.cs",
        "src/Sunder.Package.Agent.Subagents/Rpc/CapabilityCatalogClient.cs",
    ];

    private static async Task VerifySubagentsArtifact(string root, string core, string driver, string ownerArchive, string cache)
    {
        var required = new[] { "sunder.agent.runtime.catalog", "sunder.agent.behavior.loop", "sunder.agent.run.control", "sunder.agent.child.run.executor" };
        var canonical = await BuildCanonicalFamily(root, "Subagents", "SubagentsWorker", cache, ownerArchive);
        var worker = canonical.Directory;
        var selected = File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json"));
        await VerifyGeneratedAppView(worker, "Subagents");
        await VerifySubagentsRoles(worker);
        await canonical.Pack();
        Assert.Equal(selected, File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json")));
        using var archive = ZipFile.OpenRead(Path.Combine(worker, "bin/agent-source.sunderpkg"));
        await AssertArchiveHasNoCentralExecutionAssemblies(archive);
        foreach (var icon in new[] { "sub-session-icon.png", "sub-profile-icon.png" })
            Assert.Contains(archive.Entries, entry => entry.FullName.EndsWith("/assets/" + icon, StringComparison.Ordinal));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("/Sunder.Package.Agent.Subagents.Protocol.dll", StringComparison.Ordinal));
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".deps.json", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(entry.Open());
            Assert.DoesNotContain("Sunder.Package.Agent.Subagents.Protocol", await reader.ReadToEndAsync(), StringComparison.Ordinal);
        }
        using var manifestStream = archive.GetEntry("manifest/sunder-package.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var value = manifest.RootElement;
        Assert.Equal("sunder.package.agent.subagents", value.GetProperty("id").GetString());
        Assert.Equal(21, value.GetProperty("contractBundles").GetArrayLength());
        Assert.Equal(6, value.GetProperty("provides").GetArrayLength());
        Assert.Equal(20, value.GetProperty("usesContracts").GetArrayLength());
        Assert.Equal(required.Order(StringComparer.Ordinal), value.GetProperty("usesContracts").EnumerateArray()
            .Where(use => use.GetProperty("role").GetString() == "runtime" && use.GetProperty("required").GetBoolean())
            .Select(use => use.GetProperty("contractId").GetString()).Order(StringComparer.Ordinal));
        Assert.Single(value.GetProperty("platformImports").EnumerateArray());
        Assert.Equal(4, value.GetProperty("providerImports").GetArrayLength());
        var dependency = Assert.Single(value.GetProperty("dependsOn").EnumerateArray());
        Assert.Equal("sunder.package.agent", dependency.GetProperty("packageId").GetString());
        Assert.Equal("required", dependency.GetProperty("presence").GetString());
        await ProjectCanonicalWorker(worker, driver);
        await VerifySubagentsProcess(root, Path.Combine(worker, "bin/worker-projection"), value);
    }

    private static async Task VerifySubagentsProcess(string root, string projected, JsonElement manifest)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(root, "SubagentsProcessWorkspace")).FullName;
        await using (var candidate = new PackageWorkerPeer(root, projected, workspace, manifest))
        {
            await candidate.Prepare();
            Assert.False(Directory.Exists(Path.Combine(workspace, "subagents")));
            await candidate.Shutdown();
        }
        await using (var active = new PackageWorkerPeer(root, projected, workspace, manifest))
        {
            await active.Prepare();
            await active.Activate();
            Assert.True(Directory.Exists(Path.Combine(workspace, "subagents")));
            var description = await active.Invoke("subagents.behavior.loop", "behavior-loop", "describe", new { });
            Assert.Equal("orchestrated", description.GetProperty("loopId").GetString());
            Assert.Equal("subagents", Assert.Single(description.GetProperty("featureKinds").EnumerateArray()).GetString());
            var stack = await active.Invoke("subagents.stack", "stack-contributor", "metadata", new { });
            Assert.True(stack.GetProperty("supportsExport").GetBoolean());
            Assert.True(stack.GetProperty("supportsImport").GetBoolean());
            await active.Shutdown();
        }
    }
}
