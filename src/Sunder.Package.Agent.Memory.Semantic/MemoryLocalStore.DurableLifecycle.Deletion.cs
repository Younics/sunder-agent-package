using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Memory.Semantic;

public sealed partial class MemoryLocalStore
{
    private static void DeleteContributionEvidence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid memoryId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM SessionMemoryEvidence WHERE MemoryId = $memoryId AND ContributionId IS NOT NULL;";
        command.Parameters.AddWithValue("$memoryId", memoryId.ToString());
        command.ExecuteNonQuery();
    }

    private static void DeleteMemory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid memoryId)
    {
        DeleteMemoryEmbeddings(connection, transaction, memoryId);
        foreach (var table in new[] { "SessionMemoryEvidence", "SessionMemoryContributions", "SessionMemorySearch", "SessionMemories" })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE MemoryId = $memoryId;";
            command.Parameters.AddWithValue("$memoryId", memoryId.ToString());
            command.ExecuteNonQuery();
        }
    }

    private static void DeleteMemoryEmbeddings(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid memoryId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM SessionMemoryEmbeddings WHERE MemoryId = $memoryId;";
        command.Parameters.AddWithValue("$memoryId", memoryId.ToString());
        command.ExecuteNonQuery();
    }

    private static void DeleteSemanticSession(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId)
    {
        EmbeddingRepository.DeleteSession(connection, transaction, sessionId);
        EvidenceRepository.DeleteSession(connection, transaction, sessionId);
        using (var contributions = connection.CreateCommand())
        {
            contributions.Transaction = transaction;
            contributions.CommandText = "DELETE FROM SessionMemoryContributions WHERE SessionId = $sessionId;";
            contributions.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            contributions.ExecuteNonQuery();
        }
        MemoryRepository.DeleteSession(connection, transaction, sessionId);
    }

    private static void InsertTurnRetraction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentDurableLifecycleEventEnvelope lifecycleEvent,
        Guid sessionId,
        Guid turnId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO SessionMemoryTurnRetractions (
                SessionId, TurnId, EventId, PayloadHash, RetractedAtUtc)
            VALUES ($sessionId, $turnId, $eventId, $payloadHash, $retractedAtUtc);
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$turnId", turnId.ToString());
        command.Parameters.AddWithValue("$eventId", lifecycleEvent.EventId);
        command.Parameters.AddWithValue("$payloadHash", lifecycleEvent.PayloadHash);
        command.Parameters.AddWithValue("$retractedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        var affected = command.ExecuteNonQuery();
        if (affected is not (0 or 1))
        {
            throw new AgentDurableLifecycleIntegrityException(
                $"Turn retraction for '{turnId}' could not be recorded.");
        }
    }

    private static void InsertSessionDeletionTombstone(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        string? workspaceId,
        AgentDurableLifecycleEventEnvelope lifecycleEvent)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO SessionMemoryDeletionTombstones (
                SessionId, WorkspaceId, EventId, PayloadHash, DeletedAtUtc)
            VALUES ($sessionId, $workspaceId, $eventId, $payloadHash, $deletedAtUtc)
            ON CONFLICT(SessionId) DO UPDATE SET
                WorkspaceId = excluded.WorkspaceId,
                EventId = excluded.EventId,
                PayloadHash = excluded.PayloadHash,
                DeletedAtUtc = excluded.DeletedAtUtc;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$workspaceId", (object?)workspaceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$eventId", lifecycleEvent.EventId);
        command.Parameters.AddWithValue("$payloadHash", lifecycleEvent.PayloadHash);
        command.Parameters.AddWithValue("$deletedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new AgentDurableLifecycleIntegrityException(
                $"Session deletion tombstone for '{sessionId}' could not be recorded.");
        }
    }
}
