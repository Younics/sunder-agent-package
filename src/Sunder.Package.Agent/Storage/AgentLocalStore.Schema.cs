using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    private void EnsureSchema()
    {
        using var connection = CreateConnection();
        connection.Open();

        using (var pragmaCommand = connection.CreateCommand())
        {
            pragmaCommand.CommandText = "PRAGMA journal_mode=WAL;";
            pragmaCommand.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS AgentProfiles (
                ProfileId TEXT PRIMARY KEY,
                DisplayName TEXT NOT NULL,
                Description TEXT NULL,
                Instructions TEXT NULL,
                ProviderId TEXT NULL,
                ModelId TEXT NULL,
                EmbeddingProviderId TEXT NULL,
                EmbeddingModelId TEXT NULL,
                BehaviorLoopId TEXT NULL,
                BehaviorLoopSourceId TEXT NULL,
                BehaviorLoopSettingsJson TEXT NULL,
                IsInternal INTEGER NOT NULL DEFAULT 0,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AgentWorkspaces (
                WorkspaceId TEXT PRIMARY KEY,
                DisplayName TEXT NOT NULL,
                Description TEXT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AgentWorkspaceBindings (
                BindingId TEXT PRIMARY KEY,
                WorkspaceId TEXT NOT NULL,
                ExtensionPointId TEXT NOT NULL,
                ContributionId TEXT NOT NULL,
                Role TEXT NOT NULL,
                IsEnabled INTEGER NOT NULL DEFAULT 1,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AgentSessions (
                SessionId TEXT PRIMARY KEY,
                Title TEXT NOT NULL,
                State TEXT NOT NULL,
                ParentSessionId TEXT NULL,
                RootSessionId TEXT NULL,
                ParentRunId TEXT NULL,
                ParentRunRevision INTEGER NULL,
                ParentToolCallId TEXT NULL,
                TaskId TEXT NULL,
                ProfileId TEXT NULL,
                BehaviorLoopId TEXT NULL,
                AgentKind TEXT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AgentProfileSelectableCapabilityAssignments (
                ProfileId TEXT NOT NULL,
                Kind TEXT NOT NULL,
                CapabilityId TEXT NOT NULL,
                SourceId TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (ProfileId, Kind, CapabilityId, SourceId)
            );

            CREATE TABLE IF NOT EXISTS AgentProfileModelBindings (
                ProfileId TEXT NOT NULL,
                CapabilityKind TEXT NOT NULL,
                ProviderId TEXT NULL,
                ModelId TEXT NULL,
                SettingsJson TEXT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                PRIMARY KEY (ProfileId, CapabilityKind)
            );

            CREATE TABLE IF NOT EXISTS AgentTurns (
                TurnId TEXT PRIMARY KEY,
                SessionId TEXT NOT NULL,
                Role TEXT NOT NULL,
                Kind TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AgentTurnItems (
                ItemId TEXT PRIMARY KEY,
                TurnId TEXT NOT NULL,
                SequenceNumber INTEGER NOT NULL,
                Kind TEXT NOT NULL,
                TextContent TEXT NULL,
                CallId TEXT NULL,
                ToolId TEXT NULL,
                ArgumentsJson TEXT NULL,
                ResultSummary TEXT NULL,
                StructuredPayloadJson TEXT NULL,
                SourcesJson TEXT NULL,
                WasTruncated INTEGER NOT NULL DEFAULT 0,
                IsError INTEGER NOT NULL DEFAULT 0,
                ErrorCode TEXT NULL,
                BackendId TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS AgentRunCheckpoints (
                CheckpointId TEXT PRIMARY KEY,
                SessionId TEXT NOT NULL,
                RunRevision INTEGER NOT NULL,
                Status TEXT NOT NULL,
                Summary TEXT NULL,
                CreatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AgentWorkingSummaries (
                SessionId TEXT PRIMARY KEY,
                SummaryText TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AgentPermissionRules (
                RuleId TEXT PRIMARY KEY,
                ActionId TEXT NOT NULL,
                MatcherKind TEXT NOT NULL,
                Pattern TEXT NOT NULL,
                Decision TEXT NOT NULL,
                SortOrder INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AgentPermissionOverrides (
                ActionId TEXT NOT NULL,
                BoundaryId TEXT NOT NULL,
                Decision TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                PRIMARY KEY (ActionId, BoundaryId)
            );

            CREATE TABLE IF NOT EXISTS AgentSessionPermissionStates (
                SessionId TEXT PRIMARY KEY,
                IsUnrestrictedModeEnabled INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS AgentSessionPermissionApprovals (
                ApprovalId TEXT PRIMARY KEY,
                SessionId TEXT NOT NULL,
                ActionId TEXT NOT NULL,
                MatcherKind TEXT NOT NULL,
                Pattern TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AgentPendingPermissionRequests (
                RequestId TEXT PRIMARY KEY,
                SessionId TEXT NOT NULL,
                RunId TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
                RunRevision INTEGER NOT NULL DEFAULT 0,
                ProfileId TEXT NULL,
                UserTurnId TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
                UserMessage TEXT NOT NULL DEFAULT '',
                CallId TEXT NOT NULL DEFAULT '',
                ActionId TEXT NOT NULL,
                BoundaryId TEXT NOT NULL DEFAULT 'unknown',
                Summary TEXT NOT NULL,
                ToolId TEXT NULL,
                ArgumentsJson TEXT NOT NULL DEFAULT '{}',
                Command TEXT NULL,
                Path TEXT NULL,
                TargetKind TEXT NULL,
                TargetId TEXT NULL,
                WorkspaceId TEXT NULL,
                BindingId TEXT NULL,
                ResourceDisplayName TEXT NULL,
                ResourceReference TEXT NULL,
                IsMutation INTEGER NOT NULL DEFAULT 0,
                CreatedAtUtc TEXT NOT NULL,
                ParentSessionId TEXT NULL,
                RootSessionId TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_AgentWorkspaceBindings_WorkspaceId_Role ON AgentWorkspaceBindings (WorkspaceId, Role);
            CREATE INDEX IF NOT EXISTS IX_AgentTurns_SessionId_CreatedAtUtc ON AgentTurns (SessionId, CreatedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_AgentTurnItems_TurnId_SequenceNumber ON AgentTurnItems (TurnId, SequenceNumber);
            CREATE INDEX IF NOT EXISTS IX_AgentRunCheckpoints_SessionId_CreatedAtUtc ON AgentRunCheckpoints (SessionId, CreatedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_AgentRunCheckpoints_SessionId_RunRevision ON AgentRunCheckpoints (SessionId, RunRevision);
            CREATE INDEX IF NOT EXISTS IX_AgentSessionPermissionApprovals_SessionId ON AgentSessionPermissionApprovals (SessionId);
            CREATE INDEX IF NOT EXISTS IX_AgentPendingPermissionRequests_SessionId ON AgentPendingPermissionRequests (SessionId);
            """;
        command.ExecuteNonQuery();
    }

    private void EnsureSessionWorkspaceDecoupledMigration()
    {
        using var connection = CreateConnection();
        connection.Open();
        if (!TableHasColumn(connection, "AgentSessions", "WorkspaceId"))
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        using (var cleanupCommand = connection.CreateCommand())
        {
            cleanupCommand.Transaction = transaction;
            cleanupCommand.CommandText = "DROP TABLE IF EXISTS AgentSessions_New;";
            cleanupCommand.ExecuteNonQuery();
        }

        using (var createCommand = connection.CreateCommand())
        {
            createCommand.Transaction = transaction;
            createCommand.CommandText = """
                CREATE TABLE AgentSessions_New (
                    SessionId TEXT PRIMARY KEY,
                    Title TEXT NOT NULL,
                    State TEXT NOT NULL,
                    ParentSessionId TEXT NULL,
                    RootSessionId TEXT NULL,
                    ParentRunId TEXT NULL,
                    ParentRunRevision INTEGER NULL,
                    ParentToolCallId TEXT NULL,
                    TaskId TEXT NULL,
                    ProfileId TEXT NULL,
                    BehaviorLoopId TEXT NULL,
                    AgentKind TEXT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                """;
            createCommand.ExecuteNonQuery();
        }

        using (var copyCommand = connection.CreateCommand())
        {
            copyCommand.Transaction = transaction;
            copyCommand.CommandText = """
                INSERT INTO AgentSessions_New (SessionId, Title, State, ParentSessionId, RootSessionId, ParentRunId, ParentRunRevision, ParentToolCallId, TaskId, ProfileId, BehaviorLoopId, AgentKind, CreatedAtUtc, UpdatedAtUtc)
                SELECT SessionId, Title, State, ParentSessionId, RootSessionId, ParentRunId, ParentRunRevision, ParentToolCallId, TaskId, ProfileId, BehaviorLoopId, AgentKind, CreatedAtUtc, UpdatedAtUtc
                FROM AgentSessions;
                """;
            copyCommand.ExecuteNonQuery();
        }

        using (var dropCommand = connection.CreateCommand())
        {
            dropCommand.Transaction = transaction;
            dropCommand.CommandText = "DROP TABLE AgentSessions;";
            dropCommand.ExecuteNonQuery();
        }

        using (var renameCommand = connection.CreateCommand())
        {
            renameCommand.Transaction = transaction;
            renameCommand.CommandText = "ALTER TABLE AgentSessions_New RENAME TO AgentSessions;";
            renameCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private void EnsureTraceTelemetryRemoved()
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE IF EXISTS AgentRunTraceEvents;";
        command.ExecuteNonQuery();
    }

    private void EnsurePendingPermissionMigration()
    {
        using var connection = CreateConnection();
        connection.Open();

        EnsureTableColumnExists(connection, "AgentPendingPermissionRequests", "RunId", "TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'");
        EnsureTableColumnExists(connection, "AgentPendingPermissionRequests", "RunRevision", "INTEGER NOT NULL DEFAULT 0");
        EnsureTableColumnExists(connection, "AgentPendingPermissionRequests", "ProfileId", "TEXT NULL");
        EnsureTableColumnExists(connection, "AgentPendingPermissionRequests", "UserTurnId", "TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'");
        EnsureTableColumnExists(connection, "AgentPendingPermissionRequests", "UserMessage", "TEXT NOT NULL DEFAULT ''");
        EnsureTableColumnExists(connection, "AgentPendingPermissionRequests", "CallId", "TEXT NOT NULL DEFAULT ''");
        EnsureTableColumnExists(connection, "AgentPendingPermissionRequests", "BoundaryId", "TEXT NOT NULL DEFAULT 'unknown'");
        EnsureTableColumnExists(connection, "AgentPendingPermissionRequests", "ArgumentsJson", "TEXT NOT NULL DEFAULT '{}'");
        EnsureTableColumnExists(connection, "AgentPendingPermissionRequests", "BindingId", "TEXT NULL");
        EnsureTableColumnExists(connection, "AgentPendingPermissionRequests", "ResourceDisplayName", "TEXT NULL");
        EnsureTableColumnExists(connection, "AgentPendingPermissionRequests", "ResourceReference", "TEXT NULL");
        EnsureTableColumnExists(connection, "AgentPendingPermissionRequests", "ParentSessionId", "TEXT NULL");
        EnsureTableColumnExists(connection, "AgentPendingPermissionRequests", "RootSessionId", "TEXT NULL");

        using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText = "CREATE INDEX IF NOT EXISTS IX_AgentPendingPermissionRequests_RootSessionId ON AgentPendingPermissionRequests (RootSessionId);";
        indexCommand.ExecuteNonQuery();
    }

    private void EnsureSessionHierarchyMigration()
    {
        using var connection = CreateConnection();
        connection.Open();

        EnsureTableColumnExists(connection, "AgentSessions", "ParentSessionId", "TEXT NULL");
        EnsureTableColumnExists(connection, "AgentSessions", "RootSessionId", "TEXT NULL");
        EnsureTableColumnExists(connection, "AgentSessions", "ParentRunId", "TEXT NULL");
        EnsureTableColumnExists(connection, "AgentSessions", "ParentRunRevision", "INTEGER NULL");
        EnsureTableColumnExists(connection, "AgentSessions", "ParentToolCallId", "TEXT NULL");
        EnsureTableColumnExists(connection, "AgentSessions", "TaskId", "TEXT NULL");
        EnsureTableColumnExists(connection, "AgentSessions", "ProfileId", "TEXT NULL");
        EnsureTableColumnExists(connection, "AgentSessions", "BehaviorLoopId", "TEXT NULL");
        EnsureTableColumnExists(connection, "AgentSessions", "AgentKind", "TEXT NULL");

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE AgentSessions SET RootSessionId = SessionId WHERE RootSessionId IS NULL;";
        command.ExecuteNonQuery();

        using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText = """
            CREATE INDEX IF NOT EXISTS IX_AgentSessions_RootSessionId ON AgentSessions (RootSessionId);
            CREATE INDEX IF NOT EXISTS IX_AgentSessions_ParentSessionId_UpdatedAtUtc ON AgentSessions (ParentSessionId, UpdatedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_AgentSessions_ProfileId ON AgentSessions (ProfileId);
            """;
        indexCommand.ExecuteNonQuery();
    }

    private void EnsureProfileSchemaMigration()
    {
        using var connection = CreateConnection();
        connection.Open();

        EnsureTableColumnExists(connection, "AgentProfiles", "EmbeddingProviderId", "TEXT NULL");
        EnsureTableColumnExists(connection, "AgentProfiles", "EmbeddingModelId", "TEXT NULL");
        EnsureTableColumnExists(connection, "AgentProfiles", "BehaviorLoopId", "TEXT NULL");
        EnsureTableColumnExists(connection, "AgentProfiles", "BehaviorLoopSourceId", "TEXT NULL");
        EnsureTableColumnExists(connection, "AgentProfiles", "BehaviorLoopSettingsJson", "TEXT NULL");
        EnsureTableColumnExists(connection, "AgentProfiles", "IsInternal", "INTEGER NOT NULL DEFAULT 0");
    }

    private void EnsureProfileModelBindingMigration()
    {
        using var connection = CreateConnection();
        connection.Open();

        using var chatCommand = connection.CreateCommand();
        chatCommand.CommandText = """
            INSERT OR IGNORE INTO AgentProfileModelBindings (ProfileId, CapabilityKind, ProviderId, ModelId, SettingsJson, UpdatedAtUtc)
            SELECT ProfileId, $chatCapabilityKind, ProviderId, ModelId, NULL, UpdatedAtUtc
            FROM AgentProfiles
            WHERE (ProviderId IS NOT NULL AND trim(ProviderId) <> '')
               OR (ModelId IS NOT NULL AND trim(ModelId) <> '');
            """;
        chatCommand.Parameters.AddWithValue("$chatCapabilityKind", AgentModelCapabilityKinds.Chat);
        chatCommand.ExecuteNonQuery();

        using var embeddingCommand = connection.CreateCommand();
        embeddingCommand.CommandText = """
            INSERT OR IGNORE INTO AgentProfileModelBindings (ProfileId, CapabilityKind, ProviderId, ModelId, SettingsJson, UpdatedAtUtc)
            SELECT ProfileId, $embeddingCapabilityKind, EmbeddingProviderId, EmbeddingModelId, NULL, UpdatedAtUtc
            FROM AgentProfiles
            WHERE (EmbeddingProviderId IS NOT NULL AND trim(EmbeddingProviderId) <> '')
               OR (EmbeddingModelId IS NOT NULL AND trim(EmbeddingModelId) <> '');
            """;
        embeddingCommand.Parameters.AddWithValue("$embeddingCapabilityKind", AgentModelCapabilityKinds.Embedding);
        embeddingCommand.ExecuteNonQuery();
    }

    private void EnsureFailedSessionStateMigration()
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE AgentSessions
            SET State = 'Failed'
            WHERE State = 'Active'
              AND (
                  SELECT Status
                  FROM AgentRunCheckpoints
                  WHERE AgentRunCheckpoints.SessionId = AgentSessions.SessionId
                  ORDER BY CreatedAtUtc DESC
                  LIMIT 1
              ) = 'Failed';
            """;
        command.ExecuteNonQuery();
    }

    private static bool EnsureTableColumnExists(SqliteConnection connection, string tableName, string columnName, string columnDefinition)
    {
        if (TableHasColumn(connection, tableName, columnName))
        {
            return false;
        }

        using var alterTableCommand = connection.CreateCommand();
        alterTableCommand.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        alterTableCommand.ExecuteNonQuery();
        return true;
    }

    private static bool TableHasColumn(SqliteConnection connection, string tableName, string columnName)
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
