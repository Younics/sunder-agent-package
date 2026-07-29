using Microsoft.Data.Sqlite;
using System.Runtime.InteropServices;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    private SqliteConnection CreateConnection() => new(CreateConnectionString(DatabasePath));

    private static string CreateConnectionString(string databasePath)
        => new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();

    private static void EnableSecureDelete(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA secure_delete = ON;";
        command.ExecuteNonQuery();
    }

    private static void CheckpointWriteAheadLog(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using var reader = command.ExecuteReader();
        if (reader.Read() && reader.GetInt64(0) != 0)
        {
            throw new InvalidOperationException("The Agent store WAL checkpoint could not drain active readers.");
        }
    }

    private static void EnsureSqliteNativeLibraryLoaded(string installPath)
    {
        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "e_sqlite3.dll"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "libe_sqlite3.dylib"
                : "libe_sqlite3.so";

        foreach (var candidatePath in Directory.EnumerateFiles(installPath, fileName, SearchOption.AllDirectories))
        {
            try
            {
                NativeLibrary.Load(candidatePath);
                return;
            }
            catch
            {
                // Continue probing until a matching native binary loads successfully.
            }
        }
    }
}
