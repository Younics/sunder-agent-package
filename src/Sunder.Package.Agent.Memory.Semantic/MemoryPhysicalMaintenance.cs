using Microsoft.Data.Sqlite;

namespace Sunder.Package.Agent.Memory.Semantic;

internal interface IMemoryPhysicalMaintenance
{
    void SecurePurge(SqliteConnection connection);
}

internal sealed class SqliteMemoryPhysicalMaintenance : IMemoryPhysicalMaintenance
{
    public static SqliteMemoryPhysicalMaintenance Instance { get; } = new();

    public void SecurePurge(SqliteConnection connection) => MemoryDatabase.SecurePurge(connection);
}
