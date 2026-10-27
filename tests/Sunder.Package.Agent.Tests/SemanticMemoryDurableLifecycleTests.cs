using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Sunder.Package.Agent.Tests;

extern alias MemoryWorker;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Services;
using MemoryWorker::Sunder.Package.Agent.Contracts.Models;
using AgentDurableLifecycleIntegrityException = MemoryWorker::Sunder.Package.Agent.Contracts.Contracts.AgentDurableLifecycleIntegrityException;
using AgentDurableLifecycleEventEnvelope = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentDurableLifecycleEventEnvelope;
using AgentDurableLifecycleEventPayload = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentDurableLifecycleEventPayload;
using AgentLifecycleEventKind = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentLifecycleEventKind;
using AgentMemoryConsistencyBarrier = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentMemoryConsistencyBarrier;
using AgentMessageRole = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentMessageRole;
using AgentPromptContextPlan = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentPromptContextPlan;
using AgentPromptContextRequest = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentPromptContextRequest;
using AgentRunContextRecord = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentRunContextRecord;
using AgentRunStatus = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentRunStatus;
using AgentSessionContextRecord = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentSessionContextRecord;
using AgentSessionState = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentSessionState;
using AgentTurnContextRecord = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTurnContextRecord;
using AgentTurnItemKind = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTurnItemKind;
using AgentTurnItemRecord = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTurnItemRecord;
using AgentTurnKind = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTurnKind;
using AgentTurnRecord = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTurnRecord;
using IEmbeddingProviderService = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Embedding_Provider.IEmbeddingProviderService;
using IRuntimeCatalogService = MemoryWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Runtime_Catalog.IRuntimeCatalogService;

public sealed class SemanticMemoryDurableLifecycleTests
{
    [Fact]
    public void DuplicateEvent_IsSuccessWithoutDuplicateEvidence_AndMismatchFailsPermanently()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new MemoryLocalStore(scope.Context);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var lifecycleEvent = UserEvent("event-duplicate", sessionId, turnId, "Remember that the project uses cobalt.");
        var candidate = Candidate(turnId, "project-fact", "The project uses cobalt.");

        var first = store.ProcessDurableLifecycleEvent(lifecycleEvent, [candidate]);
        var duplicate = store.ProcessDurableLifecycleEvent(lifecycleEvent, [candidate]);

        var memory = Assert.Single(store.ListMemories(sessionId));
        Assert.False(first.IsDuplicate);
        Assert.True(duplicate.IsDuplicate);
        Assert.Single(store.ListEvidence(memory.MemoryId));

