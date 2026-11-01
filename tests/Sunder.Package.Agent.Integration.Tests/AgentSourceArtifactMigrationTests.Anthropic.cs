using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Protocol;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static readonly string[] AnthropicProviderSources = new[] { "AnthropicProviderValues.cs", "ChatValues.cs", "ChatStream.cs", "ChatProvider.cs" }
        .Select(file => "src/Sunder.Package.Agent.Provider.Anthropic/Rpc/" + file).ToArray();
    internal static readonly string[] AnthropicRelocationSources = [.. AnthropicProviderSources,
        "src/Sunder.Package.Agent.Provider.Anthropic/Runtime/ProviderSettings.cs",
        "src/Sunder.Package.Agent.Provider.Anthropic/Rpc/CredentialSettings.Composition.cs",
        "src/Sunder.Package.Agent.Provider.Anthropic/Rpc/AnthropicWorkerBindings.cs",
        "src/Sunder.Package.Agent.Provider.Anthropic/Runtime/AnthropicWorkerComposition.cs",
        "src/Sunder.Package.Agent.Provider.Anthropic/Runtime/AnthropicWorkerBindings.Legacy.cs",
        "src/Sunder.Package.Agent.Provider.Anthropic/PackageDependency.Legacy.cs"];

    private static async Task VerifyAnthropicArtifact(string root, string core, string driver, string ownerArchive, string cache)
    {
        var canonical = await BuildCanonicalFamily(root, "Provider.Anthropic", "AnthropicWorker", cache, ownerArchive);
        var worker = canonical.Directory;
        var selected = File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json"));
        await VerifyGeneratedAppView(worker, "Provider.Anthropic");
        await VerifyAnthropicRoles(worker);
        await canonical.Pack();
        Assert.Equal(selected, File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json")));
        using var archive = ZipFile.OpenRead(Path.Combine(worker, "bin/agent-source.sunderpkg"));
        await AssertProviderArchiveHasNoCentralProtocol(archive);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("/Sunder.Package.Agent.Provider.Anthropic.Protocol.dll", StringComparison.Ordinal));
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".deps.json", StringComparison.Ordinal)))
        { using var reader = new StreamReader(entry.Open()); Assert.DoesNotContain("Sunder.Package.Agent.Provider.Anthropic.Protocol", await reader.ReadToEndAsync(), StringComparison.Ordinal); }
        using var manifestStream = archive.GetEntry("manifest/sunder-package.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var value = manifest.RootElement;
        Assert.Equal("younics.ai.agent.provider.anthropic", value.GetProperty("id").GetString());
        Assert.Equal(2, value.GetProperty("contractBundles").GetArrayLength());
        Assert.Equal(2, value.GetProperty("provides").GetArrayLength());
        var use = Assert.Single(value.GetProperty("usesContracts").EnumerateArray());
        Assert.Equal("app", use.GetProperty("role").GetString()); Assert.True(use.GetProperty("required").GetBoolean());
        Assert.Single(value.GetProperty("providerImports").EnumerateArray());
        Assert.True(!value.TryGetProperty("packageApiImports", out var apis) || apis.GetArrayLength() == 0);
        Assert.True(!value.TryGetProperty("platformImports", out var platforms) || platforms.GetArrayLength() == 0);
        Assert.Equal("required", Assert.Single(value.GetProperty("dependsOn").EnumerateArray()).GetProperty("presence").GetString());
        await ProjectCanonicalWorker(worker, driver);
        var workspace = Directory.CreateDirectory(Path.Combine(root, "AnthropicProcessWorkspace")).FullName;
        await using (var candidate = new PackageWorkerPeer(root, Path.Combine(worker, "bin/worker-projection"), workspace, value))
        { await candidate.Prepare(); await candidate.Shutdown(); }
        await using (var active = new PackageWorkerPeer(root, Path.Combine(worker, "bin/worker-projection"), workspace, value))
        {
            await active.Prepare(); Assert.Equal(JsonValueKind.Object, active.Contributions.GetProperty("settingsSchema").ValueKind);
            await active.Activate();
            Assert.Equal("anthropic", (await active.Invoke("anthropic.chat", "chat-provider", "describe", new { })).GetProperty("providerId").GetString());
            Assert.NotEmpty((await active.Invoke("anthropic.chat", "chat-provider", "list-models", new { })).EnumerateArray());
            await active.Shutdown();
        }
    }
}
