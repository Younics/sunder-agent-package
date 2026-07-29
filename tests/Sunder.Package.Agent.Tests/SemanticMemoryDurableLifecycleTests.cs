using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

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
        Assert.Throws<AgentDurableLifecycleIntegrityException>(() =>
            store.ProcessDurableLifecycleEvent(mismatched, [Candidate(turnId, "project-fact", "The project uses amber.")]));
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
            new AgentDurableLifecycleEventPayload { ContentErased = true }) with
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
        Assert.True(FileContains(store.DatabasePath, fullTextSecret));

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
        var resolver = new SemanticModelRuntimeResolver(new RegressionTestExtensionCatalog(), settings);
        var backend = new SemanticMemoryRetrievalBackend(store, resolver, settings);
        var recall = new SemanticMemoryRecallService(store, backend, new SemanticMemoryMetricsService());
        var barrierEvent = RollbackEvent("recall-barrier", sessionId, []);
        var request = RecallRequest(sessionId) with
        {
            MemoryConsistencyBarrier = new AgentMemoryConsistencyBarrier(
                barrierEvent.EventId,
                barrierEvent.PayloadHash),
        };

        var beforeReceipt = await recall.RecallAsync(request);
        store.ProcessDurableLifecycleEvent(barrierEvent, []);
        var afterReceipt = await recall.RecallAsync(request);

        Assert.Null(beforeReceipt);
        Assert.NotNull(afterReceipt);
        Assert.NotEmpty(afterReceipt.Entries);
    }

    private static AgentMemoryRecallRequest RecallRequest(Guid sessionId)
    {
        var session = new AgentSessionContextRecord(
            sessionId,
            "profile.semantic",
            "Semantic profile",
            "Semantic session",
            AgentSessionState.Active,
            WorkingSummary: null);
        var run = new AgentRunContextRecord(Guid.NewGuid(), 1, AgentRunStatus.Running, false, DateTimeOffset.UtcNow);
        var turn = new AgentTurnContextRecord(session, run, "What release checklist should I use?", WorkingSummary: null);
        return new AgentMemoryRecallRequest(
            session,
            run,
            turn,
            [],
            [],
            new AgentMemoryRecallPlan(
                AgentMemoryRecallIntent.StandingInstruction,
                "cobalt release checklist",
                PreferredCategories: ["standing-instruction"],
                MaxEntryCount: 4,
                MaxChars: 1200));
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
        var session = new AgentSessionContextRecord(
            sessionId,
            profileId,
            "Semantic profile",
            "Semantic session",
            AgentSessionState.Active,
            WorkingSummary: null);
        var run = new AgentRunContextRecord(Guid.NewGuid(), 1, AgentRunStatus.Running, false, DateTimeOffset.UtcNow);
        var turn = TextTurn(sessionId, turnId, text);
        return Envelope(
            id,
            AgentLifecycleEventKind.UserTurnAdded,
            new AgentDurableLifecycleEventPayload
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
            new AgentDurableLifecycleEventPayload
            {
                SessionId = sessionId,
                RootSessionId = sessionId,
                WorkspaceId = "workspace.semantic",
                DeletedTurnIds = deletedTurnIds,
            });

    private static AgentDurableLifecycleEventEnvelope SessionDeletedEvent(string id, Guid sessionId)
        => Envelope(
            id,
            AgentLifecycleEventKind.SessionDeleted,
            new AgentDurableLifecycleEventPayload
            {
                SessionId = sessionId,
                RootSessionId = sessionId,
                WorkspaceId = "workspace.semantic",
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
            new AgentDurableLifecycleEventPayload
            {
                WorkspaceId = workspaceId,
                WorkspaceIncarnationId = workspaceIncarnationId,
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
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Payload = payload,
        };
    }

    private static AgentDurableLifecycleEventEnvelope ErasedDuplicate(
        AgentDurableLifecycleEventEnvelope original)
    {
        var erased = Envelope(
            original.EventId,
            original.Kind,
            new AgentDurableLifecycleEventPayload { ContentErased = true });
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
        return new AgentTurnRecord(
            turnId,
            sessionId,
            AgentMessageRole.User,
            AgentTurnKind.Message,
            [
                new AgentTurnItemRecord(
                    Guid.NewGuid(),
                    turnId,
                    0,
                    AgentTurnItemKind.Text,
                    text,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    false,
                    false,
                    null,
                    null),
            ],
            now,
            now);
    }

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
