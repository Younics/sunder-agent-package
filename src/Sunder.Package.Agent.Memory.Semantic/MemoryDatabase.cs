using System.Diagnostics;
using Microsoft.Data.Sqlite;
using System.Runtime.InteropServices;

namespace Sunder.Package.Agent.Memory.Semantic;

internal static class MemoryDatabase
{
    private static readonly object InitializationSync = new();
    private static bool _initialized;

    public static void Initialize(string installPath)
    {
        lock (InitializationSync)
        {
            if (_initialized)
            {
                return;
            }

            var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "e_sqlite3.dll"
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? "libe_sqlite3.dylib"
                    : "libe_sqlite3.so";

            if (Directory.Exists(installPath))
            {
                foreach (var candidatePath in Directory.EnumerateFiles(installPath, fileName, SearchOption.AllDirectories))
                {
                    try
                    {
                        NativeLibrary.Load(candidatePath);
                        break;
                    }
                    catch
                    {
                        // Continue probing until a native binary for the current platform loads.
                    }
                }
            }

            SQLitePCL.Batteries_V2.Init();
            _initialized = true;
        }
    }

    public static SqliteConnection OpenConnection(string databasePath)
    {
        var connection = new SqliteConnection(CreateConnectionString(databasePath));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 30000; PRAGMA secure_delete = ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    public static SqliteTransaction BeginImmediateTransaction(SqliteConnection connection)
        => connection.BeginTransaction(deferred: false);

    public static bool IsSessionDeleted(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM SessionMemoryDeletionTombstones WHERE SessionId = $sessionId LIMIT 1;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        return command.ExecuteScalar() is not null;
    }

    public static void ThrowIfSessionDeleted(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId)
    {
        if (IsSessionDeleted(connection, transaction, sessionId))
        {
            throw new InvalidOperationException(
                $"Semantic memory session '{sessionId}' was deleted and cannot be mutated.");
        }
    }

    public static void TruncateWal(SqliteConnection connection)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var checkpointBusy = true;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                using var reader = command.ExecuteReader();
                checkpointBusy = !reader.Read() || reader.GetInt32(0) != 0;
            }

            if (!checkpointBusy)
            {
                return;
            }
            Thread.Sleep(50);
        }

        throw new InvalidOperationException(
            "Semantic memory deletion committed, but the SQLite WAL could not be safely truncated.");
    }

    public static void SecurePurge(SqliteConnection connection)
    {
        TruncateWal(connection);
        using (var vacuum = connection.CreateCommand())
        {
            vacuum.CommandText = "VACUUM;";
            vacuum.ExecuteNonQuery();
        }
        TruncateWal(connection);
    }

    public static FileStream AcquireMaintenanceLock(string databasePath)
    {
        var lockPath = Path.GetFullPath(databasePath) + ".maintenance.lock";
        var timeout = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException) when (timeout.Elapsed < TimeSpan.FromSeconds(30))
            {
                Thread.Sleep(50);
            }
        }
    }

    public static string CreateConnectionString(string databasePath)
        => new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString();
}
