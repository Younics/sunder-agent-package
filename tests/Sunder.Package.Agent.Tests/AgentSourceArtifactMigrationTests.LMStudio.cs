using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Protocol;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static readonly string[] LMStudioProviderSources = new[] { "LMStudioProviderValues.cs", "ChatValues.cs", "ChatStream.cs", "ChatProvider.cs", "EmbeddingProvider.cs" }
        .Select(file => "src/Sunder.Package.Agent.Provider.LMStudio/Rpc/" + file).ToArray();
    internal static readonly string[] LMStudioRelocationSources = [.. LMStudioProviderSources,
        "src/Sunder.Package.Agent.Provider.LMStudio/Runtime/ProviderSettings.cs",
        "src/Sunder.Package.Agent.Provider.LMStudio/Rpc/CredentialSettings.Composition.cs",
        "src/Sunder.Package.Agent.Provider.LMStudio/Rpc/LMStudioWorkerBindings.cs",
        "src/Sunder.Package.Agent.Provider.LMStudio/Runtime/LMStudioWorkerComposition.cs",
        "src/Sunder.Package.Agent.Provider.LMStudio/Runtime/LMStudioWorkerBindings.Legacy.cs",
        "src/Sunder.Package.Agent.Provider.LMStudio/PackageDependency.Legacy.cs"];

    private static async Task VerifyLMStudioArtifact(string root, string core, string driver, string ownerArchive, string cache)
    {
        var canonical = await BuildCanonicalFamily(root, "Provider.LMStudio", "LMStudioWorker", cache, ownerArchive);
        var worker = canonical.Directory;
        var selected = File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json"));
        await VerifyGeneratedAppView(worker, "Provider.LMStudio");
        await VerifyLMStudioRoles(worker);
        await canonical.Pack();
        Assert.Equal(selected, File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json")));
        using var archive = ZipFile.OpenRead(Path.Combine(worker, "bin/agent-source.sunderpkg"));
        await AssertProviderArchiveHasNoCentralProtocol(archive);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("/Sunder.Package.Agent.Provider.LMStudio.Protocol.dll", StringComparison.Ordinal));
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".deps.json", StringComparison.Ordinal)))
        { using var reader = new StreamReader(entry.Open()); Assert.DoesNotContain("Sunder.Package.Agent.Provider.LMStudio.Protocol", await reader.ReadToEndAsync(), StringComparison.Ordinal); }
        using var manifestStream = archive.GetEntry("manifest/sunder-package.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var value = manifest.RootElement;
        Assert.Equal("sunder.package.agent.provider.lmstudio", value.GetProperty("id").GetString());
        Assert.Equal(3, value.GetProperty("contractBundles").GetArrayLength());
        Assert.Equal(3, value.GetProperty("provides").GetArrayLength());
        var use = Assert.Single(value.GetProperty("usesContracts").EnumerateArray());
        Assert.Equal("app", use.GetProperty("role").GetString()); Assert.True(use.GetProperty("required").GetBoolean());
        Assert.Equal(2, value.GetProperty("providerImports").GetArrayLength());
        Assert.True(!value.TryGetProperty("packageApiImports", out var apis) || apis.GetArrayLength() == 0);
        Assert.True(!value.TryGetProperty("platformImports", out var platforms) || platforms.GetArrayLength() == 0);
        Assert.Equal("required", Assert.Single(value.GetProperty("dependsOn").EnumerateArray()).GetProperty("presence").GetString());
        await ProjectCanonicalWorker(worker, driver);
        var workspace = Directory.CreateDirectory(Path.Combine(root, "LMStudioProcessWorkspace")).FullName;
        await using (var candidate = new PackageWorkerPeer(root, Path.Combine(worker, "bin/worker-projection"), workspace, value))
        { await candidate.Prepare(); await candidate.Shutdown(); }
        await using (var active = new PackageWorkerPeer(root, Path.Combine(worker, "bin/worker-projection"), workspace, value))
        {
            await active.Prepare(); Assert.Equal(JsonValueKind.Object, active.Contributions.GetProperty("settingsSchema").ValueKind);
            await active.Activate();
            Assert.Equal("lmstudio", (await active.Invoke("lmstudio.chat", "chat-provider", "describe", new { })).GetProperty("providerId").GetString());
            Assert.Equal("lmstudio", (await active.Invoke("lmstudio.embedding", "embedding-provider", "describe", new { })).GetProperty("providerId").GetString());
            Assert.True((await active.Invoke("lmstudio.chat", "chat-provider", "get-capabilities", new { modelId = "lmstudio/artifact" })).GetProperty("supportsStreamingToolCalls").GetBoolean());
            await active.Shutdown();
        }
    }
}
