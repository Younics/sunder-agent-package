extern alias MemoryWorker;
extern alias MemoryApp;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using MemoryWorker::Sunder.Package.Agent.Contracts.Models;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Runtime;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Services;
using ManagementService = MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Rpc.ManagementService;
using MemoryAppRuntimeGateway = MemoryApp::Sunder.Package.Agent.Memory.Semantic.MemoryAppRuntimeGateway;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Memory.Semantic.Tests;

public sealed class MemorySemanticWorkerTopologyTests
{
    private const string InspectorProviderId = "semantic.memory.inspector";
    private static SunderRpcContractDescriptor Inspector => RegressionTestRpcDescriptors.Get("sunder.agent.memory.semantic.inspector");

    [Fact]
    [Trait("Lane", "PackageCompatibility")]
    public void RepositoryInventory_UsesCanonicalRootProvidersAndIsolatedRoleDependencies()
    {
        var repositoryRoot = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        using var inventory = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repositoryRoot, "packages.json")));
        var package = Assert.Single(inventory.RootElement.GetProperty("packages").EnumerateArray(), static candidate =>
            candidate.GetProperty("key").GetString() == "agent-memory-semantic");

        Assert.Equal(
            "src/Sunder.Package.Agent.Memory.Semantic/Sunder.Package.Agent.Memory.Semantic.csproj",
            package.GetProperty("projectPath").GetString());
        Assert.False(package.TryGetProperty("appProjectPath", out _));
        Assert.False(package.TryGetProperty("workerProjectPath", out _));
        Assert.Equal(
            [
                "semantic.memory.inspector",
                "semantic.memory.lifecycle",
                "semantic.memory.profile.capabilities",
                "semantic.memory.prompt.context",
                "semantic.memory.session.cleaner",
            ],
            package.GetProperty("providerIds").EnumerateArray().Select(static item => item.GetString()));
        var workerManifestPath = Path.Combine(ComposedOutputRoot(), "manifest", "sunder-package.json");
        using var workerManifest = JsonDocument.Parse(File.ReadAllBytes(workerManifestPath));
        var workerTarget = Assert.Single(workerManifest.RootElement.GetProperty("targets").EnumerateArray(),
            target => target.GetProperty("role").GetString() == "runtime");
        Assert.Equal(
            [
                "background-services.v1",
                "contributions.v1",
                "core.v1",
                "execution-runtime.v1",
                "logging.v1",
                "packaging.v1",
                "role-local-workspace.v1",
                "rpc.v1",
                "runtime-generations.v1",
                "sdk-baseline-1-1.v1",
                "settings.schema.v1",
                "settings.v1",
                "storage.v1",
                "worker-protocol.v1",
            ],
            workerTarget.GetProperty("requiredHostCapabilities")
                .EnumerateArray()
                .Select(static item => item.GetString()));

        var solutionProjects = XDocument.Load(Path.Combine(repositoryRoot, "Sunder.AgentPackage.slnx"))
            .Descendants("Project")
            .Select(static project => project.Attribute("Path")?.Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("src/Sunder.Package.Agent.Memory.Semantic/Sunder.Package.Agent.Memory.Semantic.csproj", solutionProjects);
        Assert.DoesNotContain(solutionProjects, static project => project?.Contains("/Aggregate/", StringComparison.Ordinal) == true);

        var workerDependencies = RoleDependencies("worker", "Sunder.Package.Agent.Memory.Semantic");
        Assert.Contains(workerDependencies, name => name.StartsWith("Sunder.Sdk.Worker/", StringComparison.Ordinal));
        Assert.DoesNotContain(workerDependencies, name => name.StartsWith("Avalonia", StringComparison.Ordinal));

        var appDependencies = RoleDependencies("app", "Sunder.Package.Agent.Memory.Semantic.App");
        Assert.Contains(appDependencies, name => name.StartsWith("Sunder.Sdk.Avalonia/", StringComparison.Ordinal));
        Assert.DoesNotContain(appDependencies, name => name.StartsWith("Sunder.Sdk.Worker/", StringComparison.Ordinal));
        Assert.DoesNotContain(appDependencies, name => name.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeferredStore_DoesNotCreateOrMigrateSqliteUntilActivation()
    {
        using var scope = RegressionTestPackageScope.Create();
        var databasePath = Path.Combine(scope.RootPath, "memory", "agent-memory.db");
        await using var store = new MemoryLocalStore(scope.Context, deferInitialization: true);
        var activation = Assert.IsAssignableFrom<ISunderWorkerActivationService>(store);

        Assert.False(store.IsInitialized);
        Assert.Throws<InvalidOperationException>(() => store.DatabasePath);
        Assert.False(File.Exists(databasePath));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activation.ActivateAsync(null!, cancellation.Token).AsTask());
        Assert.False(store.IsInitialized);
        Assert.False(File.Exists(databasePath));

        await activation.ActivateAsync(null!);
        await activation.ActivateAsync(null!);

        Assert.True(store.IsInitialized);
        Assert.Equal(databasePath, store.DatabasePath);
        Assert.True(File.Exists(databasePath));
    }

    [Fact]
    public async Task AppGateway_UsesExactOwnerRpcForUnaryAndStreamingCalls()
    {
        using var state = new RegressionTestRpcState();
        var ignoredService = new RecordingInspectorService();
        var ownerService = new RecordingInspectorService();
        state.AddGenerated(
            "sunder.package.agent.memory.semantic",
            ignoredService,
            new ManagementService(ignoredService).CreateSunderRpcProviderRegistration(),
            new NoOpDisposable(),
            "semantic.memory.unrelated");
        state.AddGenerated(
            "sunder.package.agent.memory.semantic",
            ownerService,
            new ManagementService(ownerService).CreateSunderRpcProviderRegistration(),
            new NoOpDisposable(),
            InspectorProviderId);
        using var gateway = new MemoryAppRuntimeGateway(new RegressionTestRpcClient(
            state,
            "sunder.package.agent.memory.semantic"));
        var sessionChanged = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resetRequired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.SessionChanged += sessionId => sessionChanged.TrySetResult(sessionId);
        gateway.SemanticWorkerStatusChanged += () => workerChanged.TrySetResult();
        gateway.ResetRequired += () => resetRequired.TrySetResult();

        var memories = await gateway.ListMemoriesAsync(
            ownerService.Memory.SessionId,
            "durable",
            includeInactive: true);
        var updated = await gateway.UpdateMemoryAsync(
            ownerService.Memory.MemoryId,
            "preference",
            "Updated through owner RPC.",
            "verified");
        await gateway.InitializeAsync();

        Assert.Equal(JsonSerializer.Serialize(ownerService.Memory), JsonSerializer.Serialize(Assert.Single(memories)));
        Assert.Equal("Updated through owner RPC.", updated.Content);
        Assert.Equal(MemoryQueryKind.Memories, ownerService.LastQuery?.Kind);
        Assert.Equal("durable", ownerService.LastQuery?.SearchText);
        Assert.True(ownerService.LastQuery?.IncludeInactive);
        Assert.Equal(MemoryCommandKind.Update, ownerService.LastCommand?.Kind);
        Assert.Null(ignoredService.LastQuery);
        Assert.Null(ignoredService.LastCommand);
        Assert.Equal(
            ownerService.Memory.SessionId,
            await sessionChanged.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await workerChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await resetRequired.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AppGateway_TransfersLargeMemoryProjectionThroughContentReference()
    {
        var content = new string('m', (1024 * 1024) + 64 * 1024);
        using var state = new RegressionTestRpcState();
        var service = new RecordingInspectorService(content);
        state.AddGenerated(
            "sunder.package.agent.memory.semantic",
            service,
            new ManagementService(service).CreateSunderRpcProviderRegistration(),
            new NoOpDisposable(),
            InspectorProviderId);
        using var gateway = new MemoryAppRuntimeGateway(new RegressionTestRpcClient(
            state,
            "sunder.package.agent.memory.semantic"));

        var memory = Assert.Single(await gateway.ListMemoriesAsync(service.Memory.SessionId));

        Assert.Equal(content, memory.Content);
        Assert.Equal("sunder.rpc.values.v1", Inspector.SchemaProfile);
    }

    [Fact]
    public async Task AppGateway_MalformedChangeReconnectsAndResnapshots()
    {
        using var state = new RegressionTestRpcState();
        var service = new ReconnectingInspectorService();
        state.AddGenerated(
            "sunder.package.agent.memory.semantic",
            service,
            new ManagementService(service).CreateSunderRpcProviderRegistration(),
            new NoOpDisposable(),
            InspectorProviderId);
        var gateway = new MemoryAppRuntimeGateway(new RegressionTestRpcClient(
            state,
            "sunder.package.agent.memory.semantic"));
        var resetCount = 0;
        var resnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.ResetRequired += () =>
        {
            if (Interlocked.Increment(ref resetCount) >= 2)
            {
                resnapshot.TrySetResult();
            }
        };
        try
        {
            await gateway.InitializeAsync();
            await service.SecondSubscriptionStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await resnapshot.Task.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.True(gateway.IsOwnerStreamAvailable);
            Assert.Equal(2, service.SubscriptionCount);

            await gateway.DisposeAsync();
            Assert.True(gateway.ObservationTask!.IsCompletedSuccessfully);
            Assert.False(gateway.IsOwnerStreamAvailable);
        }
        finally
        {
            gateway.Dispose();
        }
    }

    [Fact]
    [Trait("Lane", "PackageCompatibility")]
    public void ComposedManifest_IsExactRidAppWorkerOwnerScopedAndLoadsPackagedSqlite()
    {
        var manifestPath = Path.Combine(
            ComposedOutputRoot(),
            "manifest",
            "sunder-package.json");
        Assert.True(File.Exists(manifestPath), $"Composed manifest was not found: {manifestPath}");
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = document.RootElement;
        var targets = root.GetProperty("targets").EnumerateArray().ToArray();
        var app = Assert.Single(targets, static target => target.GetProperty("role").GetString() == "app");
        var worker = Assert.Single(targets, static target => target.GetProperty("role").GetString() == "runtime");

        Assert.Equal(RuntimeInformation.RuntimeIdentifier, app.GetProperty("rid").GetString());
        Assert.Equal("avalonia", app.GetProperty("kind").GetString());
        Assert.Equal("lib/Sunder.Package.Agent.Memory.Semantic.App.dll", app.GetProperty("entryPoint").GetString());
        Assert.Equal(RuntimeInformation.RuntimeIdentifier, worker.GetProperty("rid").GetString());
        Assert.Equal("worker", worker.GetProperty("kind").GetString());
        Assert.Contains(
            "worker-protocol.v1",
            worker.GetProperty("requiredHostCapabilities").EnumerateArray()
                .Select(static capability => capability.GetString()));

        var provider = Assert.Single(root.GetProperty("provides").EnumerateArray(), static candidate =>
            candidate.GetProperty("providerId").GetString() == InspectorProviderId);
        Assert.Equal("owner", provider.GetProperty("audience").GetString());
        Assert.Equal(Inspector.ContractId, provider.GetProperty("contractId").GetString());
        var contract = Assert.Single(root.GetProperty("contractBundles").EnumerateArray(), static candidate =>
            candidate.GetProperty("contractId").GetString() == Inspector.ContractId);
        Assert.Equal("package", contract.GetProperty("exposure").GetString());
        Assert.Equal(Inspector.Sha256, contract.GetProperty("descriptorSha256").GetString());
        Assert.Equal(Inspector.AbiSha256, contract.GetProperty("abiSha256").GetString());
        var appImport = Assert.Single(root.GetProperty("usesContracts").EnumerateArray(), static candidate =>
            candidate.GetProperty("role").GetString() == "app");
        Assert.Equal(Inspector.ContractId, appImport.GetProperty("contractId").GetString());
        Assert.Equal(
            ["discover", "invoke", "subscribe"],
            appImport.GetProperty("actions").EnumerateArray().Select(static action => action.GetString()));

        var workerRoot = Path.Combine(
            ComposedOutputRoot(),
            "payload",
            "runtime",
            "shared",
            "lib");
        Assert.True(File.Exists(Path.Combine(workerRoot, "SQLitePCLRaw.provider.e_sqlite3.dll")),
            "The composed Worker payload must retain the managed SQLite provider.");
        Assert.DoesNotContain(
            Directory.EnumerateFiles(Path.Combine(ComposedOutputRoot(), "payload", "runtime"), "*", SearchOption.AllDirectories).Select(Path.GetFileName),
            static fileName => fileName?.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase) == true);

        var nativePath = Path.Combine(
            ComposedOutputRoot(),
            "payload",
            "runtime",
            RuntimeInformation.RuntimeIdentifier,
            "lib",
            NativeSqliteFileName());
        Assert.True(File.Exists(nativePath), $"Packaged SQLite native library was not found: {nativePath}");
        var handle = NativeLibrary.Load(nativePath);
        try
        {
            Assert.NotEqual(IntPtr.Zero, handle);
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
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
            "Sunder.Package.Agent.Memory.Semantic",
            "bin",
            configuration,
            output.Name,
            "sunder-dev");
    }

    private static string NativeSqliteFileName()
        => OperatingSystem.IsWindows()
            ? "e_sqlite3.dll"
            : OperatingSystem.IsMacOS()
                ? "libe_sqlite3.dylib"
                : "libe_sqlite3.so";

    private static string[] RoleDependencies(string role, string assemblyName)
    {
        var path = Path.Combine(Path.GetDirectoryName(ComposedOutputRoot())!, "sunder-roles", role,
            RuntimeInformation.RuntimeIdentifier, assemblyName + ".deps.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.GetProperty("libraries").EnumerateObject().Select(library => library.Name).ToArray();
    }

    private sealed class RecordingInspectorService : IMemoryInspectorRpcService
    {
        public RecordingInspectorService(string content = "Durable semantic memory.")
        {
            Memory = new StoredMemoryRecord(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "fact",
                content,
                "evidence",
                Guid.NewGuid(),
                0.8f,
                0.9f,
                true,
                MemoryRecordStates.Active,
                null,
                DateTimeOffset.UtcNow.AddMinutes(-2),
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow,
                3,
                AgentMemoryProvenance.User)
            {
                MemoryRevision = 7,
                IsManual = false,
            };
        }

        public StoredMemoryRecord Memory { get; }

        public MemoryQuery? LastQuery { get; private set; }

        public MemoryCommand? LastCommand { get; private set; }

        public ValueTask<MemoryProjection> QueryAsync(
            SunderRpcInvocationContext context,
            MemoryQuery request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastQuery = request;
            return ValueTask.FromResult(request.Kind == MemoryQueryKind.Memories
                ? new MemoryProjection(Memories: [Memory])
                : new MemoryProjection());
        }

        public ValueTask<MemoryProjection> CommandAsync(
            SunderRpcInvocationContext context,
            MemoryCommand request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastCommand = request;
            return ValueTask.FromResult(new MemoryProjection(Memory: Memory with
            {
                Category = request.Category ?? Memory.Category,
                Content = request.Content ?? Memory.Content,
            }));
        }

        public async IAsyncEnumerable<MemoryChanged> ChangesAsync(
            SunderRpcInvocationContext context,
            MemoryChangeSubscription request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new MemoryChanged(MemoryChangeKind.Session, Memory.SessionId);
            yield return new MemoryChanged(MemoryChangeKind.Worker);
            yield return new MemoryChanged(MemoryChangeKind.ResetRequired);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class ReconnectingInspectorService : IMemoryInspectorRpcService
    {
        private int _subscriptionCount;

        internal int SubscriptionCount => Volatile.Read(ref _subscriptionCount);
        internal TaskCompletionSource SecondSubscriptionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<MemoryProjection> QueryAsync(
            SunderRpcInvocationContext context,
            MemoryQuery request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new MemoryProjection());

        public ValueTask<MemoryProjection> CommandAsync(
            SunderRpcInvocationContext context,
            MemoryCommand request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new MemoryProjection());

        public async IAsyncEnumerable<MemoryChanged> ChangesAsync(
            SunderRpcInvocationContext context,
            MemoryChangeSubscription request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var subscription = Interlocked.Increment(ref _subscriptionCount);
            if (subscription == 2)
            {
                SecondSubscriptionStarted.TrySetResult();
            }
            yield return new MemoryChanged(MemoryChangeKind.ResetRequired);
            if (subscription == 1)
            {
                yield return new MemoryChanged((MemoryChangeKind)int.MaxValue);
                yield break;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
