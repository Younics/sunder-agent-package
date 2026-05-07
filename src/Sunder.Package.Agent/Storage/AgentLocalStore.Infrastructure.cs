using Microsoft.Data.Sqlite;
using System.Runtime.InteropServices;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    private SqliteConnection CreateConnection() => new($"Data Source={DatabasePath}");

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
