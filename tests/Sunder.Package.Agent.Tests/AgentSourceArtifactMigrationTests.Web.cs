using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static readonly string[] WebRpcSources = new[] { "WebProviderValues.cs", "ToolSourceProvider.cs", "WebWorkerBindings.cs" }
        .Select(file => "src/Sunder.Package.Agent.Tools.Web/Rpc/" + file).ToArray();
    internal static readonly string[] WebRelocationSources = [.. WebRpcSources,
        "src/Sunder.Package.Agent.Tools.Web/Services/WebToolSource.cs",
        "src/Sunder.Package.Agent.Tools.Web/Runtime/WebWorkerComposition.cs",
        "src/Sunder.Package.Agent.Tools.Web/Runtime/WebWorkerBindings.Legacy.cs",
        "src/Sunder.Package.Agent.Tools.Web/PackageDependency.Legacy.cs"];

    private static async Task VerifyWebArtifact(string root, string core, string driver, string ownerArchive, string cache)
    {
        var repository = BuilderSourceMigrationTests.FindRepository();
        var canonical = await BuildCanonicalFamily(root, "Tools.Web", "WebWorker", cache, ownerArchive);
        var worker = canonical.Directory;
        var selected = File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json"));
        var owned = File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.owned.json"));
        AssertHeadlessRoleOutput(worker);
        using (var plan = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(worker, "obj/Debug/net10.0/sunder-analysis",
            System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, "sunder-rpc/package-role-plan.v1.json"))))
        {
            var services = plan.RootElement.GetProperty("composition").GetProperty("worker").EnumerateArray().ToArray();
            const string prefix = "global::Sunder.Package.Agent.Tools.Web.";
            foreach (var name in new[] { "Rpc.ToolSourceProvider", "Services.WebToolSource", "Services.WebToolsSettingsService",
                "Services.SystemWebHostResolver", "Services.PinnedWebFetchHttpClientFactory" })
                Assert.Single(services, service => service.GetProperty("type").GetString() == prefix + name);
            var search = Assert.Single(services, service => service.GetProperty("type").GetString() == prefix + "Backends.ExaWebSearchBackend");
            Assert.Equal(prefix + "Services.WebToolsSettingsService", Assert.Single(search.GetProperty("arguments").EnumerateArray()).GetProperty("serviceType").GetString());
            var fetch = Assert.Single(services, service => service.GetProperty("type").GetString() == prefix + "Services.WebFetchService");
            Assert.Equal(new[] { prefix + "Services.WebUrlNetworkPolicy", prefix + "Services.PinnedWebFetchHttpClientFactory" },
                fetch.GetProperty("arguments").EnumerateArray().Select(argument => argument.GetProperty("serviceType").GetString()));
        }
        await VerifyWebBehavior(root, worker);
        await canonical.Pack();
        Assert.Equal(selected, File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.lock.json")));
        Assert.Equal(owned, File.ReadAllBytes(Path.Combine(worker, "sunder-contracts.owned.json")));
        using var archive = ZipFile.OpenRead(Path.Combine(worker, "bin/agent-source.sunderpkg"));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("/Sunder.Package.Agent.Protocol.dll", StringComparison.Ordinal)
            || entry.FullName.EndsWith("/Sunder.Agent.Execution.Common.dll", StringComparison.Ordinal));
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".deps.json", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(entry.Open()); var dependencies = await reader.ReadToEndAsync();
            Assert.DoesNotContain("Sunder.Package.Agent.Protocol", dependencies, StringComparison.Ordinal);
            Assert.DoesNotContain("Sunder.Agent.Execution.Common", dependencies, StringComparison.Ordinal);
        }
        using var stream = archive.GetEntry("manifest/sunder-package.json")!.Open();
        using var manifest = JsonDocument.Parse(stream);
        var value = manifest.RootElement;
        Assert.Equal("sunder.package.agent.tools.web", value.GetProperty("id").GetString());
        using var inventory = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repository, "packages.json")));
        Assert.Equal(inventory.RootElement.GetProperty("capabilities").GetProperty("sunder.package.agent.tools.web").EnumerateArray().Select(item => item.GetString()).Order(StringComparer.Ordinal),
            value.GetProperty("targets").EnumerateArray().SelectMany(target => target.GetProperty("requiredHostCapabilities").EnumerateArray())
                .Select(item => item.GetString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        Assert.Single(value.GetProperty("contractBundles").EnumerateArray()); Assert.Single(value.GetProperty("provides").EnumerateArray());
        Assert.True(!value.TryGetProperty("usesContracts", out var uses) || uses.GetArrayLength() == 0);
        Assert.Single(value.GetProperty("providerImports").EnumerateArray()); Assert.Single(value.GetProperty("targets").EnumerateArray());
        Assert.True(!value.TryGetProperty("packageApiImports", out var imports) || imports.GetArrayLength() == 0);
        Assert.True(!value.TryGetProperty("platformImports", out var platforms) || platforms.GetArrayLength() == 0);
        Assert.Equal("required", Assert.Single(value.GetProperty("dependsOn").EnumerateArray()).GetProperty("presence").GetString());
        await ProjectCanonicalWorker(worker, driver);
        var workspace = Directory.CreateDirectory(Path.Combine(root, "WebProcessWorkspace")).FullName;
        await using (var candidate = new PackageWorkerPeer(root, Path.Combine(worker, "bin/worker-projection"), workspace, value))
        { await candidate.Prepare(); await candidate.Shutdown(); }
        await using (var active = new PackageWorkerPeer(root, Path.Combine(worker, "bin/worker-projection"), workspace, value))
        {
            await active.Prepare(); await active.Activate();
            Assert.Equal(JsonValueKind.Object, active.Contributions.GetProperty("settingsSchema").ValueKind);
            Assert.Equal("web", (await active.Invoke("web.tools", "tool-source", "describe", new { })).GetProperty("sourceId").GetString());
            var listed = await active.Invoke("web.tools", "tool-source", "list-tools", new { context = new { } });
            Assert.Equal(2, listed.GetArrayLength());
            await active.Shutdown();
        }
    }
}
