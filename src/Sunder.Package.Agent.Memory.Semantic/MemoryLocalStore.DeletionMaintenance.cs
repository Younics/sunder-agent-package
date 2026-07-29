using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Memory.Semantic;

public sealed partial class MemoryLocalStore
{
    private static bool RequiresDeletionMaintenance(AgentLifecycleEventKind kind)
        => kind is AgentLifecycleEventKind.TranscriptRolledBack
            or AgentLifecycleEventKind.SessionDeleted
            or AgentLifecycleEventKind.WorkspaceDeleted;

    private static void EnsureDeletionMaintenancePending(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventId,
        AgentLifecycleEventKind kind,
        string payloadHash)
    {
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO SemanticDeletionMaintenance (
                    EventId, EventType, PayloadHash, State, CreatedAtUtc, CompletedAtUtc)
                VALUES ($eventId, $eventType, $payloadHash, 'Pending', $createdAtUtc, NULL);
                """;
            insert.Parameters.AddWithValue("$eventId", eventId);
            insert.Parameters.AddWithValue("$eventType", kind.ToString());
            insert.Parameters.AddWithValue("$payloadHash", payloadHash);
            insert.Parameters.AddWithValue("$createdAtUtc", DateTimeOffset.UtcNow.ToString("O"));
            var affected = insert.ExecuteNonQuery();
            if (affected is not (0 or 1))
            {
                throw new AgentDurableLifecycleIntegrityException(
                    $"Deletion maintenance for event '{eventId}' could not be recorded.");
            }
        }

        var maintenance = ReadDeletionMaintenance(connection, transaction, eventId)
                          ?? throw new AgentDurableLifecycleIntegrityException(
                              $"Deletion maintenance for event '{eventId}' was not found after insertion.");
        ValidateDeletionMaintenance(maintenance, eventId, kind, payloadHash);
    }

    private void CompleteDeletionMaintenance(
        string eventId,
        AgentLifecycleEventKind kind,
        string payloadHash)
    {
        using var maintenanceLock = MemoryDatabase.AcquireMaintenanceLock(DatabasePath);
        using var connection = MemoryDatabase.OpenConnection(DatabasePath);
        var maintenance = ReadDeletionMaintenance(connection, transaction: null, eventId)
                          ?? throw new AgentDurableLifecycleIntegrityException(
                              $"Deletion maintenance for event '{eventId}' was not found after logical commit.");
        ValidateDeletionMaintenance(maintenance, eventId, kind, payloadHash);
        if (string.Equals(maintenance.State, "Completed", StringComparison.Ordinal))
        {
            return;
        }

        _physicalMaintenance.SecurePurge(connection);

        using var transaction = MemoryDatabase.BeginImmediateTransaction(connection);
        using var complete = connection.CreateCommand();
        complete.Transaction = transaction;
        complete.CommandText = """
            UPDATE SemanticDeletionMaintenance
            SET State = 'Completed', CompletedAtUtc = $completedAtUtc
            WHERE EventId = $eventId
              AND EventType = $eventType
              AND PayloadHash = $payloadHash
              AND State = 'Pending';
            """;
        complete.Parameters.AddWithValue("$completedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        complete.Parameters.AddWithValue("$eventId", eventId);
        complete.Parameters.AddWithValue("$eventType", kind.ToString());
        complete.Parameters.AddWithValue("$payloadHash", payloadHash);
        if (complete.ExecuteNonQuery() != 1)
        {
            throw new AgentDurableLifecycleIntegrityException(
                $"Deletion maintenance for event '{eventId}' could not be completed.");
        }
        transaction.Commit();
    }

    private static DeletionMaintenance? ReadDeletionMaintenance(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string eventId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EventType, PayloadHash, State
            FROM SemanticDeletionMaintenance
            WHERE EventId = $eventId;
            """;
        command.Parameters.AddWithValue("$eventId", eventId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new DeletionMaintenance(reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private static void ValidateDeletionMaintenance(
        DeletionMaintenance maintenance,
        string eventId,
        AgentLifecycleEventKind kind,
        string payloadHash)
    {
        if (!string.Equals(maintenance.EventType, kind.ToString(), StringComparison.Ordinal)
            || !string.Equals(maintenance.PayloadHash, payloadHash, StringComparison.Ordinal)
            || maintenance.State is not ("Pending" or "Completed"))
        {
            throw new AgentDurableLifecycleIntegrityException(
                $"Deletion maintenance for event '{eventId}' has conflicting identity or state.");
        }
    }

    private sealed record DeletionMaintenance(string EventType, string PayloadHash, string State);
}
