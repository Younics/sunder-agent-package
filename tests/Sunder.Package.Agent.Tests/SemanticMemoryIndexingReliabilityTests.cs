using Microsoft.Extensions.Logging;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class SemanticMemoryIndexingReliabilityTests
{
    [Fact]
    public async Task Reindex_FailureAndCancellation_PreservePreviousCompleteGeneration()
    {
        await using var harness = new IndexingHarness();
        harness.AddMemory("First durable memory.");
        harness.AddMemory("Second durable memory.");
        harness.Provider.VectorVersion = 1;
        await harness.ReindexAsync();
        var original = Snapshot(harness);

        harness.Provider.VectorVersion = 2;
        harness.Provider.Mode = EmbeddingProviderMode.Fail;
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.ReindexAsync());
        Assert.Equal(original, Snapshot(harness));

        harness.Provider.Mode = EmbeddingProviderMode.WaitForCancellation;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.ReindexAsync(cancellation.Token));
        Assert.Equal(original, Snapshot(harness));
    }

    [Fact]
    public async Task BackgroundService_StartStopStart_ProcessesNewGenerationAfterRestart()
    {
        await using var harness = new IndexingHarness();
        harness.AddMemory("First durable memory.");

        await harness.Worker.StartAsync();
        await WaitUntilAsync(() => harness.ActiveEmbeddings.Count == 1);
        await harness.Worker.StopAsync();

        harness.AddMemory("Second durable memory.");
        await harness.Worker.StartAsync();
        await WaitUntilAsync(() => harness.ActiveEmbeddings.Count == 2);
        await harness.Worker.StopAsync();

        Assert.False(harness.Worker.GetStatus().IsRunning);
        Assert.Equal(2, harness.ActiveEmbeddings.Count);
    }

    [Fact]
    public async Task BackgroundService_SaturatedBoundedQueue_RetainsEveryUniqueWorkKey()
    {
        await using var harness = new IndexingHarness(queueCapacity: 2);
        var memoryId = Guid.NewGuid();

        for (var index = 0; index < 20; index++)
        {
            Assert.True(harness.Worker.QueueMemoryIndex(index == 0 ? memoryId : Guid.NewGuid(), harness.Profile.ProfileId));
        }

        Assert.True(harness.Worker.QueueSessionReindex(harness.Session.SessionId, harness.Profile.ProfileId));
        Assert.Equal(21, harness.Worker.GetStatus().PendingItemCount);

        await harness.Worker.StartAsync();
        await WaitUntilAsync(() => harness.Worker.GetStatus().PendingItemCount == 0);

        Assert.True(harness.Worker.GetStatus().ProcessedItemCount >= 21);
    }

    [Fact]
    public async Task BackgroundService_DisposeAsync_AwaitsCanceledWorker()
    {
        await using var harness = new IndexingHarness();
        harness.AddMemory("A memory whose embedding blocks.");
        harness.Provider.Mode = EmbeddingProviderMode.WaitForCancellation;
        await harness.Worker.StartAsync();
        await harness.Provider.GenerationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await harness.Worker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(harness.Provider.CancellationObserved);
        Assert.False(harness.Worker.GetStatus().IsRunning);
        Assert.Equal(0, harness.Worker.GetStatus().PendingItemCount);
    }

    private static IReadOnlyList<string> Snapshot(IndexingHarness harness)
        => harness.ActiveEmbeddings.Values
            .OrderBy(item => item.MemoryId)
            .Select(item => $"{item.MemoryId:N}:{string.Join(',', item.Values)}")
            .ToArray();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(condition(), "The semantic indexing condition was not reached before the timeout.");
    }

    private sealed class IndexingHarness : IAsyncDisposable
    {
        private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "sunder-memory-indexing-tests", Guid.NewGuid().ToString("N"));
        private readonly TestPackageContext _context;

        public IndexingHarness(int queueCapacity = 8)
        {
            Session = new AgentSessionRecord(Guid.NewGuid(), "Test Session", AgentSessionState.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            Profile = new AgentProfileRecord(
                "profile-memory-test",
                "Memory Test",
                null,
                null,
                null,
                null,
                "test-embedding-provider",
                "test-embedding-model",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                []);
            _context = new TestPackageContext(_rootPath);
            var catalog = new TestExtensionCatalog();
            catalog.Add(PackageExtensionPoints.RuntimeCatalogs, new TestRuntimeCatalog(Session, Profile));
            Provider = new ControlledEmbeddingProvider("test-embedding-provider");
            catalog.Add(PackageExtensionPoints.EmbeddingProviders, Provider);
            Store = new MemoryLocalStore(_context);
            var settings = new MemorySemanticSettingsService(_context);
            var resolver = new SemanticModelRuntimeResolver(catalog, settings);
            Backend = new SemanticMemoryRetrievalBackend(Store, resolver, settings);
            Worker = new SemanticMemoryIndexingBackgroundService(
                Store,
                resolver,
                settings,
                Backend,
                new SemanticMemoryMetricsService(),
                queueCapacity);
        }

        public AgentSessionRecord Session { get; }
        public AgentProfileRecord Profile { get; }
        public ControlledEmbeddingProvider Provider { get; }
        public MemoryLocalStore Store { get; }
        public SemanticMemoryRetrievalBackend Backend { get; }
        public SemanticMemoryIndexingBackgroundService Worker { get; }

        public IReadOnlyDictionary<Guid, StoredMemoryEmbeddingRecord> ActiveEmbeddings
            => Store.ListEmbeddings(Session.SessionId, "test-embedding-provider", "test-embedding-model");

        public void AddMemory(string content)
            => Store.UpsertMemory(new MemoryUpsertRequest(
                Session.SessionId,
                "remembered-fact",
                content,
                content.ToLowerInvariant(),
                "Indexing fixture evidence.",
                Guid.NewGuid(),
                false,
                0.8f,
                0.9f));

        public Task<int> ReindexAsync(CancellationToken cancellationToken = default)
            => Backend.ReindexSessionAsync(
                Session.SessionId,
                Profile.ProfileId,
                Store.ListMemories(Session.SessionId),
                cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await Worker.DisposeAsync();
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
    }

    private enum EmbeddingProviderMode
    {
        Success,
        Fail,
        WaitForCancellation,
    }

    private sealed class ControlledEmbeddingProvider(string providerId) : IAgentEmbeddingProvider
    {
        public AgentEmbeddingProviderDescriptor Descriptor { get; } = new(providerId, "Controlled Embeddings", []);
        public EmbeddingProviderMode Mode { get; set; }
        public int VectorVersion { get; set; } = 1;
        public bool CancellationObserved { get; private set; }
        public TaskCompletionSource GenerationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentEmbeddingModelDescriptor>>(
                [new AgentEmbeddingModelDescriptor("test-embedding-model", "Test Embedding Model", 2, true)]);

        public ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentEmbeddingProviderReadiness(providerId, AgentProviderReadinessStatus.Ready, "Ready."));

        public async ValueTask<AgentEmbeddingGenerationResult?> GenerateEmbeddingAsync(
            string modelId,
            string text,
            CancellationToken cancellationToken = default)
            => (await GenerateEmbeddingsAsync(modelId, [text], cancellationToken))[0];

        public async ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateEmbeddingsAsync(
            string modelId,
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
        {
            GenerationStarted.TrySetResult();
            if (Mode == EmbeddingProviderMode.Fail)
            {
                throw new InvalidOperationException("Embedding generation failed for the reliability fixture.");
            }

            if (Mode == EmbeddingProviderMode.WaitForCancellation)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    CancellationObserved = true;
                    throw;
                }
            }

            return texts.Select(text => (AgentEmbeddingGenerationResult?)new AgentEmbeddingGenerationResult(
                modelId,
                [VectorVersion, text.Length])).ToArray();
        }
    }

    private sealed class TestExtensionCatalog : IPackageExtensionCatalog
    {
        private readonly Dictionary<string, List<object>> _extensions = new(StringComparer.OrdinalIgnoreCase);

        public void Add<T>(PackageExtensionPoint<T> extensionPoint, T extension)
        {
            if (!_extensions.TryGetValue(extensionPoint.Id, out var entries))
            {
                entries = [];
                _extensions[extensionPoint.Id] = entries;
            }

            entries.Add(extension!);
        }

        public IReadOnlyList<T> GetExtensions<T>(PackageExtensionPoint<T> extensionPoint)
            => _extensions.TryGetValue(extensionPoint.Id, out var entries) ? entries.Cast<T>().ToArray() : [];
    }

    private sealed class TestRuntimeCatalog(AgentSessionRecord session, AgentProfileRecord profile) : IAgentRuntimeCatalog
    {
        public event Action<Guid>? SessionChanged { add { } remove { } }
        public event Action<Guid, AgentTurnRecord>? TurnChanged { add { } remove { } }
        public event Action<string>? ProfileChanged { add { } remove { } }

        public IReadOnlyList<AgentSessionRecord> ListSessions() => [session];
        public IReadOnlyList<AgentSessionRecord> ListSessionsForProfile(string profileId) => profileId == profile.ProfileId ? [session] : [];
        public IReadOnlyList<AgentSessionRecord> ListSessionsForWorkspace(string workspaceId) => [];
        public AgentSessionRecord? GetSession(Guid sessionId) => sessionId == session.SessionId ? session : null;
        public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces() => [];
        public AgentWorkspaceRecord? GetWorkspace(string workspaceId) => null;
        public AgentProfileRecord? GetSessionProfile(Guid sessionId) => sessionId == session.SessionId ? profile : null;
        public AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId) => null;
        public AgentSessionContextCheckpointRecord? GetLatestSessionContextCheckpoint(Guid sessionId) => null;
        public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId) => null;
        public IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit) => [];
        public IReadOnlyList<AgentTurnRecord> ListTurnsBefore(Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit) => [];
        public IReadOnlyList<AgentTurnRecord> ListTurnsAfter(Guid sessionId, DateTimeOffset afterCreatedAtUtc, Guid afterTurnId, int limit) => [];
        public IReadOnlyList<AgentProfileRecord> ListProfiles() => [profile];
        public AgentProfileRecord? GetProfile(string profileId) => profileId == profile.ProfileId ? profile : null;
        public AgentProfileModelBindingRecord? GetSessionModelBinding(Guid sessionId, string capabilityKind) => null;
        public AgentProfileModelBindingRecord? GetModelBinding(string profileId, string capabilityKind) => null;
    }

    private sealed class TestPackageContext : IPackageContext
    {
        public TestPackageContext(string rootPath)
        {
            Directory.CreateDirectory(rootPath);
            InstallPath = rootPath;
            Storage = new TestPackageStorageContext(rootPath);
        }

        public string PackageId => "test.package.agent.memory.semantic";
        public string Version => "1.0.0";
        public string InstallPath { get; }
        public IPackageStorageContext Storage { get; }
        public IPackageConfiguration Configuration { get; } = new TestPackageConfiguration();
        public IPackageSecrets Secrets { get; } = new TestPackageSecrets();
        public ILoggerFactory LoggerFactory => Logging.LoggerFactory;
        public IPackageLogging Logging { get; } = NullPackageLogging.Instance;
    }

    private sealed class TestPackageStorageContext : IPackageStorageContext
    {
        public TestPackageStorageContext(string rootPath)
        {
            Directory.CreateDirectory(rootPath);
            Files = new TestPackageFileStore(rootPath);
            LocalWorkspace = new TestPackageWorkspaceLease(rootPath);
        }

        public IPackageFileStore Files { get; }
        public IPackageKeyValueStore State { get; } = new TestPackageKeyValueStore();
        public IPackageLocalWorkspaceLease LocalWorkspace { get; }
    }

    private sealed class TestPackageFileStore(string rootPath) : TestPackageFileStoreBase(rootPath);

    private sealed class TestPackageKeyValueStore : IPackageKeyValueStore
    {
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class TestPackageConfiguration : EmptyPackageConfiguration;

    private sealed class TestPackageSecrets : InMemoryPackageSecrets;
}