        var mismatched = UserEvent("event-other-payload", sessionId, turnId, "Remember that the project uses amber.") with
        {
            EventId = lifecycleEvent.EventId,
        };
        var integrity = Assert.Throws<AgentDurableLifecycleIntegrityException>(() =>
            store.ProcessDurableLifecycleEvent(mismatched, [Candidate(turnId, "project-fact", "The project uses amber.")]));
        Assert.Contains(lifecycleEvent.EventId, integrity.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ContentErasureReceipt_AcknowledgesPreviouslyReceivedOriginalHashAsDuplicate()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new MemoryLocalStore(scope.Context);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var original = UserEvent(
            "event-erased-after-receipt",
            sessionId,
            turnId,
            "Remember that the project uses cobalt.");
        store.ProcessDurableLifecycleEvent(
            original,
            [Candidate(turnId, "project-fact", "The project uses cobalt.")]);
        var erased = Envelope(
            original.EventId,
            original.Kind,
            Payload() with { ContentErased = true }) with
        {
            SourceKey = original.SourceKey,
            Sequence = original.Sequence,
            OrderingKey = original.OrderingKey,
            OriginalPayloadHash = original.PayloadHash,
        };

        var result = store.ProcessDurableLifecycleEvent(erased, []);

        Assert.True(result.IsDuplicate);
        Assert.Single(store.ListMemories(sessionId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeletionMaintenance_FailureRemainsPendingAndDuplicateRetries(bool useErasedDuplicate)
    {
        using var scope = RegressionTestPackageScope.Create();
        var maintenance = new FailOncePhysicalMaintenance();
        var store = new MemoryLocalStore(scope.Context, maintenance);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        store.ProcessDurableLifecycleEvent(
            UserEvent("maintenance-source", sessionId, turnId, "Remember the physical deletion sentinel."),
            [Candidate(turnId, "project-fact", "Remember the physical deletion sentinel.")]);
        var deletion = SessionDeletedEvent("maintenance-delete", sessionId);

        Assert.Throws<InvalidOperationException>(() =>
            store.ProcessDurableLifecycleEvent(deletion, []));
        Assert.Empty(store.ListMemories(sessionId, includeInactive: true));
        Assert.Equal("Pending", ReadDeletionMaintenanceState(store.DatabasePath, deletion.EventId));

        var restarted = new MemoryLocalStore(scope.Context, maintenance);
        var retry = useErasedDuplicate ? ErasedDuplicate(deletion) : deletion;
        var result = restarted.ProcessDurableLifecycleEvent(retry, []);

        Assert.True(result.IsDuplicate);
        Assert.Equal(2, maintenance.AttemptCount);
        Assert.Equal("Completed", ReadDeletionMaintenanceState(store.DatabasePath, deletion.EventId));
    }

    [Fact]
    public void SessionDeletion_PhysicallyErasesOrdinaryFullTextAndWalCanaries()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new MemoryLocalStore(scope.Context);
        var sessionId = Guid.NewGuid();
        var ordinarySecret = "ordinary-erasure-sentinel-" + Guid.NewGuid().ToString("N");
        var fullTextSecret = "full-text-erasure-sentinel-" + Guid.NewGuid().ToString("N");
        var walSecret = "wal-erasure-sentinel-" + Guid.NewGuid().ToString("N");
        using var connection = new SqliteConnection(MemoryDatabase.CreateConnectionString(store.DatabasePath));
        connection.Open();
        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                PRAGMA wal_autocheckpoint = 0;
                INSERT INTO SessionMemoryEvidence (
                    EvidenceId, MemoryId, SessionId, SourceTurnId, EvidenceText, CreatedAtUtc)
                VALUES ('ordinary-canary', 'ordinary-memory', $sessionId, NULL, $ordinarySecret, $now);
                INSERT INTO SessionMemorySearch (MemoryId, SessionId, Category, Content, EvidenceText, State)
                VALUES ('fts-canary', $sessionId, 'project-fact', $fullTextSecret, $fullTextSecret, 'Active');
                PRAGMA wal_checkpoint(TRUNCATE);
                """;
            seed.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            seed.Parameters.AddWithValue("$ordinarySecret", ordinarySecret);
            seed.Parameters.AddWithValue("$fullTextSecret", fullTextSecret);
            seed.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            seed.ExecuteNonQuery();
        }
        Assert.True(FileContains(store.DatabasePath, ordinarySecret));
        Assert.True(DatabaseFilesContain(store.DatabasePath, fullTextSecret));

        using (var seedWal = connection.CreateCommand())
        {
            seedWal.CommandText = """
                INSERT INTO SessionMemoryEvidence (
                    EvidenceId, MemoryId, SessionId, SourceTurnId, EvidenceText, CreatedAtUtc)
                VALUES ('wal-canary', 'wal-memory', $sessionId, NULL, $walSecret, $now);
                """;
            seedWal.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            seedWal.Parameters.AddWithValue("$walSecret", walSecret);
            seedWal.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            seedWal.ExecuteNonQuery();
        }
        Assert.True(FileContains(store.DatabasePath + "-wal", walSecret));

        store.ProcessDurableLifecycleEvent(SessionDeletedEvent("canary-delete", sessionId), []);

        Assert.False(DatabaseFilesContain(store.DatabasePath, ordinarySecret));
        Assert.False(DatabaseFilesContain(store.DatabasePath, fullTextSecret));
        Assert.False(DatabaseFilesContain(store.DatabasePath, walSecret));
    }

    [Fact]
    public void RollbackBeforeAdd_TombstoneSuppressesLatePromotion()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new MemoryLocalStore(scope.Context);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        store.ProcessDurableLifecycleEvent(RollbackEvent("rollback-first", sessionId, [turnId]), []);

        store.ProcessDurableLifecycleEvent(
            UserEvent("late-add", sessionId, turnId, "Remember that the project uses cobalt."),
            [Candidate(turnId, "project-fact", "The project uses cobalt.")]);

        Assert.Empty(store.ListMemories(sessionId, includeInactive: true));
    }

    [Fact]
    public void Rollback_RecomputesMergedMemoryFromSurvivingContribution()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new MemoryLocalStore(scope.Context);
        var sessionId = Guid.NewGuid();
        var firstTurnId = Guid.NewGuid();
        var secondTurnId = Guid.NewGuid();
        const string firstContent = "The project uses cobalt release checklist.";
        const string secondContent = "The project uses cobalt release checklist for deployments.";
        store.ProcessDurableLifecycleEvent(
            UserEvent("merge-first", sessionId, firstTurnId, firstContent),
            [Candidate(firstTurnId, "project-fact", firstContent)]);
        store.ProcessDurableLifecycleEvent(
            UserEvent("merge-second", sessionId, secondTurnId, secondContent),
            [Candidate(secondTurnId, "project-fact", secondContent)]);
        Assert.Equal(secondContent, Assert.Single(store.ListMemories(sessionId)).Content);

        store.ProcessDurableLifecycleEvent(
            RollbackEvent("merge-rollback", sessionId, [secondTurnId]),
            []);

        var surviving = Assert.Single(store.ListMemories(sessionId));
        Assert.Equal(firstContent, surviving.Content);
        Assert.Equal(firstTurnId, surviving.SourceTurnId);
        Assert.Single(store.ListEvidence(surviving.MemoryId));
    }

    [Fact]
    public void Rollback_PreservesExplicitlyEditedMemory()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new MemoryLocalStore(scope.Context);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        store.ProcessDurableLifecycleEvent(
            UserEvent("manual-source", sessionId, turnId, "Remember that the project uses cobalt."),
            [Candidate(turnId, "project-fact", "The project uses cobalt.")]);
        var promoted = Assert.Single(store.ListMemories(sessionId));
        const string editedContent = "The project uses the manually verified cobalt checklist.";
        store.UpdateMemory(promoted.MemoryId, promoted.Category, editedContent, "Verified manually.");

        store.ProcessDurableLifecycleEvent(RollbackEvent("manual-rollback", sessionId, [turnId]), []);

        var preserved = Assert.IsType<StoredMemoryRecord>(store.GetMemory(promoted.MemoryId));
        Assert.True(preserved.IsManual);
        Assert.Equal(editedContent, preserved.Content);
    }

    [Fact]
    public void DurablePromotion_AttachesToManualIdentityWithoutOverwritingManualState()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new MemoryLocalStore(scope.Context);
        var sessionId = Guid.NewGuid();
        var sourceTurnId = Guid.NewGuid();
        const string content = "The project uses the manually verified cobalt checklist.";
        var manual = store.UpsertMemory(new MemoryUpsertRequest(
            sessionId,
            "project-fact",
            content,
            SemanticMemoryTextHelpers.Normalize(content),
            "Manually verified evidence.",
            sourceTurnId,
            true,
            0.99f,
            0.99f,
            AgentMemoryProvenance.User));
        var promotionTurnId = Guid.NewGuid();

        store.ProcessDurableLifecycleEvent(
            UserEvent("manual-identity-promotion", sessionId, promotionTurnId, content),
            [Candidate(promotionTurnId, "project-fact", content)]);

        var preserved = Assert.Single(store.ListMemories(sessionId));
        Assert.Equal(manual.MemoryId, preserved.MemoryId);
        Assert.True(preserved.IsManual);
        Assert.True(preserved.IsPinned);
        Assert.Equal(content, preserved.Content);
        Assert.Equal("Manually verified evidence.", preserved.EvidenceText);
        Assert.Equal(2, store.ListEvidence(manual.MemoryId).Count);

        store.ProcessDurableLifecycleEvent(
            RollbackEvent("manual-identity-rollback", sessionId, [promotionTurnId]),
            []);
        preserved = Assert.IsType<StoredMemoryRecord>(store.GetMemory(manual.MemoryId));
        Assert.True(preserved.IsManual);
        Assert.True(preserved.IsPinned);
        Assert.Equal(content, preserved.Content);
        Assert.Equal("Manually verified evidence.", preserved.EvidenceText);
    }

    [Fact]
    public void DurablePromotion_MergesSessionScopedContributionsAcrossProfiles()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new MemoryLocalStore(scope.Context);
        var sessionId = Guid.NewGuid();
        var firstTurnId = Guid.NewGuid();
        var secondTurnId = Guid.NewGuid();
        const string first = "The project uses cobalt release checklist.";
        const string second = "The project uses cobalt release checklist for production deployments.";
        store.ProcessDurableLifecycleEvent(
            UserEvent("profile-one", sessionId, firstTurnId, first, profileId: "profile.one"),
            [Candidate(firstTurnId, "project-fact", first)]);

        store.ProcessDurableLifecycleEvent(
            UserEvent("profile-two", sessionId, secondTurnId, second, profileId: "profile.two"),
            [Candidate(secondTurnId, "project-fact", second)]);

        var merged = Assert.Single(store.ListMemories(sessionId));
        Assert.Equal(second, merged.Content);
        Assert.Equal(2, store.ListEvidence(merged.MemoryId).Count);
    }

    [Fact]
    public void WorkspaceTombstones_AreScopedToDeleteRecreateDeleteIncarnations()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new MemoryLocalStore(scope.Context);
        const string workspaceId = "workspace.reused";
        const string firstIncarnation = "incarnation-one";
        const string secondIncarnation = "incarnation-two";
        var firstSessionId = Guid.NewGuid();
        var firstTurnId = Guid.NewGuid();
        store.ProcessDurableLifecycleEvent(
            UserEvent(
                "workspace-first-add",
                firstSessionId,
                firstTurnId,
                "The project uses cobalt.",
                workspaceId: workspaceId,
                workspaceIncarnationId: firstIncarnation),
            [Candidate(firstTurnId, "project-fact", "The project uses cobalt.")]);
        store.ProcessDurableLifecycleEvent(
            WorkspaceDeletedEvent(
                "workspace-first-delete",
                workspaceId,
                firstIncarnation,
                [firstSessionId]),
            []);
        Assert.Empty(store.ListMemories(firstSessionId, includeInactive: true));

        var blockedSessionId = Guid.NewGuid();
        var blockedTurnId = Guid.NewGuid();
        store.ProcessDurableLifecycleEvent(
            UserEvent(
                "workspace-late-old-add",
                blockedSessionId,
                blockedTurnId,
                "The project uses stale amber.",
                workspaceId: workspaceId,
                workspaceIncarnationId: firstIncarnation),
            [Candidate(blockedTurnId, "project-fact", "The project uses stale amber.")]);
        Assert.Empty(store.ListMemories(blockedSessionId, includeInactive: true));

        var secondSessionId = Guid.NewGuid();
        var secondTurnId = Guid.NewGuid();
        store.ProcessDurableLifecycleEvent(
            UserEvent(
                "workspace-second-add",
                secondSessionId,
                secondTurnId,
                "The project uses fresh amber.",
                workspaceId: workspaceId,
                workspaceIncarnationId: secondIncarnation),
            [Candidate(secondTurnId, "project-fact", "The project uses fresh amber.")]);
        Assert.Single(store.ListMemories(secondSessionId));

        store.ProcessDurableLifecycleEvent(
            WorkspaceDeletedEvent(
                "workspace-second-delete",
                workspaceId,
                secondIncarnation,
                [secondSessionId]),
            []);
        Assert.Empty(store.ListMemories(secondSessionId, includeInactive: true));
    }

    [Fact]
    public void SessionDeletion_TombstonesDeletesAndSuppressesLateAdd()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new MemoryLocalStore(scope.Context);
        var sessionId = Guid.NewGuid();
        var firstTurnId = Guid.NewGuid();
        store.ProcessDurableLifecycleEvent(
            UserEvent("delete-source", sessionId, firstTurnId, "Remember that the project uses cobalt."),
            [Candidate(firstTurnId, "project-fact", "The project uses cobalt.")]);
        Assert.Single(store.ListMemories(sessionId));

        store.ProcessDurableLifecycleEvent(SessionDeletedEvent("session-delete", sessionId), []);
        Assert.Empty(store.ListMemories(sessionId, includeInactive: true));

        var lateTurnId = Guid.NewGuid();
        store.ProcessDurableLifecycleEvent(
            UserEvent("delete-late-add", sessionId, lateTurnId, "Remember that the project uses amber."),
            [Candidate(lateTurnId, "project-fact", "The project uses amber.")]);
        Assert.Empty(store.ListMemories(sessionId, includeInactive: true));
    }

    [Fact]
    public void EmbeddingPersistence_RejectsStaleMemoryRevisionAndCanonicalHash()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new MemoryLocalStore(scope.Context);
        var sessionId = Guid.NewGuid();
        var memory = store.UpsertMemory(new MemoryUpsertRequest(
            sessionId,
            "project-fact",
            "The project uses cobalt.",
            "the project uses cobalt.",
            "Direct user evidence.",
            Guid.NewGuid(),
            false,
            0.8f,
            0.9f,
            AgentMemoryProvenance.User));
        const int maxCanonicalChars = 4096;
        var hash = CanonicalHash(memory, maxCanonicalChars);
        var embedding = new StoredMemoryEmbeddingRecord(
            memory.MemoryId,
            sessionId,
            "provider",
            "model",
            hash,
            2,
            [0.25f, 0.75f],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow)
        {
            MemoryRevision = memory.MemoryRevision,
        };
        store.UpdateMemory(memory.MemoryId, memory.Category, "The project uses amber.", "Changed while embedding.");

        Assert.False(store.TryUpsertEmbedding(
            embedding,
            memory.MemoryRevision,
            hash,
            maxCanonicalChars));
        Assert.Null(store.GetEmbedding(memory.MemoryId, "provider", "model"));
    }

    [Fact]
    public async Task Recall_WithMissingRollbackReceipt_FailsClosedWithoutBlocking()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new MemoryLocalStore(scope.Context);
        var sessionId = Guid.NewGuid();
        store.UpsertMemory(new MemoryUpsertRequest(
            sessionId,
            "standing-instruction",
            "Always use the cobalt release checklist.",
            "always use the cobalt release checklist.",
            "Direct user evidence.",
            Guid.NewGuid(),
            true,
            0.9f,
            0.95f,
            AgentMemoryProvenance.User));
        var settings = new MemorySemanticSettingsService(scope.Context);
        using var catalog = new RegressionTestExtensionCatalog();
        using var resolver = new SemanticModelRuntimeResolver(
            catalog.Memory().Providers<IRuntimeCatalogService>(),
            catalog.Memory().Providers<IEmbeddingProviderService>(),
            settings);
        var backend = new SemanticMemoryRetrievalBackend(store, resolver, settings);
        var recall = new SemanticMemoryRecallService(store, backend, new SemanticMemoryMetricsService());
        var barrierEvent = RollbackEvent("recall-barrier", sessionId, []);
        var request = RecallRequest(sessionId) with
        {
            MemoryConsistencyBarrier = new AgentMemoryConsistencyBarrier
            {
                EventId = barrierEvent.EventId,
                PayloadHash = barrierEvent.PayloadHash,
            },
        };

        var beforeReceipt = await recall.RecallAsync(request, RecallPlan());
        store.ProcessDurableLifecycleEvent(barrierEvent, []);
        var afterReceipt = await recall.RecallAsync(request, RecallPlan());

        Assert.Null(beforeReceipt);
        Assert.NotNull(afterReceipt);
        Assert.NotEmpty(afterReceipt.Entries);
    }

    private static AgentPromptContextRequest RecallRequest(Guid sessionId)
    {
        var session = SessionContext(sessionId);
        var run = RunContext();
        var turn = new AgentTurnContextRecord
        {
            Session = session,
            Run = run,
            UserMessage = "What release checklist should I use?",
        };
        return new AgentPromptContextRequest
        {
            AvailableTools = [],
            ContextPlan = new AgentPromptContextPlan
            {
                Intent = "standing-instruction",
                MaxChars = 1200,
                MaxEntryCount = 4,
                PreferredCategories = ["standing-instruction"],
                QueryText = "cobalt release checklist",
            },
            RecentLiveBufferTurns = [],
            Run = run,
            Session = session,
            TranscriptEpoch = 1,
            Turn = turn,
            Turns = [],
        };
    }

    private static AgentDurableLifecycleEventEnvelope UserEvent(
        string id,
        Guid sessionId,
        Guid turnId,
        string text,
        string profileId = "profile.semantic",
        string workspaceId = "workspace.semantic",
        string? workspaceIncarnationId = null)
    {
        var session = SessionContext(sessionId, profileId);
        var run = RunContext();
        var turn = TextTurn(sessionId, turnId, text);
        return Envelope(
            id,
            AgentLifecycleEventKind.UserTurnAdded,
            Payload() with
            {
                Session = session,
                Run = run,
                UserMessage = text,
                Turns = [turn],
                RecentLiveBufferTurns = [turn],
                TriggerTurn = turn,
                SessionId = sessionId,
                RootSessionId = sessionId,
                WorkspaceId = workspaceId,
                WorkspaceIncarnationId = workspaceIncarnationId,
            });
    }

    private static AgentDurableLifecycleEventEnvelope RollbackEvent(
        string id,
        Guid sessionId,
        IReadOnlyList<Guid> deletedTurnIds)
        => Envelope(
            id,
            AgentLifecycleEventKind.TranscriptRolledBack,
            Payload() with
            {
                SessionId = sessionId,
                RootSessionId = sessionId,
                WorkspaceId = "workspace.semantic",
                DeletedTurnCount = deletedTurnIds.Count,
                DeletedTurnIds = deletedTurnIds,
            });

    private static AgentDurableLifecycleEventEnvelope SessionDeletedEvent(string id, Guid sessionId)
        => Envelope(
            id,
            AgentLifecycleEventKind.SessionDeleted,
            Payload() with
            {
                SessionId = sessionId,
                RootSessionId = sessionId,
                WorkspaceId = "workspace.semantic",
                DeletedSessionCount = 1,
                DeletedSessionIds = [sessionId],
            });

    private static AgentDurableLifecycleEventEnvelope WorkspaceDeletedEvent(
        string id,
        string workspaceId,
        string workspaceIncarnationId,
        IReadOnlyList<Guid> sessionIds)
        => Envelope(
            id,
            AgentLifecycleEventKind.WorkspaceDeleted,
            Payload() with
            {
                WorkspaceId = workspaceId,
                WorkspaceIncarnationId = workspaceIncarnationId,
                DeletedSessionCount = sessionIds.Count,
                DeletedSessionIds = sessionIds,
            });

    private static AgentDurableLifecycleEventEnvelope Envelope(
        string id,
        AgentLifecycleEventKind kind,
        AgentDurableLifecycleEventPayload payload)
    {
        var payloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson))).ToLowerInvariant();
        return new AgentDurableLifecycleEventEnvelope
        {
            EventId = id,
            Sequence = Math.Abs(id.GetHashCode(StringComparison.Ordinal)) + 1L,
            Kind = kind,
            SourceKey = "source:" + id,
            OrderingKey = "workspace:workspace.semantic:root",
            PayloadHash = hash,
            PayloadJson = payloadJson,
            OccurredAtUtc = RpcTimestamp(DateTimeOffset.UtcNow),
            Payload = payload,
        };
    }

    private static AgentDurableLifecycleEventEnvelope ErasedDuplicate(
        AgentDurableLifecycleEventEnvelope original)
    {
        var erased = Envelope(
            original.EventId,
            original.Kind,
            Payload() with { ContentErased = true });
        return erased with
        {
            SourceKey = original.SourceKey,
            Sequence = original.Sequence,
            OrderingKey = original.OrderingKey,
            OriginalPayloadHash = original.PayloadHash,
        };
    }

    private static string? ReadDeletionMaintenanceState(string databasePath, string eventId)
    {
        using var connection = new SqliteConnection(MemoryDatabase.CreateConnectionString(databasePath));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT State FROM SemanticDeletionMaintenance WHERE EventId = $eventId;";
        command.Parameters.AddWithValue("$eventId", eventId);
        return command.ExecuteScalar() as string;
    }

    private static bool DatabaseFilesContain(string databasePath, string value)
        => new[] { databasePath, databasePath + "-wal" }
            .Where(File.Exists)
            .Any(path => FileContains(path, value));

    private static bool FileContains(string path, string value)
        => File.Exists(path)
           && File.ReadAllBytes(path).AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)) >= 0;

    private static AgentTurnRecord TextTurn(Guid sessionId, Guid turnId, string text)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentTurnRecord
        {
            TurnSequence = 1,
            ContentRevision = 1,
            CreatedAtUtc = RpcTimestamp(now),
            IsStreaming = false,
            Items = [new AgentTurnItemRecord
            {
                IsError = false,
                IsToolHeaderProjection = false,
                ItemId = Guid.NewGuid(),
                Kind = AgentTurnItemKind.Text,
                SequenceNumber = 0,
                ToolDetailRevision = 0,
                TurnId = turnId,
                WasTruncated = false,
                TextContent = text,
            }],
            Kind = AgentTurnKind.Message,
            Role = AgentMessageRole.User,
            SessionId = sessionId,
            TurnId = turnId,
            UpdatedAtUtc = RpcTimestamp(now),
        };
    }

    private static AgentSessionContextRecord SessionContext(Guid sessionId, string profileId = "profile.semantic")
        => new()
        {
            SessionId = sessionId,
            ProfileId = profileId,
            ProfileDisplayName = "Semantic profile",
            SessionTitle = "Semantic session",
            SessionState = AgentSessionState.Active,
        };

    private static AgentRunContextRecord RunContext()
        => new()
        {
            RunId = Guid.NewGuid(),
            Revision = 1,
            Status = AgentRunStatus.Running,
            IsInterrupted = false,
            StartedAtUtc = RpcTimestamp(DateTimeOffset.UtcNow),
        };

    private static AgentDurableLifecycleEventPayload Payload()
        => new()
        {
            ContentErased = false,
            DeletedChildSessionCount = 0,
            DeletedChildSessionIds = [],
            DeletedSessionCount = 0,
            DeletedSessionIds = [],
            DeletedTurnCount = 0,
            DeletedTurnIds = [],
            DeletionChunkReceipts = [],
            RecentLiveBufferTurns = [],
            Turns = [],
        };

    private static AgentMemoryRecallPlan RecallPlan()
        => new(
            AgentMemoryRecallIntent.StandingInstruction,
            "cobalt release checklist",
            PreferredCategories: ["standing-instruction"],
            MaxEntryCount: 4,
            MaxChars: 1200);

    private static Sunder.Sdk.Rpc.SunderRpcOffsetDateTime RpcTimestamp(DateTimeOffset value)
        => new(value.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

    private static MemoryCandidate Candidate(Guid turnId, string category, string content)
        => new(category, content, content, turnId, false, 0.85f, 0.9f);

    private static string CanonicalHash(StoredMemoryRecord memory, int maxLength)
    {
        var text = $"Category: {memory.Category}\nContent: {memory.Content}\nEvidence: {memory.EvidenceText}\nTrust: {memory.State}".Trim();
        if (text.Length > maxLength)
        {
            text = text[..maxLength].TrimEnd();
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private sealed class FailOncePhysicalMaintenance : IMemoryPhysicalMaintenance
    {
        public int AttemptCount { get; private set; }

        public void SecurePurge(SqliteConnection connection)
        {
            AttemptCount++;
            if (AttemptCount == 1)
            {
                throw new InvalidOperationException("Injected physical maintenance failure.");
            }

            MemoryDatabase.SecurePurge(connection);
        }
    }
}
