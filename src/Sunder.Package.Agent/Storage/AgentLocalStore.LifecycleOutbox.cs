using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    internal const int MaxLifecyclePayloadBytes = 256 * 1024;
    internal const int MaxLifecycleDeliveryAttempts = 5;
    internal const int MaxLifecycleDeletionIds = 250_000;
    internal const int MaxLifecycleReconciliationBatchSize = 256;
    private const int MaxLifecycleDeletionIdsPerChunk = 256;
    private const int MaxLifecycleTurns = 64;
    private const int MaxLifecycleLiveTurns = 8;
    private const int MaxLifecycleExceptionTypeChars = 512;
    private const string ErasedLifecyclePayloadJson = "{\"contentErased\":true}";
    private static readonly TimeSpan LifecyclePoisonRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly string ErasedLifecyclePayloadHash = ComputeLowerHash(ErasedLifecyclePayloadJson);

    private static readonly JsonSerializerOptions LifecycleJsonOptions = new(JsonSerializerDefaults.Web);

    internal event Action? LifecycleOutboxChanged;

    internal IReadOnlyList<AgentLifecycleOutboxRecord> ListLifecycleOutboxEvents()
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {LifecycleOutboxColumns} FROM AgentLifecycleOutbox ORDER BY Sequence;";
        using var reader = command.ExecuteReader();
        var events = new List<AgentLifecycleOutboxRecord>();
        while (reader.Read())
        {
            events.Add(ReadLifecycleOutboxRecord(reader));
        }
        return events;
    }

    internal bool ContainsRunLifecycleEvent(AgentLifecycleEventKind kind, Guid runId)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM AgentLifecycleOutbox
            WHERE EventType = $eventType
              AND json_extract(PayloadJson, '$.run.runId') = $runId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$eventType", kind.ToString());
        command.Parameters.AddWithValue("$runId", runId.ToString());
        return command.ExecuteScalar() is not null;
    }

    internal bool CompleteLifecycleDelivery(AgentLifecycleDeliveryClaim claim, DateTimeOffset now)
        => UpdateClaimedLifecycleDelivery(
            claim,
            """
            Status = 'Delivered',
            DeliveredAtUtc = $now,
            LeaseToken = NULL,
            LeaseExpiresAtUtc = NULL,
            LastError = NULL
            """,
            now);

    internal AgentLifecycleDeliveryFailureResult FailLifecycleDelivery(
        AgentLifecycleDeliveryClaim claim,
        Exception exception,
        DateTimeOffset now,
        bool permanent)
    {
        var failureCode = GetLifecycleFailureCode(exception);
        var exceptionType = GetLifecycleExceptionType(exception);
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureRuntimeGenerationCurrent(connection, transaction);
        int attemptCount;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT AttemptCount
                FROM AgentLifecycleDeliveries
                WHERE SubscriptionId = $subscriptionId
                  AND EventSequence = $eventSequence
                  AND Status = 'InFlight'
                  AND LeaseToken = $leaseToken
                  AND DeliveryVersion = $deliveryVersion;
                """;
            BindClaim(select, claim);
            var value = select.ExecuteScalar();
            if (value is null)
            {
                transaction.Rollback();
                return new AgentLifecycleDeliveryFailureResult(
                    false,
                    false,
                    0,
                    now,
                    failureCode,
                    exceptionType);
            }
            attemptCount = Convert.ToInt32(value) + 1;
        }

        var poisoned = permanent || attemptCount >= MaxLifecycleDeliveryAttempts;
        var nextAttemptAtUtc = poisoned
            ? now.Add(LifecyclePoisonRetryDelay)
            : now.Add(CalculateLifecycleRetryDelay(attemptCount));
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE AgentLifecycleDeliveries
                SET Status = $status,
                    AttemptCount = $attemptCount,
                    NextAttemptAtUtc = $nextAttemptAtUtc,
                    LeaseToken = NULL,
                    LeaseExpiresAtUtc = NULL,
                    PoisonedAtUtc = $poisonedAtUtc,
                    LastError = $lastError
                WHERE SubscriptionId = $subscriptionId
                  AND EventSequence = $eventSequence
                  AND Status = 'InFlight'
                  AND LeaseToken = $leaseToken
                  AND DeliveryVersion = $deliveryVersion;
                """;
            update.Parameters.AddWithValue("$status", poisoned ? "Poison" : "Pending");
            update.Parameters.AddWithValue("$attemptCount", attemptCount);
            update.Parameters.AddWithValue("$nextAttemptAtUtc", nextAttemptAtUtc.ToString("O"));
            update.Parameters.AddWithValue("$poisonedAtUtc", poisoned ? now.ToString("O") : DBNull.Value);
            update.Parameters.AddWithValue(
                "$lastError",
                $"{failureCode} ({exceptionType})");
            BindClaim(update, claim);
            if (update.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return new AgentLifecycleDeliveryFailureResult(
                    false,
                    false,
                    attemptCount,
                    nextAttemptAtUtc,
                    failureCode,
                    exceptionType);
            }
        }
        transaction.Commit();
        return new AgentLifecycleDeliveryFailureResult(
            true,
            poisoned,
            attemptCount,
            nextAttemptAtUtc,
            failureCode,
            exceptionType);
    }

    internal bool ReleaseLifecycleDelivery(AgentLifecycleDeliveryClaim claim, DateTimeOffset now)
        => UpdateClaimedLifecycleDelivery(
            claim,
            """
            Status = 'Pending',
            NextAttemptAtUtc = $now,
            LeaseToken = NULL,
            LeaseExpiresAtUtc = NULL
            """,
            now);

    internal AgentLifecycleDeliveryState? GetLifecycleDeliveryState(string subscriptionId, long eventSequence)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Status, AttemptCount, NextAttemptAtUtc, LeaseToken, LeaseExpiresAtUtc, LastError,
                   DeliveryVersion, MembershipGeneration
            FROM AgentLifecycleDeliveries
            WHERE SubscriptionId = $subscriptionId AND EventSequence = $eventSequence;
            """;
        command.Parameters.AddWithValue("$subscriptionId", subscriptionId);
        command.Parameters.AddWithValue("$eventSequence", eventSequence);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new AgentLifecycleDeliveryState(
                reader.GetString(0),
                reader.GetInt32(1),
                DateTimeOffset.Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt64(6),
                reader.GetInt64(7))
            : null;
    }

    internal bool ResetPoisonedLifecycleDelivery(
        string subscriptionId,
        long eventSequence,
        DateTimeOffset now)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE AgentLifecycleDeliveries
            SET Status = 'Pending',
                AttemptCount = 0,
                NextAttemptAtUtc = $now,
                LeaseToken = NULL,
                LeaseExpiresAtUtc = NULL,
                PoisonedAtUtc = NULL,
                LastError = NULL
            WHERE SubscriptionId = $subscriptionId
              AND EventSequence = $eventSequence
              AND Status = 'Poison';
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$subscriptionId", subscriptionId);
        command.Parameters.AddWithValue("$eventSequence", eventSequence);
        var reset = command.ExecuteNonQuery() == 1;
        if (reset)
        {
            SignalLifecycleOutboxChanged();
        }
        return reset;
    }

    internal static string BuildLifecycleSubscriptionId(string packageId, string observerId, string contractKind)
        => "sub_" + ComputeLowerHash($"agent-lifecycle-subscription-v1\n{packageId}\n{observerId}\n{contractKind}");

    internal static string BuildLifecycleEventId(string sourceKey)
        => "evt_" + ComputeLowerHash($"agent-lifecycle-event-v1\n{sourceKey}");

    private const string LifecycleOutboxColumns =
        "Sequence, EventId, SourceKey, EventType, OrderingKey, WorkspaceId, SessionId, PayloadJson, PayloadHash, CreatedAtUtc, PayloadState, OriginalPayloadHash, PayloadErasedAtUtc, PayloadErasureGeneration";

    private const string LifecycleReplayEligibilitySql = """
        (PayloadState = 'Available'
         OR (PayloadState = 'Erased' AND PayloadErasureGeneration >= $membershipGeneration))
        AND ($contractKind = 'Durable'
             OR EventType IN (
                 'UserTurnAdded', 'AssistantTurnCompleted', 'ToolResultRecorded',
                 'RunInterrupted', 'RunStopped', 'RunFailed'))
        """;

    private const string LifecycleContractEligibilitySql = """
        ($contractKind = 'Durable'
         OR EventType IN (
             'UserTurnAdded', 'AssistantTurnCompleted', 'ToolResultRecorded',
             'RunInterrupted', 'RunStopped', 'RunFailed'))
        """;

    private AgentMemoryConsistencyBarrier EnqueueRunLifecycleEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentLifecycleEventKind kind,
        string sourceKey,
        AgentDurableRunKey key,
        AgentTurnRecord? triggerTurn = null,
        AgentRunCheckpointRecord? checkpoint = null)
    {
        var run = GetRun(connection, transaction, key.RunId)
            ?? throw new InvalidOperationException($"Run '{key.RunId}' was not found while capturing lifecycle event '{kind}'.");
        var session = ListSessions(connection, transaction).FirstOrDefault(item => item.SessionId == key.SessionId)
            ?? throw new InvalidOperationException($"Session '{key.SessionId}' was not found while capturing lifecycle event '{kind}'.");
        var workspaceIncarnationId = ReadWorkspaceIncarnationId(
            connection,
            transaction,
            session.WorkspaceId);
        var workingSummary = ReadLatestWorkingSummary(connection, transaction, key.SessionId);
        var profileId = string.IsNullOrWhiteSpace(run.ProfileId) ? session.ProfileId ?? string.Empty : run.ProfileId;
        var sessionContext = new AgentSessionContextRecord(
            session.SessionId,
            profileId,
            ReadProfileDisplayName(connection, transaction, profileId),
            session.Title,
            session.State,
            workingSummary);
        var status = run.Status == AgentDurableRunStatus.Preparing
            ? AgentRunStatus.Idle
            : Enum.Parse<AgentRunStatus>(run.Status.ToString(), ignoreCase: false);
        var runContext = new AgentRunContextRecord(
            run.Key.RunId,
            run.Key.RunRevision,
            status,
            status == AgentRunStatus.Interrupted,
            run.StartedAtUtc);
        var turns = ReadRecentLifecycleTurns(connection, transaction, key.SessionId);
        triggerTurn ??= ReadLatestRunTurn(connection, transaction, key);
        checkpoint ??= kind is AgentLifecycleEventKind.AssistantTurnCompleted
            or AgentLifecycleEventKind.RunInterrupted
            or AgentLifecycleEventKind.RunStopped
            or AgentLifecycleEventKind.RunFailed
                ? GetLatestCheckpoint(connection, key.SessionId, transaction)
                : null;
        var payload = new AgentDurableLifecycleEventPayload
        {
            Session = sessionContext,
            Run = runContext,
            UserMessage = run.UserMessage,
            WorkingSummary = workingSummary,
            Turns = turns,
            RecentLiveBufferTurns = turns.TakeLast(MaxLifecycleLiveTurns).ToArray(),
            TriggerTurn = triggerTurn,
            Checkpoint = checkpoint,
            SessionId = key.SessionId,
            RootSessionId = session.RootSessionId ?? session.SessionId,
            WorkspaceId = session.WorkspaceId,
            WorkspaceIncarnationId = workspaceIncarnationId,
        };
        return EnqueueLifecyclePayload(
            connection,
            transaction,
            kind,
            sourceKey,
            BuildOrderingKey(session.WorkspaceId, workspaceIncarnationId, session.RootSessionId ?? session.SessionId),
            session.WorkspaceId,
            session.SessionId,
            payload);
    }

    private AgentMemoryConsistencyBarrier EnqueueLifecyclePayload(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentLifecycleEventKind kind,
        string sourceKey,
        string orderingKey,
        string? workspaceId,
        Guid? sessionId,
        AgentDurableLifecycleEventPayload payload)
    {
        var payloadJson = SerializeBoundedLifecyclePayload(payload);
        var payloadHash = ComputeLowerHash(payloadJson);
        var eventId = BuildLifecycleEventId(sourceKey);
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT EventId, EventType, PayloadHash FROM AgentLifecycleOutbox WHERE SourceKey = $sourceKey OR EventId = $eventId LIMIT 1;";
            existing.Parameters.AddWithValue("$sourceKey", sourceKey);
            existing.Parameters.AddWithValue("$eventId", eventId);
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                if (!string.Equals(reader.GetString(0), eventId, StringComparison.Ordinal)
                    || !string.Equals(reader.GetString(1), kind.ToString(), StringComparison.Ordinal)
                    || !string.Equals(reader.GetString(2), payloadHash, StringComparison.Ordinal))
                {
                    throw new AgentDurableLifecycleIntegrityException(
                        $"Lifecycle source key '{sourceKey}' was reused with a different event type or payload hash.");
                }
                return new AgentMemoryConsistencyBarrier(eventId, payloadHash);
            }
        }

        var now = DateTimeOffset.UtcNow;
        long sequence;
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO AgentLifecycleOutbox (
                    EventId, SourceKey, EventType, OrderingKey, WorkspaceId, SessionId,
                    PayloadJson, PayloadHash, CreatedAtUtc)
                VALUES (
                    $eventId, $sourceKey, $eventType, $orderingKey, $workspaceId, $sessionId,
                    $payloadJson, $payloadHash, $createdAtUtc);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$eventId", eventId);
            insert.Parameters.AddWithValue("$sourceKey", sourceKey);
            insert.Parameters.AddWithValue("$eventType", kind.ToString());
            insert.Parameters.AddWithValue("$orderingKey", orderingKey);
            insert.Parameters.AddWithValue("$workspaceId", (object?)workspaceId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$sessionId", sessionId?.ToString() ?? (object)DBNull.Value);
            insert.Parameters.AddWithValue("$payloadJson", payloadJson);
            insert.Parameters.AddWithValue("$payloadHash", payloadHash);
            insert.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
            sequence = Convert.ToInt64(insert.ExecuteScalar());
        }

        using (var deliveries = connection.CreateCommand())
        {
            deliveries.Transaction = transaction;
            deliveries.CommandText = """
                INSERT OR IGNORE INTO AgentLifecycleDeliveries (
                    SubscriptionId, EventSequence, Status, AttemptCount, NextAttemptAtUtc,
                    MembershipGeneration, DeliveryVersion)
                SELECT SubscriptionId, $eventSequence, 'Pending', 0, $now,
                       MembershipGeneration, 1
                FROM AgentLifecycleSubscriptions
                WHERE RetiredAtUtc IS NULL
                  AND (ContractKind = 'Durable'
                       OR $eventType IN (
                           'UserTurnAdded', 'AssistantTurnCompleted', 'ToolResultRecorded',
                           'RunInterrupted', 'RunStopped', 'RunFailed'));
                """;
            deliveries.Parameters.AddWithValue("$eventSequence", sequence);
            deliveries.Parameters.AddWithValue("$now", now.ToString("O"));
            deliveries.Parameters.AddWithValue("$eventType", kind.ToString());
            deliveries.ExecuteNonQuery();
        }

        using (var subscriptions = connection.CreateCommand())
        {
            subscriptions.Transaction = transaction;
            subscriptions.CommandText = """
                UPDATE AgentLifecycleSubscriptions
                SET ReconciledThroughSequence = $eventSequence
                WHERE ReconciledThroughSequence = COALESCE(
                    (
                        SELECT MAX(earlier.Sequence)
                        FROM AgentLifecycleOutbox earlier
                        WHERE earlier.Sequence < $eventSequence
                    ),
                    0);
                """;
            subscriptions.Parameters.AddWithValue("$eventSequence", sequence);
            subscriptions.ExecuteNonQuery();
        }

        SignalLifecycleOutboxChanged();
        return new AgentMemoryConsistencyBarrier(eventId, payloadHash);
    }

    private static string SerializeBoundedLifecyclePayload(AgentDurableLifecycleEventPayload payload)
    {
        foreach (var shape in new[]
                 {
                     (Turns: MaxLifecycleTurns, Items: 8, Text: 4096),
                     (Turns: 32, Items: 4, Text: 2048),
                     (Turns: 16, Items: 2, Text: 1024),
                     (Turns: 8, Items: 1, Text: 768),
                     (Turns: 0, Items: 1, Text: 512),
                 })
        {
            var bounded = BoundLifecyclePayload(payload, shape.Turns, shape.Items, shape.Text);
            var json = JsonSerializer.Serialize(bounded, LifecycleJsonOptions);
            if (Encoding.UTF8.GetByteCount(json) <= MaxLifecyclePayloadBytes)
            {
                return json;
            }
        }

        throw new InvalidOperationException($"The durable lifecycle payload exceeds {MaxLifecyclePayloadBytes} bytes after bounding.");
    }

#pragma warning disable CS0618
    private static AgentDurableLifecycleEventPayload BoundLifecyclePayload(
        AgentDurableLifecycleEventPayload payload,
        int maxTurns,
        int maxItems,
        int maxText)
    {
        var turns = payload.Turns.TakeLast(maxTurns).Select(turn => BoundLifecycleTurn(turn, maxItems, maxText)).ToArray();
        var liveTurnIds = payload.RecentLiveBufferTurns.Select(turn => turn.TurnId).ToHashSet();
        var liveTurns = turns.Where(turn => liveTurnIds.Contains(turn.TurnId)).TakeLast(MaxLifecycleLiveTurns).ToArray();
        return payload with
        {
            Session = payload.Session is null
                ? null
                : payload.Session with
                {
                    ProfileDisplayName = BoundLifecycleText(payload.Session.ProfileDisplayName, 512) ?? string.Empty,
                    SessionTitle = BoundLifecycleText(payload.Session.SessionTitle, 1024) ?? string.Empty,
                    WorkingSummary = BoundLifecycleText(payload.Session.WorkingSummary, maxText),
                },
            UserMessage = BoundLifecycleText(payload.UserMessage, Math.Max(maxText, 8192)),
            WorkingSummary = BoundLifecycleText(payload.WorkingSummary, maxText),
            Turns = turns,
            RecentLiveBufferTurns = liveTurns,
            TriggerTurn = payload.TriggerTurn is null
                ? null
                : BoundLifecycleTurn(payload.TriggerTurn, Math.Max(maxItems, 4), Math.Max(maxText, 8192)),
            Checkpoint = payload.Checkpoint is null
                ? null
                : payload.Checkpoint with { Summary = BoundLifecycleText(payload.Checkpoint.Summary, maxText) },
            WorkspaceId = BoundLifecycleText(payload.WorkspaceId, 512),
            WorkspaceIncarnationId = BoundLifecycleText(payload.WorkspaceIncarnationId, 128),
        };
    }
#pragma warning restore CS0618

    internal static AgentTurnRecord BoundLifecycleTurn(AgentTurnRecord turn, int maxItems, int maxText)
    {
        var itemsWereOmitted = turn.Items.Count > maxItems;
        return turn with
        {
            Items = turn.Items
                .OrderBy(item => item.SequenceNumber)
                .Take(maxItems)
                .Select(item => item with
                {
                    TextContent = BoundLifecycleText(item.TextContent, maxText),
                    CallId = BoundLifecycleText(item.CallId, 512),
                    ToolId = BoundLifecycleText(item.ToolId, 512),
                    ArgumentsJson = BoundLifecycleText(item.ArgumentsJson, maxText),
                    ResultSummary = BoundLifecycleText(item.ResultSummary, maxText),
                    StructuredPayloadJson = BoundLifecycleText(item.StructuredPayloadJson, maxText),
                    SourcesJson = BoundLifecycleText(item.SourcesJson, maxText),
                    ErrorCode = BoundLifecycleText(item.ErrorCode, 256),
                    BackendId = BoundLifecycleText(item.BackendId, 256),
                    PresentationPayloadJson = BoundLifecycleText(item.PresentationPayloadJson, maxText),
                    ToolOwnerPackageId = BoundLifecycleText(item.ToolOwnerPackageId, 256),
                    ToolSchemaId = BoundLifecycleText(item.ToolSchemaId, 512),
                    ToolSchemaVersion = BoundLifecycleText(item.ToolSchemaVersion, 128),
                    WasTruncated = item.WasTruncated
                                   || itemsWereOmitted
                                   || WasLifecycleTextTruncated(item.TextContent, maxText)
                                   || WasLifecycleTextTruncated(item.CallId, 512)
                                   || WasLifecycleTextTruncated(item.ToolId, 512)
                                   || WasLifecycleTextTruncated(item.ArgumentsJson, maxText)
                                   || WasLifecycleTextTruncated(item.ResultSummary, maxText)
                                   || WasLifecycleTextTruncated(item.StructuredPayloadJson, maxText)
                                   || WasLifecycleTextTruncated(item.SourcesJson, maxText)
                                   || WasLifecycleTextTruncated(item.ErrorCode, 256)
                                   || WasLifecycleTextTruncated(item.BackendId, 256)
                                   || WasLifecycleTextTruncated(item.PresentationPayloadJson, maxText)
                                   || WasLifecycleTextTruncated(item.ToolOwnerPackageId, 256)
                                   || WasLifecycleTextTruncated(item.ToolSchemaId, 512)
                                   || WasLifecycleTextTruncated(item.ToolSchemaVersion, 128),
                })
                .ToArray(),
        };
    }

    private static IReadOnlyList<AgentTurnRecord> ReadRecentLifecycleTurns(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId)
    {
        var headers = ListRecentTurnHeadersForSession(connection, sessionId, MaxLifecycleTurns, transaction)
            .OrderBy(turn => turn.CreatedAtUtc)
            .ThenBy(turn => turn.TurnId.ToString("D"), StringComparer.Ordinal)
            .ToArray();
        return AttachItems(headers, ListTurnItemsForTurns(connection, headers.Select(turn => turn.TurnId).ToArray(), transaction));
    }

    private static AgentTurnRecord? ReadLatestRunTurn(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentDurableRunKey key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT TurnId
            FROM AgentTurns
            WHERE SessionId = $sessionId AND RunId = $runId AND RunRevision = $runRevision
            ORDER BY CreatedAtUtc COLLATE BINARY DESC, TurnId COLLATE BINARY DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sessionId", key.SessionId.ToString());
        command.Parameters.AddWithValue("$runId", key.RunId.ToString());
        command.Parameters.AddWithValue("$runRevision", key.RunRevision);
        return command.ExecuteScalar() is string turnId
            ? GetTurn(connection, Guid.Parse(turnId), transaction)
            : null;
    }

    private static string ReadProfileDisplayName(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string profileId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT DisplayName FROM AgentProfiles WHERE ProfileId = $profileId;";
        command.Parameters.AddWithValue("$profileId", profileId);
        return command.ExecuteScalar() as string ?? profileId;
    }

    private static string BuildOrderingKey(
        string? workspaceId,
        string? workspaceIncarnationId,
        Guid rootSessionId)
        => $"workspace:{workspaceIncarnationId ?? workspaceId ?? "unassigned"}:root:{rootSessionId:N}";

    private static string BuildLegacyOrderingKey(string? workspaceId, Guid rootSessionId)
        => $"workspace:{workspaceId ?? "unassigned"}:root:{rootSessionId:N}";

    private static string? ReadWorkspaceIncarnationId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT CreatedAtUtc FROM AgentWorkspaces WHERE WorkspaceId = $workspaceId;";
        command.Parameters.AddWithValue("$workspaceId", workspaceId);
        return command.ExecuteScalar() is string createdAtUtc
            ? ComputeLowerHash($"agent-workspace-incarnation-v1\n{workspaceId}\n{createdAtUtc}")
            : null;
    }

    private static bool TryMapTerminalLifecycleKind(
        AgentRunStatus status,
        out AgentLifecycleEventKind lifecycleKind)
    {
        lifecycleKind = status switch
        {
            AgentRunStatus.Completed => AgentLifecycleEventKind.AssistantTurnCompleted,
            AgentRunStatus.Interrupted => AgentLifecycleEventKind.RunInterrupted,
            AgentRunStatus.Stopped => AgentLifecycleEventKind.RunStopped,
            AgentRunStatus.Failed => AgentLifecycleEventKind.RunFailed,
            _ => default,
        };
        return status is AgentRunStatus.Completed
            or AgentRunStatus.Interrupted
            or AgentRunStatus.Stopped
            or AgentRunStatus.Failed;
    }

    private static string ComputeLowerHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string? BoundLifecycleText(string? value, int maxChars)
        => string.IsNullOrEmpty(value) || value.Length <= maxChars ? value : value[..maxChars];

    private static bool WasLifecycleTextTruncated(string? value, int maxChars)
        => value?.Length > maxChars;

    private static string GetLifecycleFailureCode(Exception exception)
        => exception is AgentDurableLifecycleIntegrityException
            ? "lifecycle_integrity_failure"
            : "observer_callback_failure";

    private static string GetLifecycleExceptionType(Exception exception)
    {
        var exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
        return exceptionType[..Math.Min(exceptionType.Length, MaxLifecycleExceptionTypeChars)];
    }

    private static TimeSpan CalculateLifecycleRetryDelay(int attemptCount)
        => TimeSpan.FromMilliseconds(Math.Min(30_000, 250 * Math.Pow(2, Math.Max(0, attemptCount - 1))));

    private bool UpdateClaimedLifecycleDelivery(
        AgentLifecycleDeliveryClaim claim,
        string assignments,
        DateTimeOffset now)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureRuntimeGenerationCurrent(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE AgentLifecycleDeliveries
            SET {assignments}
                WHERE SubscriptionId = $subscriptionId
                  AND EventSequence = $eventSequence
                  AND Status = 'InFlight'
                  AND LeaseToken = $leaseToken
                  AND DeliveryVersion = $deliveryVersion;
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        BindClaim(command, claim);
        var updated = command.ExecuteNonQuery() == 1;
        transaction.Commit();
        return updated;
    }

    private void SignalLifecycleOutboxChanged()
    {
        var handlers = LifecycleOutboxChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (Action handler in handlers.GetInvocationList())
        {
            try
            {
                handler();
            }
            catch
            {
                // A wake hint must not affect the source transaction.
            }
        }
    }

    private static void BindSubscription(SqliteCommand command, AgentLifecycleSubscription subscription)
    {
        command.Parameters.AddWithValue("$subscriptionId", subscription.SubscriptionId);
        command.Parameters.AddWithValue("$packageId", subscription.PackageId);
        command.Parameters.AddWithValue("$observerId", subscription.ObserverId);
        command.Parameters.AddWithValue("$contractKind", subscription.ContractKind);
        command.Parameters.AddWithValue("$displayName", BoundLifecycleText(subscription.DisplayName, 512) ?? string.Empty);
    }

    private static void BindClaim(SqliteCommand command, AgentLifecycleDeliveryClaim claim)
    {
        command.Parameters.AddWithValue("$subscriptionId", claim.SubscriptionId);
        command.Parameters.AddWithValue("$eventSequence", claim.Event.Sequence);
        command.Parameters.AddWithValue("$leaseToken", claim.LeaseToken);
        command.Parameters.AddWithValue("$deliveryVersion", claim.DeliveryVersion);
    }

    private static AgentLifecycleOutboxRecord ReadLifecycleOutboxRecord(SqliteDataReader reader)
        => new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            Enum.Parse<AgentLifecycleEventKind>(reader.GetString(3), ignoreCase: false),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
            reader.GetString(7),
            reader.GetString(8),
            DateTimeOffset.Parse(reader.GetString(9)),
            string.Equals(reader.GetString(10), "Erased", StringComparison.Ordinal),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12)),
            reader.IsDBNull(13) ? null : reader.GetInt64(13));
}
