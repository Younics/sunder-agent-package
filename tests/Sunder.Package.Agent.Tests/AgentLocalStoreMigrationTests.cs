using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Storage;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentLocalStoreMigrationTests
{
    [Fact]
    public void FreshStore_CreatesExactCurrentBaseline()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        using var connection = OpenDatabase(store.DatabasePath);

        Assert.Equal("wal", ExecuteString(connection, "PRAGMA journal_mode;"));
        Assert.Equal(1L, ExecuteInt64(connection, "SELECT COUNT(*) FROM SchemaMigrations;"));
        Assert.Equal(
            $"{AgentLocalStore.BaselineVersion}|{AgentLocalStore.BaselineName}|{AgentLocalStore.BaselineChecksum}",
            ExecuteString(connection, "SELECT Version || '|' || Name || '|' || Checksum FROM SchemaMigrations;"));
        var sql = string.Concat(new[] { "BaselineSql", "TranscriptIndexSql" }.Select(name =>
            (string)typeof(AgentLocalStore).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue()!));
        Assert.Equal(AgentLocalStore.BaselineChecksum, Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sql))));
        Assert.True(TableExists(connection, "AgentRuntimeGenerations"));
        Assert.True(TableExists(connection, "AgentLifecycleGenerationState"));
        Assert.True(TableExists(connection, "AgentSessionCleanupJobs"));
        Assert.True(ColumnExists(connection, "AgentLifecycleOutbox", "PayloadErasureGeneration"));
        Assert.True(ColumnExists(connection, "AgentToolExecutions", "ExecutionTargetOwnerPackageId"));
        Assert.True(ColumnExists(connection, "AgentPendingPermissionRequests", "ApproveForSession"));
        Assert.True(TableExists(connection, "AgentTranscriptEntries"));
        Assert.True(TableExists(connection, "AgentTranscriptGroups"));
        Assert.True(ColumnExists(connection, "AgentTurns", "TurnSequence"));
        Assert.True(ColumnExists(connection, "AgentSessions", "PresentationEpoch"));
        Assert.True(ColumnExists(connection, "AgentToolExecutions", "DetailRevision"));
        Assert.True(IndexExists(connection, "IX_AgentTurns_RunIdentity"));
        Assert.True(IndexExists(connection, "IX_AgentLifecycleOutbox_ErasureGeneration"));
        Assert.True(TriggerExists(connection, "TR_AgentLifecycleOutbox_ImmutableDelete"));
    }

    [Fact]
    public void ConcurrentFreshStores_CreateOneBaselineEntry()
    {
        using var scope = RegressionTestPackageScope.Create();

        Parallel.For(0, 12, _ => new AgentLocalStore(scope.Context));

        using var connection = OpenDatabase(GetDatabasePath(scope));
        Assert.Equal(1L, ExecuteInt64(connection, "SELECT COUNT(*) FROM SchemaMigrations;"));
    }

    [Fact]
    public void EmptyDatabaseFile_IsAccepted()
    {
        using var scope = RegressionTestPackageScope.Create();
        var databasePath = GetDatabasePath(scope);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.WriteAllBytes(databasePath, []);

        _ = new AgentLocalStore(scope.Context);

        using var connection = OpenDatabase(databasePath);
        Assert.Equal(1L, ExecuteInt64(connection, "SELECT COUNT(*) FROM SchemaMigrations;"));
    }

    [Theory]
    [InlineData("ledgerless")]
    [InlineData("partial")]
    [InlineData("old-ledger")]
    [InlineData("tampered")]
    public void UnsupportedDatabase_IsRejectedWithoutMutation(string shape)
    {
        using var scope = RegressionTestPackageScope.Create();
        var databasePath = GetDatabasePath(scope);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using (var connection = OpenDatabase(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = shape switch
            {
                "ledgerless" => "CREATE TABLE ExistingData (Value TEXT); INSERT INTO ExistingData VALUES ('keep');",
                "partial" => "CREATE TABLE SchemaMigrations (Version INTEGER PRIMARY KEY, Name TEXT NOT NULL);",
                "old-ledger" => """
                    CREATE TABLE SchemaMigrations (
                        Version INTEGER PRIMARY KEY,
                        Name TEXT NOT NULL,
                        Checksum TEXT NOT NULL,
                        AppliedAtUtc TEXT NOT NULL);
                    INSERT INTO SchemaMigrations VALUES (21, 'legacy-tool-execution-identities', 'old', '2026-01-01T00:00:00Z');
                    """,
                "tampered" => $"""
                    CREATE TABLE SchemaMigrations (
                        Version INTEGER PRIMARY KEY,
                        Name TEXT NOT NULL,
                        Checksum TEXT NOT NULL,
                        AppliedAtUtc TEXT NOT NULL);
                    INSERT INTO SchemaMigrations VALUES ({AgentLocalStore.BaselineVersion}, '{AgentLocalStore.BaselineName}', 'tampered', '2026-01-01T00:00:00Z');
                    """,
                _ => throw new ArgumentOutOfRangeException(nameof(shape)),
            };
            command.ExecuteNonQuery();
        }
        var before = File.ReadAllBytes(databasePath);

        var exception = Assert.Throws<InvalidOperationException>(() => new AgentLocalStore(scope.Context));

        Assert.Contains("migration ledger validation failed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllBytes(databasePath));
    }

    [Fact]
    public void CurrentStore_ReopensWithoutChangingLedger()
    {
        using var scope = RegressionTestPackageScope.Create();
        var first = new AgentLocalStore(scope.Context);
        using (var connection = OpenDatabase(first.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT AppliedAtUtc FROM SchemaMigrations;";
            var appliedAt = (string)command.ExecuteScalar()!;
            _ = new AgentLocalStore(scope.Context);
            Assert.Equal(appliedAt, ExecuteString(connection, "SELECT AppliedAtUtc FROM SchemaMigrations;"));
        }
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

    private static string ExecuteString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
        => ObjectExists(connection, "table", tableName);

    private static bool IndexExists(SqliteConnection connection, string indexName)
        => ObjectExists(connection, "index", indexName);

    private static bool TriggerExists(SqliteConnection connection, string triggerName)
        => ObjectExists(connection, "trigger", triggerName);

    private static bool ObjectExists(SqliteConnection connection, string type, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = $type AND name = $name;";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is not null;
    }

    private static bool ColumnExists(SqliteConnection connection, string tableName, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
