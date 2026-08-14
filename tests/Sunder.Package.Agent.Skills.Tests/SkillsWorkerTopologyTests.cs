using System.Runtime.InteropServices;
using System.Text.Json;
using Sunder.Package.Agent.Skills.Protocol;
using Sunder.Package.Agent.Skills.Runtime;
using Sunder.Package.Agent.Skills.Services;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Skills.Tests;

public sealed class SkillsWorkerTopologyTests
{
    [Fact]
    public void Store_DoesNotTouchDurableStorageUntilActivation()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var store = new SkillStore(new SkillRuntimeTransferTests.TestContext(root, NullFileStore.Instance));

            Assert.False(Directory.Exists(store.SkillsRootPath));

            store.Activate();
            store.Activate();

            Assert.True(Directory.Exists(store.SkillsRootPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AppGateway_DisposeIsIdempotent()
    {
        var root = CreateTemporaryRoot();
        var gateway = new SkillAppRuntimeGateway(
            new NullOwnerClient(),
            new SkillRuntimeTransferTests.TestContext(root, NullFileStore.Instance));

        try
        {
            gateway.Dispose();
            gateway.Dispose();
        }
        finally
        {
            gateway.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AggregateManifest_ContainsSixRidAppExactRidWorkerAndOwnerProvider()
    {
        var aggregateRoot = AggregateOutputRoot();
        var manifestPath = Path.Combine(aggregateRoot, "manifest", "sunder-package.json");
        Assert.True(File.Exists(manifestPath), $"Aggregate manifest was not found: {manifestPath}");
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = document.RootElement;
        var targets = root.GetProperty("targets").EnumerateArray().ToArray();
        var apps = targets.Where(static target => target.GetProperty("role").GetString() == "app").ToArray();
        var worker = Assert.Single(targets, static target => target.GetProperty("role").GetString() == "runtime");

        Assert.Equal(
            ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"],
            apps.Select(static app => app.GetProperty("rid").GetString()));
        Assert.All(apps, static app => Assert.Equal("avalonia", app.GetProperty("kind").GetString()));
        Assert.Equal(RuntimeInformation.RuntimeIdentifier, worker.GetProperty("rid").GetString());
        Assert.Equal("worker", worker.GetProperty("kind").GetString());
        Assert.Contains(
            "worker-protocol.v3",
            worker.GetProperty("requiredHostCapabilities").EnumerateArray()
                .Select(static capability => capability.GetString()));
        Assert.DoesNotContain(
            "theming.v1",
            worker.GetProperty("requiredHostCapabilities").EnumerateArray()
                .Select(static capability => capability.GetString()));

        Assert.Equal(
            ["skills.management", "skills.prompt.context", "skills.selectable.capabilities", "skills.stack", "skills.tools"],
            root.GetProperty("provides").EnumerateArray()
                .Select(static provider => provider.GetProperty("providerId").GetString()));
        var owner = Assert.Single(root.GetProperty("provides").EnumerateArray(), static provider =>
            provider.GetProperty("providerId").GetString() == SkillsOwnerProtocol.ProviderId);
        Assert.Equal("owner", owner.GetProperty("audience").GetString());
        Assert.Equal(SkillsOwnerProtocol.Descriptor.ContractId, owner.GetProperty("contractId").GetString());
        var appImport = Assert.Single(root.GetProperty("usesContracts").EnumerateArray(), static contract =>
            contract.GetProperty("role").GetString() == "app");
        Assert.Equal(SkillsOwnerProtocol.Descriptor.ContractId, appImport.GetProperty("contractId").GetString());

        var runtimeRoot = Path.Combine(aggregateRoot, "payload", "runtime");
        var workerRoot = Path.Combine(runtimeRoot, RuntimeInformation.RuntimeIdentifier, "lib");
        Assert.True(File.Exists(Path.Combine(
            workerRoot,
            OperatingSystem.IsWindows() ? "Sunder.Package.Agent.Skills.exe" : "Sunder.Package.Agent.Skills")));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(runtimeRoot, "*", SearchOption.AllDirectories).Select(Path.GetFileName),
            static fileName => fileName?.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static string AggregateOutputRoot()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        var configuration = output.Parent?.Name ?? "Debug";
        return Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "src",
            "Sunder.Package.Agent.Skills",
            "Aggregate",
            "bin",
            configuration,
            output.Name,
            "sunder-dev");
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-skills-worker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class NullOwnerClient : ISkillOwnerClient
    {
        public ValueTask<SkillProjection> QueryAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<SkillProjection>(new InvalidOperationException("Runtime is unavailable."));

        public ValueTask<SkillProjection> CommandAsync(
            SkillCommand command,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SkillProjection>(new InvalidOperationException("Runtime is unavailable."));

        public async IAsyncEnumerable<SkillChanged> SubscribeChangesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class NullFileStore : IPackageFileStore
    {
        internal static NullFileStore Instance { get; } = new();

        public Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(null);

        public Task WriteAsync(
            string relativePath,
            ReadOnlyMemory<byte> contents,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
