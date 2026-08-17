using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic.Runtime;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Memory.Semantic.Tests;

public sealed class MemorySemanticWorkerTopologyTests
{
    [Fact]
    public void RepositoryInventory_UsesAggregateWorkerProvidersAndRoleSpecificLocks()
    {
        var repositoryRoot = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        using var inventory = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repositoryRoot, "packages.json")));
        var package = Assert.Single(inventory.RootElement.GetProperty("packages").EnumerateArray(), static candidate =>
            candidate.GetProperty("key").GetString() == "agent-memory-semantic");

        Assert.Equal(
            "src/Sunder.Package.Agent.Memory.Semantic/Aggregate/MemorySemanticPackage.csproj",
            package.GetProperty("projectPath").GetString());
        Assert.Equal(
            "src/Sunder.Package.Agent.Memory.Semantic/Sunder.Package.Agent.Memory.Semantic.csproj",
            package.GetProperty("workerProjectPath").GetString());
        Assert.Equal(
            [
                "semantic.memory.inspector",
                "semantic.memory.lifecycle",
                "semantic.memory.profile.capabilities",
                "semantic.memory.prompt.context",
                "semantic.memory.session.cleaner",
            ],
            package.GetProperty("providerIds").EnumerateArray().Select(static item => item.GetString()));
        var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        var workerManifestPath = Path.Combine(
            repositoryRoot,
            "src",
            "Sunder.Package.Agent.Memory.Semantic",
            "obj",
            outputDirectory.Parent?.Name ?? "Debug",
            outputDirectory.Name,
            RuntimeInformation.RuntimeIdentifier,
            "sunder-package.json");
        using var workerManifest = JsonDocument.Parse(File.ReadAllBytes(workerManifestPath));
        var workerTarget = Assert.Single(workerManifest.RootElement.GetProperty("targets").EnumerateArray());
        Assert.Equal(
            [
                "background-services.v1",
                "contributions.v1",
                "core.v1",
                "logging.v1",
                "packaging.v1",
                "role-local-workspace.v1",
                "rpc.v2",
                "runtime-generations.v1",
                "sdk-baseline-1-1.v1",
                "settings.schema.v1",
                "settings.v1",
                "storage.v1",
                "worker-protocol.v3",
            ],
            workerTarget.GetProperty("requiredHostCapabilities")
                .EnumerateArray()
                .Select(static item => item.GetString()));

        var solutionProjects = XDocument.Load(Path.Combine(repositoryRoot, "Sunder.AgentPackage.slnx"))
            .Descendants("Project")
            .Select(static project => project.Attribute("Path")?.Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("src/Sunder.Package.Agent.Memory.Semantic/Sunder.Package.Agent.Memory.Semantic.csproj", solutionProjects);
        Assert.Contains("src/Sunder.Package.Agent.Memory.Semantic/App/Sunder.Package.Agent.Memory.Semantic.App.csproj", solutionProjects);
        Assert.Contains("src/Sunder.Package.Agent.Memory.Semantic/Protocol/Sunder.Package.Agent.Memory.Semantic.Protocol.csproj", solutionProjects);
        Assert.Contains("src/Sunder.Package.Agent.Memory.Semantic/Aggregate/MemorySemanticPackage.csproj", solutionProjects);

        using var workerLock = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            repositoryRoot,
            "src",
            "Sunder.Package.Agent.Memory.Semantic",
            "packages.release.lock.json")));
        var workerDependencies = workerLock.RootElement.GetProperty("dependencies").GetProperty("net10.0");
        Assert.True(workerDependencies.TryGetProperty("Sunder.Sdk.Worker", out _));
        Assert.False(workerDependencies.TryGetProperty("Avalonia", out _));

        using var appLock = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            repositoryRoot,
            "src",
            "Sunder.Package.Agent.Memory.Semantic",
            "App",
            "packages.release.lock.json")));
        var appDependencies = appLock.RootElement.GetProperty("dependencies").GetProperty("net10.0");
        Assert.True(appDependencies.TryGetProperty("Sunder.Sdk.Avalonia", out _));
        Assert.False(appDependencies.TryGetProperty("Sunder.Sdk.Worker", out _));
    }

    [Fact]
    public void DeferredStore_DoesNotCreateOrMigrateSqliteUntilActivation()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = MemoryLocalStore.CreateDeferred(scope.Context);

        Assert.False(store.IsInitialized);
        Assert.False(File.Exists(store.DatabasePath));

        store.Initialize();
        store.Initialize();

        Assert.True(store.IsInitialized);
        Assert.True(File.Exists(store.DatabasePath));
    }

    [Fact]
    public async Task AppGateway_UsesExactOwnerRpcForUnaryAndStreamingCalls()
    {
        using var state = new RegressionTestRpcState();
        var ignoredService = new RecordingInspectorService();
        var ownerService = new RecordingInspectorService();
        state.Add(
            "sunder.package.agent.memory.semantic",
            MemorySemanticProtocol.Inspector,
            ignoredService,
            MemoryInspectorRpc.CreateHandler(ignoredService),
            new NoOpDisposable(),
            "semantic.memory.unrelated");
        state.Add(
            "sunder.package.agent.memory.semantic",
            MemorySemanticProtocol.Inspector,
            ownerService,
            MemoryInspectorRpc.CreateHandler(ownerService),
            new NoOpDisposable(),
            MemorySemanticProtocol.ProviderId);
        using var gateway = new MemoryAppRuntimeGateway(new RegressionTestRpcClient(
            state,
            "sunder.package.agent.memory.semantic"));
        var sessionChanged = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.SessionChanged += sessionId => sessionChanged.TrySetResult(sessionId);
        gateway.SemanticWorkerStatusChanged += () => workerChanged.TrySetResult();

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

        Assert.Equal(ownerService.Memory, Assert.Single(memories));
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
    }

    [Fact]
    public async Task AppGateway_TransfersLargeMemoryProjectionThroughContentReference()
    {
        var content = new string('m', (1024 * 1024) + 64 * 1024);
        using var state = new RegressionTestRpcState();
        var service = new RecordingInspectorService(content);
        state.Add(
            "sunder.package.agent.memory.semantic",
            MemorySemanticProtocol.Inspector,
            service,
            MemoryInspectorRpc.CreateHandler(service),
            new NoOpDisposable(),
            MemorySemanticProtocol.ProviderId);
        using var gateway = new MemoryAppRuntimeGateway(new RegressionTestRpcClient(
            state,
            "sunder.package.agent.memory.semantic"));

        var memory = Assert.Single(await gateway.ListMemoriesAsync(service.Memory.SessionId));

        Assert.Equal(content, memory.Content);
        Assert.Equal("sunder.rpc.values.v1", MemorySemanticProtocol.Inspector.SchemaProfile);
    }

    [Fact]
    public void AggregateManifest_IsSixRidAppExactRidWorkerOwnerScopedAndLoadsPackagedSqlite()
    {
        var manifestPath = Path.Combine(
            AggregateOutputRoot(),
            "manifest",
            "sunder-package.json");
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

        var provider = Assert.Single(root.GetProperty("provides").EnumerateArray(), static candidate =>
            candidate.GetProperty("providerId").GetString() == MemorySemanticProtocol.ProviderId);
        Assert.Equal("owner", provider.GetProperty("audience").GetString());
        Assert.Equal(MemorySemanticProtocol.Inspector.ContractId, provider.GetProperty("contractId").GetString());
        var contract = Assert.Single(root.GetProperty("contractBundles").EnumerateArray(), static candidate =>
            candidate.GetProperty("contractId").GetString() == MemorySemanticProtocol.Inspector.ContractId);
        Assert.Equal("package", contract.GetProperty("exposure").GetString());
        Assert.Equal(MemorySemanticProtocol.Inspector.Sha256, contract.GetProperty("descriptorSha256").GetString());
        Assert.Equal(MemorySemanticProtocol.Inspector.AbiSha256, contract.GetProperty("abiSha256").GetString());
        var appImport = Assert.Single(root.GetProperty("usesContracts").EnumerateArray(), static candidate =>
            candidate.GetProperty("role").GetString() == "app");
        Assert.Equal(MemorySemanticProtocol.Inspector.ContractId, appImport.GetProperty("contractId").GetString());
        Assert.Equal(
            ["discover", "invoke", "subscribe"],
            appImport.GetProperty("actions").EnumerateArray().Select(static action => action.GetString()));

        var workerRoot = Path.Combine(
            AggregateOutputRoot(),
            "payload",
            "runtime",
            "shared",
            "lib");
        Assert.True(File.Exists(Path.Combine(workerRoot, "SQLitePCLRaw.provider.e_sqlite3.dll")));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(workerRoot, "*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName),
            static fileName => fileName?.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase) == true);

        var nativePath = Path.Combine(workerRoot, NativeSqliteFileName());
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

    private static string AggregateOutputRoot()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        var configuration = output.Parent?.Name ?? "Debug";
        return Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "src",
            "Sunder.Package.Agent.Memory.Semantic",
            "Aggregate",
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
