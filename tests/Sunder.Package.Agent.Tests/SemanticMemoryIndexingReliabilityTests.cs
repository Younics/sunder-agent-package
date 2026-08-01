using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Package.Agent.Protocol;
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

        await harness.StartWorkerAsync();
        await WaitUntilAsync(() => harness.ActiveEmbeddings.Count == 1);
        await harness.Worker.StopAsync();

        harness.AddMemory("Second durable memory.");
        await harness.StartWorkerAsync();
        await WaitUntilAsync(() => harness.ActiveEmbeddings.Count == 2);
        await harness.Worker.StopAsync();

        Assert.False(harness.Worker.GetStatus().IsRunning);
        Assert.Equal(2, harness.ActiveEmbeddings.Count);
    }

    [Fact]
    public async Task BackgroundService_CandidateStartIsPreparationOnlyAndDiscardIsNonDestructive()
    {
        await using var harness = new IndexingHarness();
        harness.AddMemory("A discarded candidate must not run semantic indexing.");
        Assert.True(harness.Worker.QueueSessionReindex(harness.Session.SessionId, harness.Profile.ProfileId));

        await harness.Worker.StartAsync();
        await harness.WaitForMonitorTicksAsync(3);

        Assert.False(harness.Worker.GetStatus().IsRunning);
        Assert.Equal(0, harness.Provider.TotalCallbackCount);
        Assert.Empty(harness.ActiveEmbeddings);

        await harness.Worker.StopAsync();
        Assert.Equal(0, harness.Worker.GetStatus().PendingItemCount);
    }

    [Fact]
    public async Task BackgroundService_CommitIsIdempotentOnlyForTheExactGeneration()
    {
        await using var harness = new IndexingHarness();
        harness.AddMemory("An exact generation retry must not start duplicate workers.");
        var generation = new PackageRuntimeGeneration(Guid.NewGuid(), 1);

        await harness.Worker.StartAsync();
        await harness.Worker.CommitGenerationAsync(generation);
        await harness.Worker.CommitGenerationAsync(generation);
        await WaitUntilAsync(() => harness.ActiveEmbeddings.Count == 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Worker.CommitGenerationAsync(new PackageRuntimeGeneration(Guid.NewGuid(), 2)));
        Assert.Equal(1, harness.Provider.BatchCallCount);
    }

    [Fact]
    public async Task BackgroundService_RepeatedMonitorTicks_DoNotCallProviderForCurrentSession()
    {
        await using var harness = new IndexingHarness();
        harness.AddMemory("A durable memory that is already semantically current.");
        await harness.StartWorkerAsync();
        await WaitUntilAsync(() => harness.ActiveEmbeddings.Count == 1
                                   && harness.Worker.GetStatus().PendingItemCount == 0);
        var callCount = harness.Provider.BatchCallCount;
        var uploadedTextCount = harness.Provider.UploadedTextCount;
        var totalCallbackCount = harness.Provider.TotalCallbackCount;
        var processedCount = harness.Worker.GetStatus().ProcessedItemCount;

        await harness.WaitForMonitorTicksAsync(4);

        Assert.Equal(totalCallbackCount, harness.Provider.TotalCallbackCount);
        Assert.Equal(callCount, harness.Provider.BatchCallCount);
        Assert.Equal(uploadedTextCount, harness.Provider.UploadedTextCount);
        Assert.Equal(processedCount, harness.Worker.GetStatus().ProcessedItemCount);
    }

    [Fact]
    public async Task BackgroundService_RestartWithCurrentDurableState_DoesNotCallProvider()
    {
        await using var harness = new IndexingHarness();
        harness.AddMemory("A durable memory whose semantic state survives restart.");
        await harness.StartWorkerAsync();
        await WaitUntilAsync(() => harness.ActiveEmbeddings.Count == 1
                                   && harness.Worker.GetStatus().PendingItemCount == 0);
        await harness.Worker.StopAsync();
        var callCount = harness.Provider.BatchCallCount;
        var uploadedTextCount = harness.Provider.UploadedTextCount;

        await harness.RestartWorkerAsync();
        await harness.StartWorkerAsync();
        await harness.WaitForMonitorTicksAsync(4);

        Assert.Equal(callCount, harness.Provider.BatchCallCount);
        Assert.Equal(uploadedTextCount, harness.Provider.UploadedTextCount);
        Assert.Equal(0, harness.Worker.GetStatus().ProcessedItemCount);
    }

    [Fact]
    public async Task BackgroundService_NeverMode_SuppressesAutomaticWorkButAllowsExplicitReindex()
    {
        await using var harness = new IndexingHarness();
        await harness.SetSettingAsync("semantic.reindex.mode", "never");
        harness.AddMemory("A missing embedding must remain missing in Never mode.");

        await harness.StartWorkerAsync();
        await harness.WaitForMonitorTicksAsync(4);

        Assert.Equal(0, harness.Provider.TotalCallbackCount);
        Assert.Equal(0, harness.Provider.BatchCallCount);
        Assert.Equal(0, harness.Provider.UploadedTextCount);
        Assert.Empty(harness.ActiveEmbeddings);

        Assert.True(harness.Worker.QueueSessionReindex(harness.Session.SessionId, harness.Profile.ProfileId));
        await WaitUntilAsync(() => harness.ActiveEmbeddings.Count == 1);

        Assert.Equal(1, harness.Provider.BatchCallCount);
        Assert.Equal(1, harness.Provider.UploadedTextCount);
    }

    [Fact]
    public async Task BackgroundService_LazyMode_DefersEveryProviderCallbackUntilRecall()
    {
        await using var harness = new IndexingHarness();
        await harness.SetSettingAsync("semantic.reindex.mode", "lazy");
        var memory = harness.AddMemory("Lazy mode indexes this memory only when recall needs it.");

        await harness.StartWorkerAsync();
        await harness.WaitForMonitorTicksAsync(4);

        Assert.Equal(0, harness.Provider.TotalCallbackCount);
        Assert.Empty(harness.ActiveEmbeddings);

        var scores = await harness.Backend.ScoreSemanticAsync(
            harness.Profile.ProfileId,
            [memory],
            "When is this memory indexed?");

        Assert.True(harness.Provider.TotalCallbackCount > 0);
        Assert.Single(harness.ActiveEmbeddings);
        Assert.Single(scores);
    }

    [Fact]
    public async Task BackgroundService_ModeChangeDuringProviderCallStopsFurtherCallsAndActivation()
    {
        await using var harness = new IndexingHarness();
        await harness.SetSettingAsync("semantic.batchSize", "1");
        harness.AddMemory("The first automatic batch is stopped before activation.");
        harness.AddMemory("The second automatic batch must never reach the provider.");
        harness.Provider.Mode = EmbeddingProviderMode.WaitForRelease;

        await harness.StartWorkerAsync();
        await harness.Provider.GenerationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await harness.SetSettingAsync("semantic.reindex.mode", "never");
        var callbacksAtDisable = harness.Provider.TotalCallbackCount;
        harness.Provider.ReleaseGeneration();
        await WaitUntilAsync(() => harness.Worker.GetStatus().PendingItemCount == 0);

        Assert.Equal(1, harness.Provider.BatchCallCount);
        Assert.Equal(callbacksAtDisable, harness.Provider.TotalCallbackCount);
        Assert.Empty(harness.ActiveEmbeddings);
    }

    [Fact]
    public async Task BackgroundService_ReconcilesContentProjectionSettingsAndModelChangesOnlyWhenChanged()
    {
        await using var harness = new IndexingHarness();
        var memory = harness.AddMemory("The project uses " + new string('c', 320));
        await harness.StartWorkerAsync();
        await WaitUntilAsync(() => harness.ActiveEmbeddings.Count == 1
                                   && harness.Worker.GetStatus().PendingItemCount == 0);

        var beforeContentChange = harness.Provider.BatchCallCount;
        harness.Store.UpdateMemory(memory.MemoryId, memory.Category, "The project uses " + new string('a', 320), "Changed source content.");
        await WaitUntilAsync(() => harness.Provider.BatchCallCount > beforeContentChange
                                   && harness.ActiveEmbeddings[memory.MemoryId].MemoryRevision
                                   == harness.Store.GetMemory(memory.MemoryId)!.MemoryRevision);

        var beforeProjectionSettingChange = harness.Provider.BatchCallCount;
        await harness.SetSettingAsync("semantic.maxCanonicalTextChars", "128");
        await WaitUntilAsync(() => harness.Provider.BatchCallCount > beforeProjectionSettingChange);

        var beforeBindingSettingChange = harness.Provider.BatchCallCount;
        harness.RuntimeCatalog.SetEmbeddingSettings("{\"vectorSpace\":\"alternate\"}");
        await WaitUntilAsync(() => harness.Provider.BatchCallCount > beforeBindingSettingChange);

        var beforeModelChange = harness.Provider.BatchCallCount;
        harness.RuntimeCatalog.SetEmbeddingModel("test-embedding-model-v2");
        await WaitUntilAsync(() => harness.Provider.BatchCallCount > beforeModelChange
                                   && harness.ActiveEmbeddings.Count == 1);
        await WaitUntilAsync(() => harness.Worker.GetStatus().PendingItemCount == 0);
        var currentCallCount = harness.Provider.BatchCallCount;

        await harness.WaitForMonitorTicksAsync(3);

        Assert.All(harness.ActiveEmbeddings.Values, embedding =>
            Assert.Equal("test-embedding-model-v2", embedding.ModelId));
        Assert.Equal(currentCallCount, harness.Provider.BatchCallCount);
    }

    [Fact]
    public async Task BackgroundService_ProviderSpaceIdentityChange_InvalidatesDurableGeneration()
    {
        await using var harness = new IndexingHarness();
        harness.AddMemory("Provider endpoint identity participates in the vector-space fingerprint.");
        await harness.StartWorkerAsync();
        await WaitUntilAsync(() => harness.ActiveEmbeddings.Count == 1
                                   && harness.Worker.GetStatus().PendingItemCount == 0);
        var previousCallCount = harness.Provider.BatchCallCount;

        harness.Provider.SpaceIdentity = "test-space-v2";

        await WaitUntilAsync(() => harness.Provider.BatchCallCount > previousCallCount
                                   && harness.Worker.GetStatus().PendingItemCount == 0);
        var currentCallCount = harness.Provider.BatchCallCount;
        await harness.WaitForMonitorTicksAsync(3);
        Assert.Equal(currentCallCount, harness.Provider.BatchCallCount);
    }

    [Fact]
    public async Task BackgroundService_ReusesSelectedProviderGenerationInsteadOfSessionLatest()
    {
        await using var harness = new IndexingHarness();
        var memory = harness.AddMemory("Each configured provider keeps its own active generation.");
        await harness.StartWorkerAsync();
        await WaitUntilAsync(() => harness.GetEmbeddings("test-embedding-provider").Count == 1);

        var alternate = new ControlledEmbeddingProvider("alternate-embedding-provider");
        harness.AddProvider(alternate);
        harness.RuntimeCatalog.SetEmbeddingProvider("alternate-embedding-provider");
        await WaitUntilAsync(() => harness.GetEmbeddings("alternate-embedding-provider").Count == 1);

        var originalCallCount = harness.Provider.BatchCallCount;
        harness.RuntimeCatalog.SetEmbeddingProvider("TEST-EMBEDDING-PROVIDER");
        await harness.WaitForMonitorTicksAsync(4);

        Assert.Equal(originalCallCount, harness.Provider.BatchCallCount);
        Assert.Single(harness.GetEmbeddings("TEST-EMBEDDING-PROVIDER"));
        Assert.Equal("test-embedding-provider", Assert.Single(harness.GetEmbeddings("test-embedding-provider")).Value.ProviderId);

        await harness.SetSettingAsync("semantic.reindex.mode", "never");
        var originalCallbacks = harness.Provider.TotalCallbackCount;
        var alternateCallbacks = alternate.TotalCallbackCount;
        SeedInactiveAndStagingEmbeddings(harness.Store.DatabasePath, memory.MemoryId, harness.Session.SessionId);
        harness.Store.SetState(memory.MemoryId, MemoryLocalStore.ForgottenState, "Retracted across providers.");
        await WaitUntilAsync(() => harness.GetEmbeddings("test-embedding-provider").Count == 0
                                   && harness.GetEmbeddings("alternate-embedding-provider").Count == 0
                                   && CountAllEmbeddings(harness.Store.DatabasePath, memory.MemoryId) == 0
                                   && CountStagingGenerations(harness.Store.DatabasePath, harness.Session.SessionId) == 0);
        Assert.Equal(originalCallbacks, harness.Provider.TotalCallbackCount);
        Assert.Equal(alternateCallbacks, alternate.TotalCallbackCount);
    }

    [Fact]
    public async Task BackgroundService_PartialFailure_RetriesOnlyRemainingWorkThenStopsCallingProvider()
    {
        await using var harness = new IndexingHarness();
        await harness.SetSettingAsync("semantic.batchSize", "1");
        harness.AddMemory("Existing current memory.");
        await harness.StartWorkerAsync();
        await WaitUntilAsync(() => harness.ActiveEmbeddings.Count == 1
                                   && harness.Worker.GetStatus().PendingItemCount == 0);

        const string firstNewContent = "First missing memory survives a later batch failure.";
        const string secondNewContent = "Second missing memory is retried after the partial failure.";
        harness.AddMemory(firstNewContent);
        harness.AddMemory(secondNewContent);
        harness.Provider.FailOnBatchCall = harness.Provider.BatchCallCount + 2;

        await WaitUntilAsync(() => harness.ActiveEmbeddings.Count == 3
                                   && harness.Worker.GetStatus().PendingItemCount == 0);

        var newMemoryRequestCounts = new[]
        {
            harness.Provider.CountRequestsContaining(firstNewContent),
            harness.Provider.CountRequestsContaining(secondNewContent),
        };
        Assert.Equal([1, 2], newMemoryRequestCounts.Order().ToArray());
        var completedCallCount = harness.Provider.BatchCallCount;
        await harness.WaitForMonitorTicksAsync(4);
        Assert.Equal(completedCallCount, harness.Provider.BatchCallCount);
    }

    [Fact]
    public async Task BackgroundService_RetractionPrunesEmbeddingPromptlyWithoutProviderCallEvenInNeverMode()
    {
        await using var harness = new IndexingHarness();
        var removed = harness.AddMemory("This memory will be retracted.");
        var retained = harness.AddMemory("This memory remains active.");
        await harness.StartWorkerAsync();
        await WaitUntilAsync(() => harness.ActiveEmbeddings.Count == 2
                                   && harness.Worker.GetStatus().PendingItemCount == 0);
        await harness.SetSettingAsync("semantic.reindex.mode", "never");
        var callCount = harness.Provider.BatchCallCount;
        var uploadedTextCount = harness.Provider.UploadedTextCount;
        var totalCallbackCount = harness.Provider.TotalCallbackCount;

        harness.Store.SetState(removed.MemoryId, MemoryLocalStore.ForgottenState, "Retracted by test.");
        await WaitUntilAsync(() => harness.ActiveEmbeddings.Count == 1);

        Assert.True(harness.ActiveEmbeddings.ContainsKey(retained.MemoryId));
        Assert.False(harness.ActiveEmbeddings.ContainsKey(removed.MemoryId));
        Assert.Equal(callCount, harness.Provider.BatchCallCount);
        Assert.Equal(uploadedTextCount, harness.Provider.UploadedTextCount);
        Assert.Equal(totalCallbackCount, harness.Provider.TotalCallbackCount);

        harness.Store.DeleteSessionData(harness.Session.SessionId);
        await harness.WaitForMonitorTicksAsync(2);
        Assert.Empty(harness.ActiveEmbeddings);
        Assert.Equal(callCount, harness.Provider.BatchCallCount);
        Assert.Equal(totalCallbackCount, harness.Provider.TotalCallbackCount);
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

        await harness.StartWorkerAsync();
        await WaitUntilAsync(() => harness.Worker.GetStatus().PendingItemCount == 0);

        Assert.True(harness.Worker.GetStatus().ProcessedItemCount >= 21);
    }

    [Fact]
    public async Task BackgroundService_DisposeAsync_AwaitsCanceledWorker()
    {
        await using var harness = new IndexingHarness();
        harness.AddMemory("A memory whose embedding blocks.");
        harness.Provider.Mode = EmbeddingProviderMode.WaitForCancellation;
        await harness.StartWorkerAsync();
        await harness.Provider.GenerationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await harness.Worker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(harness.Provider.CancellationObserved);
        Assert.False(harness.Worker.GetStatus().IsRunning);
        Assert.Equal(0, harness.Worker.GetStatus().PendingItemCount);
    }

    [Fact]
    public async Task ProviderRetirement_CancelsInFlightLeaseAndPreventsLaterCallbacks()
    {
        await using var harness = new IndexingHarness();
        harness.AddMemory("Provider retirement cancels this in-flight embedding request.");
        harness.Provider.Mode = EmbeddingProviderMode.WaitForCancellation;

        var indexing = harness.ReindexAsync();
        await harness.Provider.GenerationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        harness.RemoveProvider(harness.Provider);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => indexing);
        Assert.True(harness.Provider.CancellationObserved);
        var callbackCount = harness.Provider.TotalCallbackCount;

        Assert.Equal(0, await harness.ReindexAsync());
        Assert.Equal(callbackCount, harness.Provider.TotalCallbackCount);
    }

    [Fact]
    public async Task BackgroundService_TransientFailuresRetryWithBoundedBackoff()
    {
        await using var harness = new IndexingHarness();
        harness.AddMemory("A memory that succeeds after transient provider failures.");
        harness.Provider.FailuresRemaining = 2;

        await harness.StartWorkerAsync();
        await WaitUntilAsync(() => harness.ActiveEmbeddings.Count == 1);

        var status = harness.Worker.GetStatus();
        Assert.True(status.ProcessedItemCount >= 3);
        Assert.Null(status.LastFailureMessage);
    }

    [Fact]
    public async Task BackgroundService_PersistentFailure_EntersCooldownWithoutMonitorHotLoop()
    {
        await using var harness = new IndexingHarness(monitorInterval: TimeSpan.FromMilliseconds(500));
        harness.AddMemory("A memory whose provider remains unavailable.");
        harness.Provider.Mode = EmbeddingProviderMode.Fail;

        await harness.StartWorkerAsync();
        await WaitUntilAsync(() => harness.Worker.GetStatus().PendingItemCount == 0
                                   && harness.Worker.GetStatus().LastFailureMessage is not null);

        Assert.Equal(4, harness.Provider.BatchCallCount);
        await harness.WaitForMonitorTicksAsync(3);
        Assert.Equal(4, harness.Provider.BatchCallCount);
        Assert.Empty(harness.ActiveEmbeddings);
    }

    [Fact]
    public async Task Reindex_RestartResumesSourceFencedStagingGeneration()
    {
        await using var harness = new IndexingHarness();
        await harness.SetSettingAsync("semantic.batchSize", "1");
        const string firstContent = "The first staged embedding survives process restart.";
        const string secondContent = "Only the failed staged embedding is requested again.";
        harness.AddMemory(firstContent);
        harness.AddMemory(secondContent);
        harness.Provider.FailOnBatchCall = 2;

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.ReindexAsync());
        Assert.Empty(harness.ActiveEmbeddings);
        Assert.Equal(1, CountStagingEmbeddings(harness.Store.DatabasePath));

        await harness.RestartWorkerAsync();
        await harness.ReindexAsync();

        Assert.Equal(
            [1, 2],
            new[]
            {
                harness.Provider.CountRequestsContaining(firstContent),
                harness.Provider.CountRequestsContaining(secondContent),
            }.Order().ToArray());
        Assert.Equal(2, harness.ActiveEmbeddings.Count);
        Assert.Equal(0, CountStagingEmbeddings(harness.Store.DatabasePath));
    }

    private static int CountStagingEmbeddings(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM SessionMemoryEmbeddings embedding
            INNER JOIN SessionMemoryEmbeddingGenerations generation
                ON generation.GenerationId = embedding.GenerationId
            WHERE generation.State = 'Staging';
            """;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static int CountStagingGenerations(string databasePath, Guid sessionId)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM SessionMemoryEmbeddingGenerations WHERE SessionId = $sessionId AND State = 'Staging';";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static int CountAllEmbeddings(string databasePath, Guid memoryId)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM SessionMemoryEmbeddings WHERE MemoryId = $memoryId;";
        command.Parameters.AddWithValue("$memoryId", memoryId.ToString());
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void SeedInactiveAndStagingEmbeddings(string databasePath, Guid memoryId, Guid sessionId)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SessionMemoryEmbeddingGenerations
                (GenerationId, SessionId, ProviderId, ModelId, State, ExpectedMemoryCount, CreatedAtUtc, CompletedAtUtc,
                 ConfigurationFingerprint, SourceFingerprint, MaxCanonicalTextChars)
            VALUES
                ('inactive-generation', $sessionId, 'inactive-provider', 'inactive-model', 'Complete', 1, $now, $now,
                 'inactive-config', NULL, NULL),
                ('staging-generation', $sessionId, 'staging-provider', 'staging-model', 'Staging', 1, $now, NULL,
                 'staging-config', 'staging-source', 1200);
            INSERT INTO SessionMemoryEmbeddings
                (GenerationId, MemoryId, SessionId, ProviderId, ModelId, CanonicalTextHash, Dimensions, VectorJson,
                 CreatedAtUtc, UpdatedAtUtc, MemoryRevision)
            SELECT 'inactive-generation', MemoryId, SessionId, 'inactive-provider', 'inactive-model', CanonicalTextHash,
                   Dimensions, VectorJson, CreatedAtUtc, UpdatedAtUtc, MemoryRevision
            FROM SessionMemoryEmbeddings
            WHERE MemoryId = $memoryId
            LIMIT 1;
            INSERT INTO SessionMemoryEmbeddings
                (GenerationId, MemoryId, SessionId, ProviderId, ModelId, CanonicalTextHash, Dimensions, VectorJson,
                 CreatedAtUtc, UpdatedAtUtc, MemoryRevision)
            SELECT 'staging-generation', MemoryId, SessionId, 'staging-provider', 'staging-model', CanonicalTextHash,
                   Dimensions, VectorJson, CreatedAtUtc, UpdatedAtUtc, MemoryRevision
            FROM SessionMemoryEmbeddings
            WHERE MemoryId = $memoryId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$memoryId", memoryId.ToString());
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
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
        private readonly RegressionTestExtensionCatalog _catalog;
        private readonly int _queueCapacity;
        private readonly TimeSpan _monitorInterval;
        private long _runtimeGeneration;

        public IndexingHarness(int queueCapacity = 8, TimeSpan? monitorInterval = null)
        {
            _queueCapacity = queueCapacity;
            _monitorInterval = monitorInterval ?? TimeSpan.FromMilliseconds(40);
            Session = new AgentSessionRecord(Guid.NewGuid(), "Test Session", AgentSessionState.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            var profile = new AgentProfileRecord(
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
            _context.MutableSettings.SetValueAsync("semantic.reindex.mode", "eager").GetAwaiter().GetResult();
            _catalog = new RegressionTestExtensionCatalog();
            RuntimeCatalog = new TestRuntimeCatalog(Session, profile);
            _catalog.AddProvider(AgentRpcServices.RuntimeCatalogs, RuntimeCatalog);
            Provider = new ControlledEmbeddingProvider("test-embedding-provider");
            _catalog.AddProvider(AgentRpcServices.EmbeddingProviders, Provider);
            Store = new MemoryLocalStore(_context);
            (Backend, Worker) = CreateIndexingServices();
        }

        public AgentSessionRecord Session { get; }
        public AgentProfileRecord Profile => RuntimeCatalog.Profile;
        public TestRuntimeCatalog RuntimeCatalog { get; }
        public ControlledEmbeddingProvider Provider { get; }
        public MemoryLocalStore Store { get; private set; }
        public SemanticMemoryRetrievalBackend Backend { get; private set; }
        public SemanticMemoryIndexingBackgroundService Worker { get; private set; }

        public IReadOnlyDictionary<Guid, StoredMemoryEmbeddingRecord> ActiveEmbeddings
            => Store.ListEmbeddings(Session.SessionId, "test-embedding-provider", Profile.EmbeddingModelId!);

        public IReadOnlyDictionary<Guid, StoredMemoryEmbeddingRecord> GetEmbeddings(string providerId)
            => Store.ListEmbeddings(Session.SessionId, providerId, Profile.EmbeddingModelId!);

        public void AddProvider(ControlledEmbeddingProvider provider)
            => _catalog.AddProvider(AgentRpcServices.EmbeddingProviders, provider);

        public void RemoveProvider(ControlledEmbeddingProvider provider)
            => _catalog.RemoveProvider(provider);

        public StoredMemoryRecord AddMemory(string content)
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

        public MemoryLocalStore ReopenStore() => new(_context);

        public Task SetSettingAsync(string key, string value) => _context.MutableSettings.SetValueAsync(key, value);

        public Task WaitForMonitorTicksAsync(int count)
            => Task.Delay(TimeSpan.FromMilliseconds(_monitorInterval.TotalMilliseconds * count + 50));

        public async Task StartWorkerAsync()
        {
            await Worker.StartAsync();
            await Worker.CommitGenerationAsync(new PackageRuntimeGeneration(
                Guid.NewGuid(),
                Interlocked.Increment(ref _runtimeGeneration)));
        }

        public async Task RestartWorkerAsync()
        {
            await Worker.DisposeAsync();
            Store = new MemoryLocalStore(_context);
            (Backend, Worker) = CreateIndexingServices();
        }

        public Task<int> ReindexAsync(CancellationToken cancellationToken = default)
            => Backend.ReindexSessionAsync(
                Session.SessionId,
                Profile.ProfileId,
                Store.ListMemories(Session.SessionId),
                cancellationToken);

        private (SemanticMemoryRetrievalBackend Backend, SemanticMemoryIndexingBackgroundService Worker) CreateIndexingServices()
        {
            var settings = new MemorySemanticSettingsService(_context);
            var resolver = new SemanticModelRuntimeResolver(
                _catalog,
                settings,
                configurationCacheDuration: TimeSpan.FromMilliseconds(500));
            var backend = new SemanticMemoryRetrievalBackend(Store, resolver, settings);
            var worker = new SemanticMemoryIndexingBackgroundService(
                Store,
                resolver,
                settings,
                backend,
                new SemanticMemoryMetricsService(),
                _queueCapacity,
                _monitorInterval);
            return (backend, worker);
        }

        public async ValueTask DisposeAsync()
        {
            await Worker.DisposeAsync();
            _catalog.Dispose();
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
        WaitForRelease,
    }

    private sealed class ControlledEmbeddingProvider(string providerId) :
        IAgentEmbeddingProvider,
        IAgentEmbeddingSpaceIdentityProvider
    {
        private readonly ConcurrentQueue<string> _requests = new();
        private readonly AgentEmbeddingProviderDescriptor _descriptor = new(providerId, "Controlled Embeddings", []);
        private int _descriptorCallCount;
        private int _availableModelsCallCount;
        private int _identityCallCount;
        private int _readinessCallCount;
        private int _singleCallCount;
        private int _batchCallCount;
        private int _uploadedTextCount;

        public AgentEmbeddingProviderDescriptor Descriptor
        {
            get
            {
                Interlocked.Increment(ref _descriptorCallCount);
                return _descriptor;
            }
        }
        public EmbeddingProviderMode Mode { get; set; }
        public int VectorVersion { get; set; } = 1;
        public int FailuresRemaining { get; set; }
        public int FailOnBatchCall { get; set; }
        public bool CancellationObserved { get; private set; }
        public TaskCompletionSource GenerationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource GenerationRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int BatchCallCount => Volatile.Read(ref _batchCallCount);
        public int UploadedTextCount => Volatile.Read(ref _uploadedTextCount);
        public int TotalCallbackCount => Volatile.Read(ref _descriptorCallCount)
                                         + Volatile.Read(ref _availableModelsCallCount)
                                         + Volatile.Read(ref _identityCallCount)
                                         + Volatile.Read(ref _readinessCallCount)
                                         + Volatile.Read(ref _singleCallCount)
                                         + Volatile.Read(ref _batchCallCount);
        public string SpaceIdentity { get; set; } = "test-space-v1";

        public void ReleaseGeneration() => GenerationRelease.TrySetResult();

        public ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _availableModelsCallCount);
            return ValueTask.FromResult<IReadOnlyList<AgentEmbeddingModelDescriptor>>(
            [
                new AgentEmbeddingModelDescriptor("test-embedding-model", "Test Embedding Model", 2, true),
                new AgentEmbeddingModelDescriptor("test-embedding-model-v2", "Test Embedding Model V2", 2),
            ]);
        }

        public ValueTask<string> GetEmbeddingSpaceIdentityAsync(
            string modelId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _identityCallCount);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(SpaceIdentity);
        }

        public ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readinessCallCount);
            return ValueTask.FromResult(new AgentEmbeddingProviderReadiness(providerId, AgentProviderReadinessStatus.Ready, "Ready."));
        }

        public async ValueTask<AgentEmbeddingGenerationResult?> GenerateEmbeddingAsync(
            string modelId,
            string text,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _singleCallCount);
            return (await GenerateEmbeddingsAsync(modelId, [text], cancellationToken))[0];
        }

        public async ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateEmbeddingsAsync(
            string modelId,
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
        {
            var callNumber = Interlocked.Increment(ref _batchCallCount);
            foreach (var text in texts)
            {
                _requests.Enqueue(text);
            }
            GenerationStarted.TrySetResult();
            if (FailOnBatchCall == callNumber)
            {
                FailOnBatchCall = 0;
                throw new InvalidOperationException("Configured partial embedding generation failure.");
            }
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("Transient embedding generation failure.");
            }

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
            if (Mode == EmbeddingProviderMode.WaitForRelease)
            {
                await GenerationRelease.Task.WaitAsync(cancellationToken);
            }

            Interlocked.Add(ref _uploadedTextCount, texts.Count);
            return texts.Select(text => (AgentEmbeddingGenerationResult?)new AgentEmbeddingGenerationResult(
                modelId,
                [VectorVersion, text.Length])).ToArray();
        }

        public int CountRequestsContaining(string value)
            => _requests.Count(request => request.Contains(value, StringComparison.Ordinal));
    }

    private sealed class TestRuntimeCatalog : IAgentRuntimeCatalog
    {
        private readonly AgentSessionRecord _session;
        private string? _embeddingSettings;

        public TestRuntimeCatalog(AgentSessionRecord session, AgentProfileRecord profile)
        {
            _session = session;
            Profile = profile;
        }

        public AgentProfileRecord Profile { get; private set; }

        public event Action<Guid>? SessionChanged { add { } remove { } }
        public event Action<Guid, AgentTurnRecord>? TurnChanged { add { } remove { } }
        public event Action<string>? ProfileChanged;

        public IReadOnlyList<AgentSessionRecord> ListSessions() => [_session];
        public IReadOnlyList<AgentSessionRecord> ListSessionsForProfile(string profileId) => profileId == Profile.ProfileId ? [_session] : [];
        public IReadOnlyList<AgentSessionRecord> ListSessionsForWorkspace(string workspaceId) => [];
        public AgentSessionRecord? GetSession(Guid sessionId) => sessionId == _session.SessionId ? _session : null;
        public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces() => [];
        public AgentWorkspaceRecord? GetWorkspace(string workspaceId) => null;
        public AgentProfileRecord? GetSessionProfile(Guid sessionId) => sessionId == _session.SessionId ? Profile : null;
        public AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId) => null;
        public AgentSessionContextCheckpointRecord? GetLatestSessionContextCheckpoint(Guid sessionId) => null;
        public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId) => null;
        public IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit) => [];
        public IReadOnlyList<AgentTurnRecord> ListTurnsBefore(Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit) => [];
        public IReadOnlyList<AgentTurnRecord> ListTurnsAfter(Guid sessionId, DateTimeOffset afterCreatedAtUtc, Guid afterTurnId, int limit) => [];
        public IReadOnlyList<AgentProfileRecord> ListProfiles() => [Profile];
        public AgentProfileRecord? GetProfile(string profileId) => profileId == Profile.ProfileId ? Profile : null;
        public AgentProfileModelBindingRecord? GetSessionModelBinding(Guid sessionId, string capabilityKind)
            => sessionId == _session.SessionId ? GetModelBinding(Profile.ProfileId, capabilityKind) : null;

        public AgentProfileModelBindingRecord? GetModelBinding(string profileId, string capabilityKind)
            => profileId == Profile.ProfileId
               && capabilityKind == AgentModelCapabilityKinds.Embedding
               && Profile.EmbeddingProviderId is { } providerId
               && Profile.EmbeddingModelId is { } modelId
                ? new AgentProfileModelBindingRecord(
                    Profile.ProfileId,
                    capabilityKind,
                    providerId,
                    modelId,
                    _embeddingSettings,
                    Profile.UpdatedAtUtc)
                : null;

        public void SetEmbeddingSettings(string? settings)
        {
            _embeddingSettings = settings;
            Profile = Profile with { UpdatedAtUtc = DateTimeOffset.UtcNow };
            ProfileChanged?.Invoke(Profile.ProfileId);
        }

        public void SetEmbeddingModel(string modelId)
        {
            Profile = Profile with
            {
                EmbeddingModelId = modelId,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            ProfileChanged?.Invoke(Profile.ProfileId);
        }

        public void SetEmbeddingProvider(string providerId)
        {
            Profile = Profile with
            {
                EmbeddingProviderId = providerId,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            ProfileChanged?.Invoke(Profile.ProfileId);
        }
    }

    private sealed class TestPackageContext : IPackageContext
    {
        public TestPackageContext(string rootPath)
        {
            Directory.CreateDirectory(rootPath);
            ContentRootPath = rootPath;
            Storage = new TestPackageStorageContext(rootPath);
            MutableSettings = new TestPackageSettings();
        }

        public string PackageId => "test.package.agent.memory.semantic";
        public string Version => "1.0.0";
        public string ContentRootPath { get; }
        public IPackageStorageContext Storage { get; }
        public TestPackageSettings MutableSettings { get; }
        public IPackageSettings Settings => MutableSettings;
        public IPackageSecrets Secrets { get; } = new TestPackageSecrets();
        public IPackageLogging Logging { get; } = NullPackageLogging.Instance;
    }

    private sealed class TestPackageStorageContext : IPackageStorageContext
    {
        public TestPackageStorageContext(string rootPath)
        {
            Directory.CreateDirectory(rootPath);
            Files = new TestPackageFileStore(rootPath);
            RoleLocalWorkspace = new TestPackageRoleLocalWorkspace(rootPath);
        }

        public IPackageFileStore Files { get; }
        public IPackageKeyValueStore State { get; } = new TestPackageKeyValueStore();
        public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; }
    }

    private sealed class TestPackageFileStore(string rootPath) : TestPackageFileStoreBase(rootPath);

    private sealed class TestPackageKeyValueStore : IPackageKeyValueStore
    {
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.FromResult<string?>(null);
        }
        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            TestPackageStorageGuards.Value(value);
            return Task.CompletedTask;
        }
        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.FromResult(false);
        }
        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Prefix(prefix);
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    private sealed class TestPackageSettings : IPackageSettings
    {
        private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.FromResult(_values.GetValueOrDefault(key));
        }

        public Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default)
            => GetValueAsync(key, cancellationToken);

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            TestPackageStorageGuards.Value(value);
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            _values.TryRemove(key, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class TestPackageSecrets : InMemoryPackageSecrets;
}
