using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Protocol;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static readonly string[] GeminiProviderSources = new[] { "GeminiProviderValues.cs", "ChatValues.cs", "ChatStream.cs", "ChatProvider.cs", "EmbeddingProvider.cs" }
        .Select(file => "src/Sunder.Package.Agent.Provider.Gemini/Rpc/" + file).ToArray();
    internal static readonly string[] GeminiRelocationSources = [.. GeminiProviderSources,
        "src/Sunder.Package.Agent.Provider.Gemini/Runtime/ProviderSettings.cs",
        "src/Sunder.Package.Agent.Provider.Gemini/Rpc/CredentialSettings.Composition.cs",
        "src/Sunder.Package.Agent.Provider.Gemini/Rpc/GeminiWorkerBindings.cs",
        "src/Sunder.Package.Agent.Provider.Gemini/Runtime/GeminiWorkerComposition.cs",
        "src/Sunder.Package.Agent.Provider.Gemini/Runtime/GeminiWorkerBindings.Legacy.cs",
        "src/Sunder.Package.Agent.Provider.Gemini/PackageDependency.Legacy.cs"];

    private static async Task VerifyGeminiArtifact(string root, string core, string driver, string ownerArchive, string cache)
    {
        var canonical = await BuildCanonicalFamily(root, "Provider.Gemini", "GeminiWorker", cache, ownerArchive);
        var worker = canonical.Directory;
        var selected = File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json"));
        await VerifyGeneratedAppView(worker, "Provider.Gemini");
        await VerifyGeminiRoles(worker);
        await canonical.Pack();
        Assert.Equal(selected, File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json")));
        using var archive = ZipFile.OpenRead(Path.Combine(worker, "bin/agent-source.sunderpkg"));
        await AssertProviderArchiveHasNoCentralProtocol(archive);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("/Sunder.Package.Agent.Provider.Gemini.Protocol.dll", StringComparison.Ordinal));
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".deps.json", StringComparison.Ordinal)))
        { using var reader = new StreamReader(entry.Open()); Assert.DoesNotContain("Sunder.Package.Agent.Provider.Gemini.Protocol", await reader.ReadToEndAsync(), StringComparison.Ordinal); }
        using var manifestStream = archive.GetEntry("manifest/sunder-package.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var value = manifest.RootElement;
        Assert.Equal("younics.ai.agent.provider.gemini", value.GetProperty("id").GetString());
        Assert.Equal(3, value.GetProperty("contractBundles").GetArrayLength());
        Assert.Equal(3, value.GetProperty("provides").GetArrayLength());
        var use = Assert.Single(value.GetProperty("usesContracts").EnumerateArray());
        Assert.Equal("app", use.GetProperty("role").GetString()); Assert.True(use.GetProperty("required").GetBoolean());
        Assert.Equal(2, value.GetProperty("providerImports").GetArrayLength());
        Assert.True(!value.TryGetProperty("packageApiImports", out var apis) || apis.GetArrayLength() == 0);
        Assert.True(!value.TryGetProperty("platformImports", out var platforms) || platforms.GetArrayLength() == 0);
        Assert.Equal("required", Assert.Single(value.GetProperty("dependsOn").EnumerateArray()).GetProperty("presence").GetString());
        await ProjectCanonicalWorker(worker, driver);
        var workspace = Directory.CreateDirectory(Path.Combine(root, "GeminiProcessWorkspace")).FullName;
        await using (var candidate = new PackageWorkerPeer(root, Path.Combine(worker, "bin/worker-projection"), workspace, value))
        { await candidate.Prepare(); await candidate.Shutdown(); }
        await using (var active = new PackageWorkerPeer(root, Path.Combine(worker, "bin/worker-projection"), workspace, value))
        {
            await active.Prepare(); Assert.Equal(JsonValueKind.Object, active.Contributions.GetProperty("settingsSchema").ValueKind);
            await active.Activate();
            Assert.Equal("gemini", (await active.Invoke("gemini.chat", "chat-provider", "describe", new { })).GetProperty("providerId").GetString());
            Assert.Equal(2, (await active.Invoke("gemini.embedding", "embedding-provider", "list-models", new { })).GetArrayLength());
            await active.Shutdown();
        }
    }
}
