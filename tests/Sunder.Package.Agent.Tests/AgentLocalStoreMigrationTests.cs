using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Storage;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentLocalStoreMigrationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void HistoricalSchemaFixture_UpgradesToCurrentSchema(int historicalVersion)
    {
        using var scope = RegressionTestPackageScope.Create();
        var databasePath = GetDatabasePath(scope);
        CreateHistoricalFixture(databasePath, historicalVersion);

        var store = new AgentLocalStore(scope.Context);

        using var connection = OpenDatabase(store.DatabasePath);
        Assert.Equal(7L, ExecuteInt64(connection, "SELECT COUNT(*) FROM SchemaMigrations;"));
        Assert.Equal(7L, ExecuteInt64(connection, "SELECT MAX(Version) FROM SchemaMigrations;"));
        Assert.Equal(7L, ExecuteInt64(connection,
            "SELECT COUNT(*) FROM SchemaMigrations WHERE length(Checksum) = 64;"));
        Assert.True(ColumnExists(connection, "AgentPendingPermissionRequests", "ExecutionSnapshotJson"));
        Assert.True(TableExists(connection, "AgentParentContinuationWork"));
    }

    [Fact]
    public void InterruptedMigration_RollsBackSchemaAndLedgerTogether()
    {
        using var scope = RegressionTestPackageScope.Create();
        var databasePath = GetDatabasePath(scope);
        CreateHistoricalFixture(databasePath, 6);
        using (var connection = OpenDatabase(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER InterruptMigrationSeven
                BEFORE INSERT ON SchemaMigrations
                WHEN NEW.Version = 7
                BEGIN
                    SELECT RAISE(ABORT, 'simulated migration interruption');
                END;
                """;
            command.ExecuteNonQuery();
        }

        Assert.Throws<SqliteException>(() => new AgentLocalStore(scope.Context));

        using var verification = OpenDatabase(databasePath);
        Assert.Equal(6L, ExecuteInt64(verification, "SELECT MAX(Version) FROM SchemaMigrations;"));
        Assert.False(ColumnExists(verification, "AgentPendingPermissionRequests", "ExecutionSnapshotJson"));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("checksum")]
    [InlineData("newer")]
    [InlineData("unknown")]
    public void InvalidMigrationLedger_FailsClosedWithRecoveryGuidance(string corruption)
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        using (var connection = OpenDatabase(store.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = corruption switch
            {
                "name" => "UPDATE SchemaMigrations SET Name = 'renamed' WHERE Version = 3;",
                "checksum" => "UPDATE SchemaMigrations SET Checksum = 'tampered' WHERE Version = 4;",
                "newer" => "INSERT INTO SchemaMigrations VALUES (8, 'future', 'future', '2026-01-01T00:00:00Z');",
                "unknown" => "UPDATE SchemaMigrations SET Version = 0 WHERE Version = 1;",
                _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
            };
            command.ExecuteNonQuery();
        }

        var exception = Assert.Throws<InvalidOperationException>(() => new AgentLocalStore(scope.Context));
        Assert.Contains("migration ledger validation failed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Back up 'agent/agent.db'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("do not edit or delete migration ledger rows", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyLedgerWithoutChecksums_IsUpgradedOnlyForKnownMigrations()
    {
        using var scope = RegressionTestPackageScope.Create();
        var databasePath = GetDatabasePath(scope);
        CreateHistoricalFixture(databasePath, 2);
        using (var connection = OpenDatabase(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE LegacySchemaMigrations AS
                    SELECT Version, Name, AppliedAtUtc FROM SchemaMigrations;
                DROP TABLE SchemaMigrations;
                ALTER TABLE LegacySchemaMigrations RENAME TO SchemaMigrations;
                """;
            command.ExecuteNonQuery();
        }

        var store = new AgentLocalStore(scope.Context);

        using var verification = OpenDatabase(store.DatabasePath);
        Assert.True(ColumnExists(verification, "SchemaMigrations", "Checksum"));
        Assert.Equal(7L, ExecuteInt64(verification,
            "SELECT COUNT(*) FROM SchemaMigrations WHERE length(Checksum) = 64;"));
    }

    private static void CreateHistoricalFixture(string databasePath, int version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using var connection = OpenDatabase(databasePath);
        AgentLocalStore.ApplySchemaMigrations(connection, version);
    }

    private static string GetDatabasePath(RegressionTestPackageScope scope)
        => scope.Context.Storage.RoleLocalWorkspace.GetLocalPath("agent/agent.db");

    private static SqliteConnection OpenDatabase(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static long ExecuteInt64(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return command.ExecuteScalar() is not null;
    }

    private static bool ColumnExists(SqliteConnection connection, string tableName, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
