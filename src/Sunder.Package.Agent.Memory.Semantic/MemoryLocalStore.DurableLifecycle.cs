using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic.Services;
using static Sunder.Package.Agent.Memory.Semantic.MemoryRepositorySql;

namespace Sunder.Package.Agent.Memory.Semantic;

public sealed partial class MemoryLocalStore
{
    private const string DurableMemoryColumns =
        "MemoryId, SessionId, Category, Content, EvidenceText, SourceTurnId, Importance, Confidence, IsPinned, State, SupersededByMemoryId, CreatedAtUtc, UpdatedAtUtc, LastAccessedAtUtc, AccessCount, Provenance, MemoryRevision, IsManual";

    internal SemanticLifecycleProcessingResult ProcessDurableLifecycleEvent(
        AgentDurableLifecycleEventEnvelope lifecycleEvent,
        IReadOnlyList<MemoryCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateEnvelopeHash(lifecycleEvent);
        var requiresDeletionMaintenance = RequiresDeletionMaintenance(lifecycleEvent.Kind);
        using var connection = MemoryDatabase.OpenConnection(DatabasePath);
        using var transaction = MemoryDatabase.BeginImmediateTransaction(connection);
        if (ReadInboxIdentity(connection, transaction, lifecycleEvent.EventId) is { } existing)
        {
            if (!string.Equals(existing.EventType, lifecycleEvent.Kind.ToString(), StringComparison.Ordinal)
                || !string.Equals(existing.PayloadHash, lifecycleEvent.PayloadHash, StringComparison.Ordinal)
                   && !(lifecycleEvent.Payload.ContentErased
                        && string.Equals(
                            existing.PayloadHash,
                            lifecycleEvent.OriginalPayloadHash,
                            StringComparison.Ordinal)))
            {
                throw new AgentDurableLifecycleIntegrityException(
                    $"Semantic Memory inbox event '{lifecycleEvent.EventId}' was reused with a different type or payload hash.");
            }

            if (requiresDeletionMaintenance)
            {
                EnsureDeletionMaintenancePending(
                    connection,
                    transaction,
                    lifecycleEvent.EventId,
                    lifecycleEvent.Kind,
                    existing.PayloadHash);
            }
            transaction.Commit();
            if (requiresDeletionMaintenance)
            {
                CompleteDeletionMaintenance(
                    lifecycleEvent.EventId,
                    lifecycleEvent.Kind,
                    existing.PayloadHash);
            }
            return SemanticLifecycleProcessingResult.DuplicateReceipt;
        }

        if (!lifecycleEvent.Payload.ContentErased)
        {
            ValidateDeletionManifest(connection, transaction, lifecycleEvent);
        }

        var changedMemories = new Dictionary<Guid, string>();
        var promotedCount = 0;
        if (!lifecycleEvent.Payload.ContentErased)
        {
            switch (lifecycleEvent.Kind)
            {
                case AgentLifecycleEventKind.UserTurnAdded:
                    promotedCount = ApplyPromotion(
                        connection,
                        transaction,
                        lifecycleEvent,
                        candidates,
                        changedMemories,
                        cancellationToken);
                    break;

                case AgentLifecycleEventKind.TranscriptRolledBack:
                    ApplyRollback(
                        connection,
                        transaction,
                        lifecycleEvent,
                        changedMemories,
                        cancellationToken);
                    break;

                case AgentLifecycleEventKind.SessionDeleted:
                    ApplySessionDeletion(connection, transaction, lifecycleEvent, cancellationToken);
                    break;

                case AgentLifecycleEventKind.WorkspaceDeleted:
                    ApplyWorkspaceDeletion(connection, transaction, lifecycleEvent, cancellationToken);
                    break;
            }
        }

        using (var inbox = connection.CreateCommand())
        {
            inbox.Transaction = transaction;
            inbox.CommandText = """
                INSERT INTO SemanticLifecycleInbox (
                    EventId, EventType, PayloadHash, SourceKey, EventSequence, ReceivedAtUtc)
                VALUES ($eventId, $eventType, $payloadHash, $sourceKey, $eventSequence, $receivedAtUtc);
                """;
            inbox.Parameters.AddWithValue("$eventId", lifecycleEvent.EventId);
            inbox.Parameters.AddWithValue("$eventType", lifecycleEvent.Kind.ToString());
            inbox.Parameters.AddWithValue("$payloadHash", lifecycleEvent.PayloadHash);
            inbox.Parameters.AddWithValue("$sourceKey", lifecycleEvent.SourceKey);
            inbox.Parameters.AddWithValue("$eventSequence", lifecycleEvent.Sequence);
            inbox.Parameters.AddWithValue("$receivedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
            if (inbox.ExecuteNonQuery() != 1)
            {
                throw new AgentDurableLifecycleIntegrityException(
                    $"Semantic Memory inbox event '{lifecycleEvent.EventId}' could not be recorded.");
            }
        }
        if (requiresDeletionMaintenance)
        {
            EnsureDeletionMaintenancePending(
                connection,
                transaction,
                lifecycleEvent.EventId,
                lifecycleEvent.Kind,
                lifecycleEvent.PayloadHash);
        }
        transaction.Commit();
        if (requiresDeletionMaintenance)
        {
            CompleteDeletionMaintenance(
                lifecycleEvent.EventId,
                lifecycleEvent.Kind,
                lifecycleEvent.PayloadHash);
        }
        return new SemanticLifecycleProcessingResult(
            IsDuplicate: false,
            CandidateCount: candidates.Count,
            PromotedCount: promotedCount,
            changedMemories.Select(item => new SemanticMemoryIndexRequest(item.Key, item.Value)).ToArray());
    }

    private static int ApplyPromotion(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentDurableLifecycleEventEnvelope lifecycleEvent,
        IReadOnlyList<MemoryCandidate> candidates,
        IDictionary<Guid, string> changedMemories,
        CancellationToken cancellationToken)
    {
        var payload = lifecycleEvent.Payload;
        var sessionId = payload.Session?.SessionId ?? payload.SessionId;
        var profileId = payload.Session?.ProfileId;
        var sourceTurnId = payload.TriggerTurn?.TurnId;
        if (sessionId is null
            || sourceTurnId is null
            || string.IsNullOrWhiteSpace(profileId)
            || IsSessionDeleted(connection, transaction, sessionId.Value)
            || !string.IsNullOrWhiteSpace(payload.WorkspaceId)
               && IsWorkspaceDeleted(
                   connection,
                   transaction,
                   payload.WorkspaceId,
                   payload.WorkspaceIncarnationId)
            || IsTurnRetracted(connection, transaction, sessionId.Value, sourceTurnId.Value))
        {
            return 0;
        }

        var promoted = 0;
        foreach (var candidate in candidates.Take(SemanticMemoryPromotionService.MaxPromotionCandidatesPerEvent))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.SourceTurnId != sourceTurnId)
            {
                continue;
            }

            var contributionId = BuildContributionId(
                lifecycleEvent.EventId,
                sessionId.Value,
                sourceTurnId.Value,
                candidate.Category,
                SemanticMemoryTextHelpers.Normalize(candidate.Content));
            if (ContributionExists(connection, transaction, contributionId))
            {
                continue;
            }

            var normalizedContent = SemanticMemoryTextHelpers.Normalize(candidate.Content);
            var mergeTarget = FindMemoryByIdentity(
                                  connection,
                                  transaction,
                                  sessionId.Value,
                                  candidate.Category,
                                  normalizedContent)
                              ?? SemanticMemoryPromotionService.FindMergeCandidate(
                                  candidate,
                                  ListMergeCandidates(connection, transaction, sessionId.Value));
            if (mergeTarget is null && CountRecallable(connection, transaction, sessionId.Value) >= MaxRecallableMemoriesPerSession)
            {
                continue;
            }

            var now = lifecycleEvent.OccurredAtUtc == default
                ? DateTimeOffset.UtcNow
                : lifecycleEvent.OccurredAtUtc;
            var mergedCandidate = mergeTarget is null
                ? candidate
                : SemanticMemoryPromotionService.MergeCandidate(candidate, mergeTarget);
            var memory = mergeTarget is null
                ? new StoredMemoryRecord(
                    Guid.NewGuid(),
                    sessionId.Value,
                    mergedCandidate.Category,
                    mergedCandidate.Content,
                    mergedCandidate.EvidenceText,
                    mergedCandidate.SourceTurnId,
                    mergedCandidate.Importance,
                    mergedCandidate.Confidence,
                    mergedCandidate.IsPinned,
                    ActiveState,
                    SupersededByMemoryId: null,
                    now,
                    now,
                    LastAccessedAtUtc: null,
                    AccessCount: 0,
                    AgentMemoryProvenance.User)
                {
                    MemoryRevision = 1,
                    IsManual = false,
                }
                : mergeTarget.IsManual
                    ? mergeTarget
                    : mergeTarget with
                    {
                        Category = mergedCandidate.Category,
                        Content = mergedCandidate.Content,
                        EvidenceText = mergedCandidate.EvidenceText,
                        SourceTurnId = mergedCandidate.SourceTurnId,
                        Importance = mergedCandidate.Importance,
                        Confidence = mergedCandidate.Confidence,
                        IsPinned = mergedCandidate.IsPinned,
                        UpdatedAtUtc = now,
                        MemoryRevision = mergeTarget.MemoryRevision + 1,
                        IsManual = false,
                    };

            var memoryChanged = mergeTarget is null || !mergeTarget.IsManual;
            if (memoryChanged)
            {
                Write(
                    connection,
                    transaction,
                    memory,
                    SemanticMemoryTextHelpers.Normalize(memory.Content),
                    insert: mergeTarget is null);
                WriteSearch(connection, transaction, memory);
            }
            InsertContribution(
                connection,
                transaction,
                contributionId,
                lifecycleEvent.EventId,
                memory.MemoryId,
                sessionId.Value,
                profileId,
                sourceTurnId.Value,
                candidate,
                now);
            RebuildContributionEvidence(connection, transaction, memory.MemoryId, sessionId.Value);
            if (memoryChanged)
            {
                DeleteMemoryEmbeddings(connection, transaction, memory.MemoryId);
                changedMemories[memory.MemoryId] = profileId;
            }
            promoted++;
        }
        return promoted;
    }

    private static void ApplyRollback(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentDurableLifecycleEventEnvelope lifecycleEvent,
        IDictionary<Guid, string> changedMemories,
        CancellationToken cancellationToken)
    {
        var payload = lifecycleEvent.Payload;
        if (payload.SessionId is not { } sessionId)
        {
            throw new AgentDurableLifecycleIntegrityException(
                $"Rollback event '{lifecycleEvent.EventId}' has no session id.");
        }

        var affectedMemoryIds = new HashSet<Guid>();
        foreach (var turnId in payload.DeletedTurnIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            InsertTurnRetraction(connection, transaction, lifecycleEvent, sessionId, turnId);
            foreach (var memoryId in ListContributionMemoryIds(connection, transaction, sessionId, turnId))
            {
                affectedMemoryIds.Add(memoryId);
            }
        }

        if (payload.DeletedTurnIds.Count > 0)
        {
            using var deleteContributions = connection.CreateCommand();
            deleteContributions.Transaction = transaction;
            var parameterNames = new List<string>();
            deleteContributions.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            for (var index = 0; index < payload.DeletedTurnIds.Count; index++)
            {
                var name = $"$turnId{index}";
                parameterNames.Add(name);
                deleteContributions.Parameters.AddWithValue(name, payload.DeletedTurnIds[index].ToString());
            }
            deleteContributions.CommandText = $"DELETE FROM SessionMemoryContributions WHERE SessionId = $sessionId AND SourceTurnId IN ({string.Join(", ", parameterNames)});";
            deleteContributions.ExecuteNonQuery();
        }

        foreach (var memoryId in affectedMemoryIds)
        {
            RecomputeMemory(connection, transaction, memoryId, changedMemories);
        }

        foreach (var deletedSessionId in payload.DeletedChildSessionIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            InsertSessionDeletionTombstone(
                connection,
                transaction,
                deletedSessionId,
                payload.WorkspaceId,
                lifecycleEvent);
            DeleteSemanticSession(connection, transaction, deletedSessionId);
        }
    }

    private static void ApplySessionDeletion(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentDurableLifecycleEventEnvelope lifecycleEvent,
        CancellationToken cancellationToken)
    {
        var payload = lifecycleEvent.Payload;
        var sessionIds = payload.DeletedSessionIds.Count > 0
            ? payload.DeletedSessionIds
            : payload.SessionId is { } sessionId ? [sessionId] : [];
        foreach (var deletedSessionId in sessionIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            InsertSessionDeletionTombstone(
                connection,
                transaction,
                deletedSessionId,
                payload.WorkspaceId,
                lifecycleEvent);
            DeleteSemanticSession(connection, transaction, deletedSessionId);
        }
    }

    private static void ApplyWorkspaceDeletion(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentDurableLifecycleEventEnvelope lifecycleEvent,
        CancellationToken cancellationToken)
    {
        var payload = lifecycleEvent.Payload;
        if (string.IsNullOrWhiteSpace(payload.WorkspaceId))
        {
            throw new AgentDurableLifecycleIntegrityException(
                $"Workspace deletion event '{lifecycleEvent.EventId}' has no workspace id.");
        }

        using (var tombstone = connection.CreateCommand())
        {
            tombstone.Transaction = transaction;
            tombstone.CommandText = """
                INSERT OR IGNORE INTO WorkspaceMemoryDeletionTombstones (
                    WorkspaceId, EventId, PayloadHash, DeletedAtUtc)
                VALUES ($workspaceId, $eventId, $payloadHash, $deletedAtUtc);
                """;
            tombstone.Parameters.AddWithValue(
                "$workspaceId",
                BuildWorkspaceTombstoneKey(payload.WorkspaceId, payload.WorkspaceIncarnationId));
            tombstone.Parameters.AddWithValue("$eventId", lifecycleEvent.EventId);
            tombstone.Parameters.AddWithValue("$payloadHash", lifecycleEvent.PayloadHash);
            tombstone.Parameters.AddWithValue("$deletedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
            var affected = tombstone.ExecuteNonQuery();
            if (affected is not (0 or 1))
            {
                throw new AgentDurableLifecycleIntegrityException(
                    $"Workspace deletion tombstone for '{payload.WorkspaceId}' could not be recorded.");
            }
        }

        foreach (var deletedSessionId in payload.DeletedSessionIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            InsertSessionDeletionTombstone(
                connection,
                transaction,
                deletedSessionId,
                payload.WorkspaceId,
                lifecycleEvent);
            DeleteSemanticSession(connection, transaction, deletedSessionId);
        }
    }

    private static void RecomputeMemory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid memoryId,
        IDictionary<Guid, string> changedMemories)
    {
        var memory = GetMemory(connection, transaction, memoryId);
        if (memory is null)
        {
            return;
        }

        var contributions = ListContributions(connection, transaction, memoryId);
        DeleteContributionEvidence(connection, transaction, memoryId);
        if (contributions.Count == 0)
        {
            if (!memory.IsManual && !memory.IsPinned)
            {
                DeleteMemory(connection, transaction, memoryId);
                return;
            }

            if (memory.IsManual)
            {
                return;
            }

            var preserved = memory with
            {
                EvidenceText = null,
                SourceTurnId = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                MemoryRevision = memory.MemoryRevision + 1,
            };
            Write(connection, transaction, preserved, Normalize(preserved.Content), insert: false);
            WriteSearch(connection, transaction, preserved);
            DeleteMemoryEmbeddings(connection, transaction, memoryId);
            return;
        }

        var profileId = contributions[0].ProfileId;
        if (memory.IsManual)
        {
            RebuildContributionEvidence(connection, transaction, memoryId, memory.SessionId);
            return;
        }

        if (memory.IsPinned)
        {
            RebuildContributionEvidence(connection, transaction, memoryId, memory.SessionId);
            var preserved = memory with
            {
                EvidenceText = ChooseRicher(contributions.Select(item => item.EvidenceText)),
                SourceTurnId = contributions.OrderByDescending(item => item.CreatedAtUtc).First().SourceTurnId,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                MemoryRevision = memory.MemoryRevision + 1,
            };
            Write(connection, transaction, preserved, Normalize(preserved.Content), insert: false);
            WriteSearch(connection, transaction, preserved);
            DeleteMemoryEmbeddings(connection, transaction, memoryId);
            changedMemories[memoryId] = profileId;
            return;
        }

        var ordered = contributions.OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.ContributionId, StringComparer.Ordinal).ToArray();
        var content = ChooseRicher(ordered.Select(item => item.Content)) ?? ordered[^1].Content;
        var evidence = ChooseRicher(ordered.Select(item => item.EvidenceText));
        var source = ordered.OrderByDescending(item => item.CreatedAtUtc).First();
        var recomputed = memory with
        {
            Category = source.Category,
            Content = content,
            EvidenceText = evidence,
            SourceTurnId = source.SourceTurnId,
            Importance = ordered.Max(item => item.Importance),
            Confidence = ordered.Max(item => item.Confidence),
            IsPinned = ordered.Any(item => item.IsPinned),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            MemoryRevision = memory.MemoryRevision + 1,
        };
        Write(connection, transaction, recomputed, Normalize(recomputed.Content), insert: false);
        WriteSearch(connection, transaction, recomputed);
        RebuildContributionEvidence(connection, transaction, memoryId, memory.SessionId);
        DeleteMemoryEmbeddings(connection, transaction, memoryId);
        changedMemories[memoryId] = profileId;
    }

    private static void InsertContribution(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string contributionId,
        string eventId,
        Guid memoryId,
        Guid sessionId,
        string profileId,
        Guid sourceTurnId,
        MemoryCandidate candidate,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO SessionMemoryContributions (
                ContributionId, EventId, MemoryId, SessionId, ProfileId, SourceTurnId,
                Category, Content, NormalizedContent, EvidenceText, Importance,
                Confidence, IsPinned, CreatedAtUtc)
            VALUES (
                $contributionId, $eventId, $memoryId, $sessionId, $profileId, $sourceTurnId,
                $category, $content, $normalizedContent, $evidenceText, $importance,
                $confidence, $isPinned, $createdAtUtc);
            """;
        command.Parameters.AddWithValue("$contributionId", contributionId);
        command.Parameters.AddWithValue("$eventId", eventId);
        command.Parameters.AddWithValue("$memoryId", memoryId.ToString());
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$sourceTurnId", sourceTurnId.ToString());
        command.Parameters.AddWithValue("$category", candidate.Category);
        command.Parameters.AddWithValue("$content", candidate.Content);
        command.Parameters.AddWithValue("$normalizedContent", SemanticMemoryTextHelpers.Normalize(candidate.Content));
        command.Parameters.AddWithValue("$evidenceText", (object?)candidate.EvidenceText ?? DBNull.Value);
        command.Parameters.AddWithValue("$importance", candidate.Importance);
        command.Parameters.AddWithValue("$confidence", candidate.Confidence);
        command.Parameters.AddWithValue("$isPinned", candidate.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new AgentDurableLifecycleIntegrityException(
                $"Semantic memory contribution '{contributionId}' could not be recorded.");
        }
    }

    private static StoredMemoryRecord? GetMemory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid memoryId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {DurableMemoryColumns} FROM SessionMemories WHERE MemoryId = $memoryId;";
        command.Parameters.AddWithValue("$memoryId", memoryId.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static IReadOnlyList<StoredContribution> ListContributions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid memoryId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ContributionId, ProfileId, SourceTurnId, Category, Content, EvidenceText,
                   Importance, Confidence, IsPinned, CreatedAtUtc
            FROM SessionMemoryContributions
            WHERE MemoryId = $memoryId
            ORDER BY CreatedAtUtc, ContributionId;
            """;
        command.Parameters.AddWithValue("$memoryId", memoryId.ToString());
        using var reader = command.ExecuteReader();
        var contributions = new List<StoredContribution>();
        while (reader.Read())
        {
            contributions.Add(new StoredContribution(
                reader.GetString(0),
                reader.GetString(1),
                Guid.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                Convert.ToSingle(reader.GetDouble(6)),
                Convert.ToSingle(reader.GetDouble(7)),
                reader.GetInt64(8) != 0,
                DateTimeOffset.Parse(reader.GetString(9))));
        }
        return contributions;
    }

    private static void RebuildContributionEvidence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid memoryId,
        Guid sessionId)
    {
        DeleteContributionEvidence(connection, transaction, memoryId);
        foreach (var contribution in ListContributions(connection, transaction, memoryId)
                     .OrderByDescending(item => item.CreatedAtUtc)
                     .ThenByDescending(item => item.ContributionId, StringComparer.Ordinal)
                     .Take(EvidenceRepository.MaxEvidenceRecordsPerMemory))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO SessionMemoryEvidence (
                    EvidenceId, MemoryId, SessionId, SourceTurnId, EvidenceText, CreatedAtUtc, ContributionId)
                VALUES (
                    $evidenceId, $memoryId, $sessionId, $sourceTurnId, $evidenceText, $createdAtUtc, $contributionId);
                """;
            command.Parameters.AddWithValue("$evidenceId", BuildContributionEvidenceId(contribution.ContributionId).ToString());
            command.Parameters.AddWithValue("$memoryId", memoryId.ToString());
            command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            command.Parameters.AddWithValue("$sourceTurnId", contribution.SourceTurnId.ToString());
            command.Parameters.AddWithValue(
                "$evidenceText",
                string.IsNullOrWhiteSpace(contribution.EvidenceText)
                    ? DBNull.Value
                    : contribution.EvidenceText[..Math.Min(
                        contribution.EvidenceText.Length,
                        EvidenceRepository.MaxEvidenceChars)]);
            command.Parameters.AddWithValue("$createdAtUtc", contribution.CreatedAtUtc.ToString("O"));
            command.Parameters.AddWithValue("$contributionId", contribution.ContributionId);
            command.ExecuteNonQuery();
        }
    }

    private static bool IsTurnRetracted(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        Guid turnId)
        => Exists(
            connection,
            transaction,
            "SELECT 1 FROM SessionMemoryTurnRetractions WHERE SessionId = $first AND TurnId = $second LIMIT 1;",
            sessionId.ToString(),
            turnId.ToString());

    private static bool IsSessionDeleted(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId)
        => Exists(
            connection,
            transaction,
            "SELECT 1 FROM SessionMemoryDeletionTombstones WHERE SessionId = $first LIMIT 1;",
            sessionId.ToString(),
            second: null);

    private static bool IsWorkspaceDeleted(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string workspaceId,
        string? workspaceIncarnationId)
        => Exists(
            connection,
            transaction,
            "SELECT 1 FROM WorkspaceMemoryDeletionTombstones WHERE WorkspaceId = $first LIMIT 1;",
            BuildWorkspaceTombstoneKey(workspaceId, workspaceIncarnationId),
            second: null);

    private static string BuildWorkspaceTombstoneKey(
        string workspaceId,
        string? workspaceIncarnationId)
        => string.IsNullOrWhiteSpace(workspaceIncarnationId)
            ? workspaceId
            : workspaceId + "\n" + workspaceIncarnationId;

    private static bool Exists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string first,
        string? second)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$first", first);
        if (second is not null)
        {
            command.Parameters.AddWithValue("$second", second);
        }
        return command.ExecuteScalar() is not null;
    }

    private static IReadOnlyList<Guid> ListContributionMemoryIds(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        Guid turnId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT DISTINCT MemoryId FROM SessionMemoryContributions WHERE SessionId = $sessionId AND SourceTurnId = $turnId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$turnId", turnId.ToString());
        using var reader = command.ExecuteReader();
        var ids = new List<Guid>();
        while (reader.Read())
        {
            ids.Add(Guid.Parse(reader.GetString(0)));
        }
        return ids;
    }

    private static bool ContributionExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string contributionId)
        => Exists(
            connection,
            transaction,
            "SELECT 1 FROM SessionMemoryContributions WHERE ContributionId = $first LIMIT 1;",
            contributionId,
            second: null);

    private static int CountRecallable(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM SessionMemories WHERE SessionId = $sessionId AND State IN ($active, $contested);";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$active", ActiveState);
        command.Parameters.AddWithValue("$contested", ContestedState);
        return Convert.ToInt32(command.ExecuteScalar());
    }

}
