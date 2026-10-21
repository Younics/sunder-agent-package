using System.Runtime.InteropServices;
using System.Text.Json;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Xunit;

namespace Sunder.Package.Agent.Subagents.Tests;

public sealed class SubagentsWorkerTopologyTests
{
    [Fact]
    public void Store_DoesNotTouchDurableStorageUntilActivation()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var store = new SubagentStore(new TestPackageContext(root));
            var storeDirectory = Path.Combine(root, "subagents");

            Assert.False(Directory.Exists(storeDirectory));

            store.Activate();
            store.Activate();

            Assert.True(Directory.Exists(storeDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Lane", "PackageCompatibility")]
    public void ComposedManifest_ContainsExactRidAppWorkerAndOwnerProvider()
    {
        var composedRoot = ComposedOutputRoot();
        var manifestPath = Path.Combine(composedRoot, "manifest", "sunder-package.json");
        Assert.True(File.Exists(manifestPath), $"Composed manifest was not found: {manifestPath}");
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = document.RootElement;
        var targets = root.GetProperty("targets").EnumerateArray().ToArray();
        var app = Assert.Single(targets, static target => target.GetProperty("role").GetString() == "app");
        var worker = Assert.Single(targets, static target => target.GetProperty("role").GetString() == "runtime");

        Assert.Equal(RuntimeInformation.RuntimeIdentifier, app.GetProperty("rid").GetString());
        Assert.Equal("avalonia", app.GetProperty("kind").GetString());
        Assert.Equal("lib/Sunder.Package.Agent.Subagents.App.dll", app.GetProperty("entryPoint").GetString());
        Assert.Equal(RuntimeInformation.RuntimeIdentifier, worker.GetProperty("rid").GetString());
        Assert.Equal("worker", worker.GetProperty("kind").GetString());
        Assert.Contains(
            "worker-protocol.v1",
            worker.GetProperty("requiredHostCapabilities").EnumerateArray()
                .Select(static capability => capability.GetString()));

        Assert.Equal(
            [
                "subagents.behavior.loop",
                "subagents.management",
                "subagents.prompt.context",
                "subagents.selectable.capabilities",
                "subagents.stack",
                "subagents.tools",
            ],
            root.GetProperty("provides").EnumerateArray()
                .Select(static provider => provider.GetProperty("providerId").GetString()));
        var owner = Assert.Single(root.GetProperty("provides").EnumerateArray(), static provider =>
            provider.GetProperty("providerId").GetString() == "subagents.management");
        Assert.Equal("owner", owner.GetProperty("audience").GetString());
        Assert.Equal("younics.ai.agent.subagents.owner", owner.GetProperty("contractId").GetString());
        var appImport = Assert.Single(root.GetProperty("usesContracts").EnumerateArray(), static contract =>
            contract.GetProperty("role").GetString() == "app");
        Assert.Equal("younics.ai.agent.subagents.owner", appImport.GetProperty("contractId").GetString());

        var runtimeRoot = Path.Combine(composedRoot, "payload", "runtime");
        var workerRoot = Path.Combine(runtimeRoot, RuntimeInformation.RuntimeIdentifier, "lib");
        Assert.True(File.Exists(Path.Combine(
            workerRoot,
            "Sunder.Package.Agent.Subagents.dll")));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(runtimeRoot, "*", SearchOption.AllDirectories).Select(Path.GetFileName),
            static fileName => fileName?.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static string ComposedOutputRoot()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        var configuration = output.Parent?.Name ?? "Debug";
        return Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "src",
            "Sunder.Package.Agent.Subagents",
            "bin",
            configuration,
            output.Name,
            "sunder-dev");
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-subagents-worker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class TestPackageContext(string root) : IPackageContext
    {
        public string PackageId => "younics.ai.agent.subagents";
        public string Version => "2.0.0";
        public string ContentRootPath => AppContext.BaseDirectory;
        public IPackageStorageContext Storage { get; } = new TestStorageContext(root);
        public IPackageSettings Settings { get; } = new EmptyPackageSettings();
        public IPackageSecrets Secrets { get; } = new InMemoryPackageSecrets();
        public IPackageLogging Logging => NullPackageLogging.Instance;
    }

    private sealed class TestStorageContext : IPackageStorageContext
    {
        public TestStorageContext(string root)
        {
            Files = new TestPackageFileStoreBase(Path.Combine(root, "files"));
            RoleLocalWorkspace = new TestPackageRoleLocalWorkspace(root);
        }

        public IPackageFileStore Files { get; }
        public IPackageKeyValueStore State { get; } = new NullStateStore();
        public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; }
    }

    private sealed class NullStateStore : IPackageKeyValueStore
    {
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListKeysAsync(
            string? prefix = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
