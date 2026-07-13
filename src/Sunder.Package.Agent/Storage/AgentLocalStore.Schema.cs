using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    private static void ApplyV1Baseline(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

            CREATE TABLE IF NOT EXISTS AgentWorkspacePaths (
                PathId TEXT PRIMARY KEY,
                WorkspaceId TEXT NOT NULL,
                HostPath TEXT NOT NULL,
                IsDefault INTEGER NOT NULL DEFAULT 0,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AgentWorkspaceDocuments (
                DocumentId TEXT PRIMARY KEY,
                WorkspaceId TEXT NOT NULL,
                FilePath TEXT NOT NULL,
                SortOrder INTEGER NOT NULL DEFAULT 0,
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
                WorkspaceId TEXT NULL,
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
                BackendId TEXT NULL,
                PresentationPayloadJson TEXT NULL
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

            CREATE TABLE IF NOT EXISTS AgentSessionContextCheckpoints (
                ContextCheckpointId TEXT PRIMARY KEY,
                SessionId TEXT NOT NULL,
                FirstOmittedTurnId TEXT NULL,
                LastOmittedTurnId TEXT NULL,
                OmittedTurnCount INTEGER NOT NULL,
                SummaryText TEXT NOT NULL,
                DetailsJson TEXT NULL,
                CreatedAtUtc TEXT NOT NULL
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
            CREATE INDEX IF NOT EXISTS IX_AgentWorkspacePaths_WorkspaceId_SortOrder ON AgentWorkspacePaths (WorkspaceId, SortOrder);
            CREATE INDEX IF NOT EXISTS IX_AgentWorkspaceDocuments_WorkspaceId_SortOrder ON AgentWorkspaceDocuments (WorkspaceId, SortOrder);
            CREATE INDEX IF NOT EXISTS IX_AgentTurns_SessionId_CreatedAtUtc ON AgentTurns (SessionId, CreatedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_AgentTurnItems_TurnId_SequenceNumber ON AgentTurnItems (TurnId, SequenceNumber);
            CREATE INDEX IF NOT EXISTS IX_AgentRunCheckpoints_SessionId_CreatedAtUtc ON AgentRunCheckpoints (SessionId, CreatedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_AgentRunCheckpoints_SessionId_RunRevision ON AgentRunCheckpoints (SessionId, RunRevision);
            CREATE INDEX IF NOT EXISTS IX_AgentSessionContextCheckpoints_SessionId_CreatedAtUtc ON AgentSessionContextCheckpoints (SessionId, CreatedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_AgentSessionPermissionApprovals_SessionId ON AgentSessionPermissionApprovals (SessionId);
            CREATE INDEX IF NOT EXISTS IX_AgentPendingPermissionRequests_SessionId ON AgentPendingPermissionRequests (SessionId);
            """;
        command.ExecuteNonQuery();

        AddV1LegacyColumns(connection, transaction);
        RemoveV1TraceTelemetry(connection, transaction);
        BackfillV1SessionHierarchy(connection, transaction);
        BackfillV1SessionWorkspaces(connection, transaction);
        BackfillV1ProfileModelBindings(connection, transaction);
        BackfillV1FailedSessionState(connection, transaction);
    }

    private static void AddV1LegacyColumns(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        AddColumnIfMissing(connection, transaction, "AgentTurnItems", "PresentationPayloadJson", "TEXT NULL");

        AddColumnIfMissing(connection, transaction, "AgentSessions", "WorkspaceId", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "AgentSessions", "ParentSessionId", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "AgentSessions", "RootSessionId", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "AgentSessions", "ParentRunId", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "AgentSessions", "ParentRunRevision", "INTEGER NULL");
        AddColumnIfMissing(connection, transaction, "AgentSessions", "ParentToolCallId", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "AgentSessions", "TaskId", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "AgentSessions", "ProfileId", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "AgentSessions", "BehaviorLoopId", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "AgentSessions", "AgentKind", "TEXT NULL");

        AddColumnIfMissing(connection, transaction, "AgentPendingPermissionRequests", "RunId", "TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'");
        AddColumnIfMissing(connection, transaction, "AgentPendingPermissionRequests", "RunRevision", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, transaction, "AgentPendingPermissionRequests", "ProfileId", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "AgentPendingPermissionRequests", "UserTurnId", "TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'");
        AddColumnIfMissing(connection, transaction, "AgentPendingPermissionRequests", "UserMessage", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, transaction, "AgentPendingPermissionRequests", "CallId", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, transaction, "AgentPendingPermissionRequests", "BoundaryId", "TEXT NOT NULL DEFAULT 'unknown'");
        AddColumnIfMissing(connection, transaction, "AgentPendingPermissionRequests", "ArgumentsJson", "TEXT NOT NULL DEFAULT '{}'");
        AddColumnIfMissing(connection, transaction, "AgentPendingPermissionRequests", "BindingId", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "AgentPendingPermissionRequests", "ResourceDisplayName", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "AgentPendingPermissionRequests", "ResourceReference", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "AgentPendingPermissionRequests", "ParentSessionId", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "AgentPendingPermissionRequests", "RootSessionId", "TEXT NULL");

        AddColumnIfMissing(connection, transaction, "AgentProfiles", "EmbeddingProviderId", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "AgentProfiles", "EmbeddingModelId", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "AgentProfiles", "BehaviorLoopId", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "AgentProfiles", "BehaviorLoopSourceId", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "AgentProfiles", "BehaviorLoopSettingsJson", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "AgentProfiles", "IsInternal", "INTEGER NOT NULL DEFAULT 0");

        using var indexCommand = connection.CreateCommand();
        indexCommand.Transaction = transaction;
        indexCommand.CommandText = """
            CREATE INDEX IF NOT EXISTS IX_AgentSessions_WorkspaceId_UpdatedAtUtc ON AgentSessions (WorkspaceId, UpdatedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_AgentSessions_RootSessionId ON AgentSessions (RootSessionId);
            CREATE INDEX IF NOT EXISTS IX_AgentSessions_ParentSessionId_UpdatedAtUtc ON AgentSessions (ParentSessionId, UpdatedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_AgentSessions_ProfileId ON AgentSessions (ProfileId);
            CREATE INDEX IF NOT EXISTS IX_AgentPendingPermissionRequests_RootSessionId ON AgentPendingPermissionRequests (RootSessionId);
            """;
        indexCommand.ExecuteNonQuery();
    }

    private static void BackfillV1SessionWorkspaces(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using (var existsCommand = connection.CreateCommand())
        {
            existsCommand.Transaction = transaction;
            existsCommand.CommandText = "SELECT 1 FROM AgentSessions WHERE WorkspaceId IS NULL OR TRIM(WorkspaceId) = '' LIMIT 1;";
            if (existsCommand.ExecuteScalar() is null)
            {
                return;
            }
        }

        EnsureUnassignedSessionsWorkspace(connection, transaction);

        using var updateCommand = connection.CreateCommand();
        updateCommand.Transaction = transaction;
        updateCommand.CommandText = "UPDATE AgentSessions SET WorkspaceId = $workspaceId WHERE WorkspaceId IS NULL OR TRIM(WorkspaceId) = '';";
        updateCommand.Parameters.AddWithValue("$workspaceId", UnassignedSessionsWorkspaceId);
        updateCommand.ExecuteNonQuery();
    }

    private static void RemoveV1TraceTelemetry(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DROP TABLE IF EXISTS AgentRunTraceEvents;";
        command.ExecuteNonQuery();
    }

    private static void BackfillV1SessionHierarchy(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE AgentSessions SET RootSessionId = SessionId WHERE RootSessionId IS NULL;";
        command.ExecuteNonQuery();
    }

    private static void BackfillV1ProfileModelBindings(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var chatCommand = connection.CreateCommand();
        chatCommand.Transaction = transaction;
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
        embeddingCommand.Transaction = transaction;
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

    private static void BackfillV1FailedSessionState(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

    private static void AddColumnIfMissing(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName,
        string columnDefinition)
    {
        if (TableHasColumn(connection, transaction, tableName, columnName))
        {
            return;
        }

        using var alterTableCommand = connection.CreateCommand();
        alterTableCommand.Transaction = transaction;
        alterTableCommand.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        alterTableCommand.ExecuteNonQuery();
    }

    private static bool TableHasColumn(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
