using Microsoft.Data.Sqlite;

namespace Sunder.Package.Agent.HistorySearch;

internal sealed partial class HistorySearchStore
{
    private bool _requiresSecureRecreation;
    private bool _ftsSecureDeleteEnabled;

    private static void EnableSecureDelete(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA secure_delete = ON;";
        command.ExecuteNonQuery();
    }

    private static bool TryEnableFtsSecureDelete(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO HistoryDocumentsFts(HistoryDocumentsFts, rank)
                VALUES('secure-delete', 1);
                """;
            command.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1)
        {
            return false;
        }
    }

    private static bool IsFtsSecureDeleteEnabled(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT v FROM HistoryDocumentsFts_config WHERE k = 'secure-delete';";
            return Convert.ToInt64(command.ExecuteScalar()) == 1;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static void CheckpointTruncate(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using var reader = command.ExecuteReader();
        if (reader.Read() && reader.GetInt64(0) != 0)
        {
            throw new InvalidOperationException("History projection WAL checkpoint could not drain active readers.");
        }
    }

    private void RecreateDerivedDatabase(long configurationRevision, bool manuallyCleared)
    {
        EnsureCommittedRuntimeEpochCurrent();
        var replacementPath = DatabasePath + ".replacement-" + Guid.NewGuid().ToString("N");
        try
        {
            CreateSchema(
                GetBoundRuntimeEpoch(),
                configurationRevision,
                manuallyCleared,
                replacementPath);
            EnsureCommittedRuntimeEpochCurrent();
            TryCheckpointExistingDatabase();
            TryDelete(DatabasePath + "-wal");
            TryDelete(DatabasePath + "-shm");
            EnsureCommittedRuntimeEpochCurrent();
            File.Move(replacementPath, DatabasePath, overwrite: true);
            TryDelete(replacementPath + "-wal");
            TryDelete(replacementPath + "-shm");
            _requiresSecureRecreation = false;
            EnsureCommittedRuntimeEpochCurrent();
        }
        catch
        {
            TryDelete(replacementPath);
            TryDelete(replacementPath + "-wal");
            TryDelete(replacementPath + "-shm");
            throw;
        }
    }

    private void TryCheckpointExistingDatabase()
    {
        if (!File.Exists(DatabasePath))
        {
            return;
        }
        try
        {
            using var connection = CreateConnection();
            connection.Open();
            CheckpointTruncate(connection);
        }
        catch (Exception exception) when (IsConfirmedProjectionFailure(exception))
        {
            // A corrupt disposable database can still be replaced once all projection users are drained.
        }
    }
}
