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
        command.CommandText = "PRAGMA busy_timeout = 30000;";
        command.ExecuteNonQuery();
        return connection;
    }

    public static string CreateConnectionString(string databasePath)
        => new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString();
}
