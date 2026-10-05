extern alias SkillsApp;

using System.Runtime.InteropServices;
using System.Text.Json;
using Sunder.Package.Agent.Skills.Runtime;
using Sunder.Package.Agent.Skills.Services;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Skills.Tests;

using SkillAppRuntimeGateway = SkillsApp::Sunder.Package.Agent.Skills.Runtime.SkillAppRuntimeGateway;
using ISkillOwnerClient = SkillsApp::Sunder.Package.Agent.Skills.Runtime.ISkillOwnerClient;
using SkillProjection = SkillsApp::Sunder.Package.Agent.Skills.Runtime.SkillProjection;
using SkillCommand = SkillsApp::Sunder.Package.Agent.Skills.Runtime.SkillCommand;
using SkillChanged = SkillsApp::Sunder.Package.Agent.Skills.Runtime.SkillChanged;

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
    public async Task AppGateway_RetiredOwnerReconnectsAndShutsDownObservation()
    {
        var root = CreateTemporaryRoot();
        var client = new ReconnectingOwnerClient();
        var gateway = new SkillAppRuntimeGateway(
            client,
            new SkillRuntimeTransferTests.TestContext(root, NullFileStore.Instance));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.SkillsChanged += () => changed.TrySetResult();
        try
        {
            await client.SecondSubscriptionStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.True(gateway.IsOwnerStreamAvailable);
            Assert.Equal(2, client.SubscriptionCount);

            await gateway.DisposeAsync();
            Assert.True(gateway.ObservationTask.IsCompletedSuccessfully);
            Assert.False(gateway.IsOwnerStreamAvailable);
        }
        finally
        {
            gateway.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ChangeStream_EstablishesSubscriptionBeforeInitialSignal()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var store = new SkillStore(
                new SkillRuntimeTransferTests.TestContext(root, NullFileStore.Instance));
            var stream = new SkillRuntimeChangeStream(store, store.NotifySkillsImported);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await using var enumerator = stream.SubscribeAsync(
                    new SkillChangeSubscription(),
                    cancellation.Token)
                .GetAsyncEnumerator(cancellation.Token);

            Assert.True(await enumerator.MoveNextAsync());
            Assert.True(await enumerator.MoveNextAsync());
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
        Assert.Equal("lib/Sunder.Package.Agent.Skills.App.dll", app.GetProperty("entryPoint").GetString());
        Assert.Equal(RuntimeInformation.RuntimeIdentifier, worker.GetProperty("rid").GetString());
        Assert.Equal("worker", worker.GetProperty("kind").GetString());
        Assert.Contains(
            "worker-protocol.v1",
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
            provider.GetProperty("providerId").GetString() == "skills.management");
        Assert.Equal("owner", owner.GetProperty("audience").GetString());
        Assert.Equal("sunder.package.agent.skills.owner", owner.GetProperty("contractId").GetString());
        var appImport = Assert.Single(root.GetProperty("usesContracts").EnumerateArray(), static contract =>
            contract.GetProperty("role").GetString() == "app");
        Assert.Equal("sunder.package.agent.skills.owner", appImport.GetProperty("contractId").GetString());

        var runtimeRoot = Path.Combine(composedRoot, "payload", "runtime");
        var workerRoot = Path.Combine(runtimeRoot, RuntimeInformation.RuntimeIdentifier, "lib");
        Assert.True(File.Exists(Path.Combine(
            workerRoot,
            "Sunder.Package.Agent.Skills.dll")));
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
            "Sunder.Package.Agent.Skills",
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

    private sealed class ReconnectingOwnerClient : ISkillOwnerClient
    {
        private int _subscriptionCount;

        internal int SubscriptionCount => Volatile.Read(ref _subscriptionCount);
        internal TaskCompletionSource SecondSubscriptionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<SkillProjection> QueryAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<SkillProjection>(new NotSupportedException());

        public ValueTask<SkillProjection> CommandAsync(
            SkillCommand command,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SkillProjection>(new NotSupportedException());

        public async IAsyncEnumerable<SkillChanged> SubscribeChangesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var subscription = Interlocked.Increment(ref _subscriptionCount);
            yield return new SkillChanged();
            if (subscription == 1)
            {
                throw new IOException("Injected owner retirement.");
            }

            SecondSubscriptionStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
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
