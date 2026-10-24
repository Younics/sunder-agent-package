using Microsoft.Data.Sqlite;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    internal const int BaselineVersion = 1;
    internal const string BaselineName = "agent-2.0-baseline";

    private const string BaselineSql = """
        CREATE TABLE SchemaMigrations (
            Version INTEGER PRIMARY KEY,
            Name TEXT NOT NULL,
            Checksum TEXT NOT NULL,
            AppliedAtUtc TEXT NOT NULL
        );

        CREATE TABLE AgentProfiles (
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

        CREATE TABLE AgentWorkspaces (
            WorkspaceId TEXT PRIMARY KEY,
            DisplayName TEXT NOT NULL,
            Description TEXT NULL,
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL
        );

        CREATE TABLE AgentWorkspacePaths (
            PathId TEXT PRIMARY KEY,
            WorkspaceId TEXT NOT NULL,
            HostPath TEXT NOT NULL,
            IsDefault INTEGER NOT NULL DEFAULT 0,
            SortOrder INTEGER NOT NULL DEFAULT 0,
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL
        );

        CREATE TABLE AgentWorkspaceDocuments (
            DocumentId TEXT PRIMARY KEY,
            WorkspaceId TEXT NOT NULL,
            FilePath TEXT NOT NULL,
            SortOrder INTEGER NOT NULL DEFAULT 0,
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL
        );

        CREATE TABLE AgentWorkspaceBindings (
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

        CREATE TABLE AgentSessions (
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
            UpdatedAtUtc TEXT NOT NULL,
            TranscriptEpoch INTEGER NOT NULL DEFAULT 1 CHECK (TranscriptEpoch >= 1),
            ActiveContextCheckpointId TEXT NULL,
            ActiveContextGeneration INTEGER NOT NULL DEFAULT 0 CHECK (ActiveContextGeneration >= 0)
        );

        CREATE TABLE AgentProfileSelectableCapabilityAssignments (
            ProfileId TEXT NOT NULL,
            Kind TEXT NOT NULL,
            CapabilityId TEXT NOT NULL,
            SourceId TEXT NOT NULL DEFAULT '',
            PRIMARY KEY (ProfileId, Kind, CapabilityId, SourceId)
        );

        CREATE TABLE AgentProfileModelBindings (
            ProfileId TEXT NOT NULL,
            CapabilityKind TEXT NOT NULL,
            ProviderId TEXT NULL,
            ModelId TEXT NULL,
            SettingsJson TEXT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            PRIMARY KEY (ProfileId, CapabilityKind)
        );

        CREATE TABLE AgentTurns (
            TurnId TEXT PRIMARY KEY,
            SessionId TEXT NOT NULL,
            Role TEXT NOT NULL,
            Kind TEXT NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            ContentRevision INTEGER NOT NULL DEFAULT 1,
            IsStreaming INTEGER NOT NULL DEFAULT 0,
            RunId TEXT NULL,
            RunRevision INTEGER NULL
        );

        CREATE TABLE AgentTurnItems (
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
            PresentationPayloadJson TEXT NULL,
            ToolExecutionId TEXT NULL
        );

        CREATE TABLE AgentRunCheckpoints (
            CheckpointId TEXT PRIMARY KEY,
            SessionId TEXT NOT NULL,
            RunRevision INTEGER NOT NULL,
            Status TEXT NOT NULL,
            Summary TEXT NULL,
            CreatedAtUtc TEXT NOT NULL
        );

        CREATE TABLE AgentSessionContextCheckpoints (
            ContextCheckpointId TEXT PRIMARY KEY,
            SessionId TEXT NOT NULL,
            FirstOmittedTurnId TEXT NULL,
            LastOmittedTurnId TEXT NULL,
            OmittedTurnCount INTEGER NOT NULL,
            SummaryText TEXT NOT NULL,
            DetailsJson TEXT NULL,
            CreatedAtUtc TEXT NOT NULL,
            CheckpointKind TEXT NOT NULL DEFAULT 'Legacy'
                CHECK (CheckpointKind IN ('Legacy', 'Deterministic', 'ModelRefined')),
            TranscriptEpoch INTEGER NOT NULL DEFAULT 0 CHECK (TranscriptEpoch >= 0),
            CoveredThroughCreatedAtUtc TEXT NULL,
            CoveredThroughContentRevision INTEGER NULL,
            SourceRunId TEXT NULL,
            SourceRunRevision INTEGER NULL,
            SourceRunEpoch INTEGER NULL,
            Generation INTEGER NOT NULL DEFAULT 0 CHECK (Generation >= 0),
            PreviousContextCheckpointId TEXT NULL,
            GeneratorVersion TEXT NULL,
            ProviderId TEXT NULL,
            ModelId TEXT NULL
        );

        CREATE TABLE AgentPermissionOverrides (
            ActionId TEXT NOT NULL,
            BoundaryId TEXT NOT NULL,
            Decision TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            PRIMARY KEY (ActionId, BoundaryId)
        );

        CREATE TABLE AgentSessionPermissionStates (
            SessionId TEXT PRIMARY KEY,
            IsUnrestrictedModeEnabled INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE AgentSessionPermissionApprovals (
            ApprovalId TEXT PRIMARY KEY,
            SessionId TEXT NOT NULL,
            ActionId TEXT NOT NULL,
            MatcherKind TEXT NOT NULL,
            Pattern TEXT NOT NULL,
            CreatedAtUtc TEXT NOT NULL
        );

        CREATE TABLE AgentPendingPermissionRequests (
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
            RootSessionId TEXT NULL,
            Status TEXT NOT NULL DEFAULT 'Pending'
                CHECK (Status IN ('Pending', 'Claimed', 'Executed', 'Denied', 'Failed', 'Expired')),
            ClaimToken TEXT NULL,
            ClaimedAtUtc TEXT NULL,
            DecidedAtUtc TEXT NULL,
            DecisionSummary TEXT NULL,
            ExecutionFingerprint TEXT NOT NULL DEFAULT '',
            ContinuationToken TEXT NULL,
            ClaimLeaseExpiresAtUtc TEXT NULL,
            ContinuationConsumedAtUtc TEXT NULL,
            ExecutionStartedAtUtc TEXT NULL,
            ExecutionSnapshotJson TEXT NOT NULL DEFAULT '',
            ToolExecutionId TEXT NULL,
            ResourceClaimSetVersion INTEGER NOT NULL DEFAULT 0,
            ResourceClaimsJson TEXT NOT NULL DEFAULT '[]'
        );

        CREATE TABLE AgentRuns (
            RunId TEXT PRIMARY KEY,
            SessionId TEXT NOT NULL,
            RunRevision INTEGER NOT NULL,
            Epoch INTEGER NOT NULL,
            Status TEXT NOT NULL,
            ProfileId TEXT NOT NULL,
            UserMessage TEXT NOT NULL,
            StartedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            FinishedAtUtc TEXT NULL,
            SuspensionKind TEXT NULL
                CHECK (SuspensionKind IS NULL OR SuspensionKind IN ('Permission', 'ChildJoin')),
            ContinuationToken TEXT NULL,
            SuspensionDataJson TEXT NULL,
            ProviderCycleCount INTEGER NOT NULL DEFAULT 0,
            ToolCallCount INTEGER NOT NULL DEFAULT 0,
            SubmittedContextTokenCount INTEGER NOT NULL DEFAULT 0,
            MemoryConsistencyBarrierEventId TEXT NULL,
            MemoryConsistencyBarrierPayloadHash TEXT NULL,
            UserTurnId TEXT NULL,
            WorkspaceId TEXT NULL,
            AdmissionKind TEXT NULL CHECK (AdmissionKind IS NULL OR AdmissionKind IN ('Normal', 'Rollback')),
            RollbackAnchorTurnId TEXT NULL,
            RequestFingerprint TEXT NULL CHECK (RequestFingerprint IS NULL OR length(RequestFingerprint) = 64),
            ExecutionStartedAtUtc TEXT NULL,
            UNIQUE (SessionId, RunRevision)
        );

        CREATE TABLE AgentParentContinuationWork (
            WorkId TEXT PRIMARY KEY,
            ParentRunId TEXT NOT NULL,
            ParentSessionId TEXT NOT NULL,
            ParentRunRevision INTEGER NOT NULL,
            ChildSessionId TEXT NOT NULL,
            ToolCallId TEXT NOT NULL,
            ChildStatus TEXT NOT NULL,
            Summary TEXT NOT NULL,
            Content TEXT NULL,
            Title TEXT NULL,
            SuspensionDataJson TEXT NULL,
            Status TEXT NOT NULL CHECK (Status IN ('Pending', 'Ready', 'Dispatching', 'Completed', 'Failed')),
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            ExecutionStartedAtUtc TEXT NULL,
            LastError TEXT NULL,
            UNIQUE (ParentRunId, ChildSessionId, ToolCallId)
        );

        CREATE TABLE AgentToolExecutions (
            ExecutionId TEXT PRIMARY KEY,
            SessionId TEXT NOT NULL,
            RunId TEXT NOT NULL,
            RunRevision INTEGER NOT NULL,
            CallId TEXT NOT NULL CHECK (trim(CallId) <> '' AND length(CallId) <= 512),
            ToolId TEXT NOT NULL CHECK (trim(ToolId) <> '' AND length(ToolId) <= 512),
            InvocationFingerprint TEXT NOT NULL CHECK (length(InvocationFingerprint) = 64),
            IsReadOnly INTEGER NOT NULL CHECK (IsReadOnly IN (0, 1)),
            Status TEXT NOT NULL CHECK (Status IN ('Prepared', 'Started', 'Completed', 'Failed', 'Ambiguous')),
            PreparedAtUtc TEXT NOT NULL,
            StartedAtUtc TEXT NULL,
            FinishedAtUtc TEXT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            OutcomeCode TEXT NULL CHECK (OutcomeCode IS NULL OR length(OutcomeCode) <= 128),
            OutcomeSummary TEXT NULL CHECK (OutcomeSummary IS NULL OR length(OutcomeSummary) <= 2048),
            OwnerPackageId TEXT NULL
                CHECK (OwnerPackageId IS NULL OR (trim(OwnerPackageId) <> '' AND length(OwnerPackageId) <= 256)),
            ToolSchemaId TEXT NULL
                CHECK (ToolSchemaId IS NULL OR (trim(ToolSchemaId) <> '' AND length(ToolSchemaId) <= 512)),
            ToolSchemaVersion TEXT NULL
                CHECK (ToolSchemaVersion IS NULL OR (trim(ToolSchemaVersion) <> '' AND length(ToolSchemaVersion) <= 128)),
            ExecutionTargetOwnerPackageId TEXT NULL
                CHECK (ExecutionTargetOwnerPackageId IS NULL OR (trim(ExecutionTargetOwnerPackageId) <> '' AND length(ExecutionTargetOwnerPackageId) <= 256)),
            UNIQUE (RunId, RunRevision, CallId)
        );

        CREATE TABLE AgentLifecycleOutbox (
            Sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            EventId TEXT NOT NULL UNIQUE CHECK (trim(EventId) <> '' AND length(EventId) <= 80),
            SourceKey TEXT NOT NULL UNIQUE CHECK (trim(SourceKey) <> '' AND length(SourceKey) <= 1024),
            EventType TEXT NOT NULL CHECK (EventType IN (
                'UserTurnAdded', 'AssistantTurnCompleted', 'ToolResultRecorded',
                'RunInterrupted', 'RunStopped', 'RunFailed', 'TranscriptRolledBack',
                'SessionDeleted', 'WorkspaceDeleted')),
            OrderingKey TEXT NOT NULL CHECK (trim(OrderingKey) <> '' AND length(OrderingKey) <= 1024),
            WorkspaceId TEXT NULL CHECK (WorkspaceId IS NULL OR length(WorkspaceId) <= 512),
            SessionId TEXT NULL,
            PayloadJson TEXT NOT NULL CHECK (length(PayloadJson) <= 262144),
            PayloadHash TEXT NOT NULL CHECK (length(PayloadHash) = 64),
            CreatedAtUtc TEXT NOT NULL,
            PayloadState TEXT NOT NULL DEFAULT 'Available' CHECK (PayloadState IN ('Available', 'Erased')),
            OriginalPayloadHash TEXT NULL CHECK (OriginalPayloadHash IS NULL OR length(OriginalPayloadHash) = 64),
            PayloadErasedAtUtc TEXT NULL,
            PayloadErasureGeneration INTEGER NULL CHECK (
                PayloadErasureGeneration IS NULL OR PayloadErasureGeneration >= 1),
            CHECK (
                (PayloadState = 'Available'
                 AND OriginalPayloadHash IS NULL
                 AND PayloadErasedAtUtc IS NULL
                 AND PayloadErasureGeneration IS NULL
                 AND PayloadJson <> '{"contentErased":true}')
                OR
                (PayloadState = 'Erased'
                 AND OriginalPayloadHash IS NOT NULL
                 AND PayloadErasedAtUtc IS NOT NULL
                 AND PayloadErasureGeneration IS NOT NULL
                 AND PayloadJson = '{"contentErased":true}'
                 AND PayloadHash = '2cab2434dff48e3639c975cd3ac9693994e3e1ec44a4921628100a047add6950')
            )
        );

        CREATE TABLE AgentLifecycleSubscriptions (
            SubscriptionId TEXT PRIMARY KEY CHECK (trim(SubscriptionId) <> '' AND length(SubscriptionId) <= 80),
            PackageId TEXT NOT NULL CHECK (trim(PackageId) <> '' AND length(PackageId) <= 256),
            ObserverId TEXT NOT NULL CHECK (trim(ObserverId) <> '' AND length(ObserverId) <= 512),
            ContractKind TEXT NOT NULL CHECK (ContractKind IN ('Durable', 'Compatibility')),
            DisplayName TEXT NOT NULL CHECK (length(DisplayName) <= 512),
            CreatedAtUtc TEXT NOT NULL,
            LastSeenAtUtc TEXT NOT NULL,
            ReplayStartSequence INTEGER NOT NULL DEFAULT 1 CHECK (ReplayStartSequence >= 1),
            ReconciledThroughSequence INTEGER NOT NULL DEFAULT 0 CHECK (ReconciledThroughSequence >= 0),
            MembershipGeneration INTEGER NOT NULL DEFAULT 1 CHECK (MembershipGeneration >= 1),
            RetiredAtUtc TEXT NULL,
            UNIQUE (PackageId, ObserverId, ContractKind)
        );

        CREATE TABLE AgentLifecycleDeliveries (
            SubscriptionId TEXT NOT NULL,
            EventSequence INTEGER NOT NULL,
            Status TEXT NOT NULL CHECK (Status IN ('Pending', 'InFlight', 'Delivered', 'Poison')),
            AttemptCount INTEGER NOT NULL DEFAULT 0 CHECK (AttemptCount >= 0),
            NextAttemptAtUtc TEXT NOT NULL,
            LeaseToken TEXT NULL,
            LeaseExpiresAtUtc TEXT NULL,
            LastAttemptAtUtc TEXT NULL,
            DeliveredAtUtc TEXT NULL,
            PoisonedAtUtc TEXT NULL,
            LastError TEXT NULL CHECK (LastError IS NULL OR length(LastError) <= 2048),
            MembershipGeneration INTEGER NOT NULL DEFAULT 1 CHECK (MembershipGeneration >= 1),
            DeliveryVersion INTEGER NOT NULL DEFAULT 1 CHECK (DeliveryVersion >= 1),
            PRIMARY KEY (SubscriptionId, EventSequence)
        );

        CREATE TABLE AgentRuntimeGenerations (
            Epoch TEXT PRIMARY KEY,
            RuntimeSessionGeneration INTEGER NOT NULL CHECK (RuntimeSessionGeneration >= 1),
            ProcessId INTEGER NOT NULL CHECK (ProcessId > 0),
            ProcessStartedAtUtc TEXT NOT NULL,
            Status TEXT NOT NULL CHECK (Status IN ('Committed', 'Stopped', 'Dead', 'Expired')),
            CreatedAtUtc TEXT NOT NULL,
            CommittedAtUtc TEXT NOT NULL,
            LastHeartbeatAtUtc TEXT NOT NULL,
            LeaseExpiresAtUtc TEXT NOT NULL,
            StoppedAtUtc TEXT NULL
        );

        CREATE TABLE AgentRuntimeGenerationState (
            SingletonId INTEGER PRIMARY KEY CHECK (SingletonId = 1),
            CurrentEpoch TEXT NULL
        );

        CREATE TABLE AgentLifecycleGenerationState (
            SingletonId INTEGER PRIMARY KEY CHECK (SingletonId = 1),
            CurrentGeneration INTEGER NOT NULL CHECK (CurrentGeneration >= 1)
        );

        CREATE TABLE AgentSessionDataCleaners (
            PackageId TEXT NOT NULL CHECK (trim(PackageId) <> '' AND length(PackageId) <= 256),
            CleanerId TEXT NOT NULL CHECK (trim(CleanerId) <> '' AND length(CleanerId) <= 512),
            LastSeenAtUtc TEXT NOT NULL,
            PRIMARY KEY (PackageId, CleanerId)
        );

        CREATE TABLE AgentSessionCleanupJobs (
            JobId INTEGER PRIMARY KEY AUTOINCREMENT,
            PackageId TEXT NOT NULL CHECK (trim(PackageId) <> '' AND length(PackageId) <= 256),
            CleanerId TEXT NOT NULL CHECK (trim(CleanerId) <> '' AND length(CleanerId) <= 512),
            SessionId TEXT NOT NULL CHECK (trim(SessionId) <> ''),
            Status TEXT NOT NULL CHECK (Status IN ('Pending', 'InFlight', 'Completed')),
            AttemptCount INTEGER NOT NULL DEFAULT 0 CHECK (AttemptCount >= 0),
            NextAttemptAtUtc TEXT NOT NULL,
            LeaseToken TEXT NULL,
            LeaseExpiresAtUtc TEXT NULL,
            LastAttemptAtUtc TEXT NULL,
            CompletedAtUtc TEXT NULL,
            LastError TEXT NULL CHECK (LastError IS NULL OR length(LastError) <= 1024),
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            UNIQUE (PackageId, CleanerId, SessionId)
        );

        CREATE TABLE AgentLifecycleMaintenanceState (
            SingletonId INTEGER PRIMARY KEY CHECK (SingletonId = 1),
            AllowCompaction INTEGER NOT NULL DEFAULT 0 CHECK (AllowCompaction IN (0, 1))
        );

        INSERT INTO AgentRuntimeGenerationState (SingletonId, CurrentEpoch) VALUES (1, NULL);
        INSERT INTO AgentLifecycleGenerationState (SingletonId, CurrentGeneration) VALUES (1, 2);
        INSERT INTO AgentLifecycleMaintenanceState (SingletonId, AllowCompaction) VALUES (1, 0);

        CREATE INDEX IX_AgentWorkspaceBindings_WorkspaceId_Role
            ON AgentWorkspaceBindings (WorkspaceId, Role);
        CREATE INDEX IX_AgentWorkspacePaths_WorkspaceId_SortOrder
            ON AgentWorkspacePaths (WorkspaceId, SortOrder);
        CREATE INDEX IX_AgentWorkspaceDocuments_WorkspaceId_SortOrder
            ON AgentWorkspaceDocuments (WorkspaceId, SortOrder);
        CREATE INDEX IX_AgentSessions_WorkspaceId_UpdatedAtUtc
            ON AgentSessions (WorkspaceId, UpdatedAtUtc);
        CREATE INDEX IX_AgentSessions_RootSessionId
            ON AgentSessions (RootSessionId);
        CREATE INDEX IX_AgentSessions_ParentSessionId_UpdatedAtUtc
            ON AgentSessions (ParentSessionId, UpdatedAtUtc);
        CREATE INDEX IX_AgentSessions_ProfileId
            ON AgentSessions (ProfileId);
        CREATE INDEX IX_AgentTurns_SessionId_CreatedAtUtc
            ON AgentTurns (SessionId, CreatedAtUtc);
        CREATE INDEX IX_AgentTurns_StreamingRun
            ON AgentTurns (SessionId, RunId, RunRevision)
            WHERE IsStreaming = 1;
        CREATE INDEX IX_AgentTurns_RunIdentity
            ON AgentTurns (SessionId, RunId, RunRevision)
            WHERE RunId IS NOT NULL AND RunRevision IS NOT NULL;
        CREATE INDEX IX_AgentTurnItems_TurnId_SequenceNumber
            ON AgentTurnItems (TurnId, SequenceNumber);
        CREATE INDEX IX_AgentTurnItems_ToolExecutionId
            ON AgentTurnItems (ToolExecutionId);
        CREATE INDEX IX_AgentTurnItems_CallId
            ON AgentTurnItems (CallId)
            WHERE CallId IS NOT NULL AND Kind IN ('ToolCall', 'ToolResult');
        CREATE UNIQUE INDEX UX_AgentTurnItems_ToolExecutionCall
            ON AgentTurnItems (ToolExecutionId)
            WHERE ToolExecutionId IS NOT NULL AND Kind = 'ToolCall';
        CREATE UNIQUE INDEX UX_AgentTurnItems_ToolExecutionResult
            ON AgentTurnItems (ToolExecutionId)
            WHERE ToolExecutionId IS NOT NULL AND Kind = 'ToolResult';
        CREATE INDEX IX_AgentRunCheckpoints_SessionId_CreatedAtUtc
            ON AgentRunCheckpoints (SessionId, CreatedAtUtc);
        CREATE INDEX IX_AgentRunCheckpoints_SessionId_RunRevision
            ON AgentRunCheckpoints (SessionId, RunRevision);
        CREATE INDEX IX_AgentSessionContextCheckpoints_SessionId_CreatedAtUtc
            ON AgentSessionContextCheckpoints (SessionId, CreatedAtUtc);
        CREATE INDEX IX_AgentSessionContextCheckpoints_SessionGeneration
            ON AgentSessionContextCheckpoints (SessionId, Generation DESC);
        CREATE INDEX IX_AgentSessionContextCheckpoints_SessionAnchor
            ON AgentSessionContextCheckpoints (
                SessionId, TranscriptEpoch, CoveredThroughCreatedAtUtc, LastOmittedTurnId);
        CREATE INDEX IX_AgentSessionPermissionApprovals_SessionId
            ON AgentSessionPermissionApprovals (SessionId);
        CREATE INDEX IX_AgentPendingPermissionRequests_SessionId
            ON AgentPendingPermissionRequests (SessionId);
        CREATE INDEX IX_AgentPendingPermissionRequests_RootSessionId
            ON AgentPendingPermissionRequests (RootSessionId);
        CREATE INDEX IX_AgentPendingPermissionRequests_ActiveStatus
            ON AgentPendingPermissionRequests (SessionId, Status, CreatedAtUtc DESC);
        CREATE UNIQUE INDEX UX_AgentPendingPermissionRequests_ActiveRunCall
            ON AgentPendingPermissionRequests (SessionId, RunId, RunRevision, CallId)
            WHERE Status IN ('Pending', 'Claimed')
              AND ExecutionFingerprint <> ''
              AND CallId <> '';
        CREATE UNIQUE INDEX UX_AgentPendingPermissionRequests_ActiveContinuationToken
            ON AgentPendingPermissionRequests (ContinuationToken)
            WHERE ContinuationToken IS NOT NULL
              AND Status IN ('Pending', 'Claimed');
        CREATE INDEX IX_AgentPendingPermissionRequests_ClaimRecovery
            ON AgentPendingPermissionRequests (Status, ClaimLeaseExpiresAtUtc)
            WHERE Status = 'Claimed';
        CREATE UNIQUE INDEX UX_AgentPendingPermissionRequests_ToolExecutionId
            ON AgentPendingPermissionRequests (ToolExecutionId)
            WHERE ToolExecutionId IS NOT NULL;
        CREATE INDEX IX_AgentRuns_SessionId_CurrentRevision
            ON AgentRuns (SessionId, RunRevision DESC)
            WHERE FinishedAtUtc IS NULL;
        CREATE UNIQUE INDEX UX_AgentRuns_ActiveContinuationToken
            ON AgentRuns (ContinuationToken)
            WHERE ContinuationToken IS NOT NULL
              AND FinishedAtUtc IS NULL;
        CREATE UNIQUE INDEX UX_AgentRuns_UserTurnId
            ON AgentRuns (UserTurnId)
            WHERE UserTurnId IS NOT NULL;
        CREATE INDEX IX_AgentRuns_PreparingDispatch
            ON AgentRuns (Status, StartedAtUtc, SessionId, RunRevision)
            WHERE Status = 'Preparing'
              AND FinishedAtUtc IS NULL
              AND UserTurnId IS NOT NULL;
        CREATE INDEX IX_AgentParentContinuationWork_Dispatch
            ON AgentParentContinuationWork (Status, UpdatedAtUtc)
            WHERE Status IN ('Pending', 'Ready', 'Dispatching');
        CREATE INDEX IX_AgentToolExecutions_SessionRun
            ON AgentToolExecutions (SessionId, RunRevision, PreparedAtUtc);
        CREATE INDEX IX_AgentToolExecutions_OpenRun
            ON AgentToolExecutions (RunId, RunRevision, Status)
            WHERE Status IN ('Prepared', 'Started');
        CREATE INDEX IX_AgentLifecycleOutbox_Ordering
            ON AgentLifecycleOutbox (OrderingKey, Sequence);
        CREATE INDEX IX_AgentLifecycleOutbox_Replay
            ON AgentLifecycleOutbox (PayloadState, Sequence);
        CREATE INDEX IX_AgentLifecycleOutbox_ErasureGeneration
            ON AgentLifecycleOutbox (PayloadErasureGeneration, Sequence)
            WHERE PayloadState = 'Erased';
        CREATE INDEX IX_AgentLifecycleDeliveries_Dispatch
            ON AgentLifecycleDeliveries (SubscriptionId, Status, EventSequence, NextAttemptAtUtc);
        CREATE INDEX IX_AgentLifecycleDeliveries_LeaseRecovery
            ON AgentLifecycleDeliveries (Status, LeaseExpiresAtUtc)
            WHERE Status = 'InFlight';
        CREATE INDEX IX_AgentSessionCleanupJobs_Dispatch
            ON AgentSessionCleanupJobs (PackageId, CleanerId, Status, NextAttemptAtUtc, JobId);
        CREATE INDEX IX_AgentSessionCleanupJobs_Recovery
            ON AgentSessionCleanupJobs (Status, LeaseExpiresAtUtc)
            WHERE Status = 'InFlight';

        CREATE TRIGGER TR_AgentLifecycleOutbox_CanonicalInsert
        BEFORE INSERT ON AgentLifecycleOutbox
        WHEN NEW.PayloadState <> 'Available'
             OR NEW.OriginalPayloadHash IS NOT NULL
             OR NEW.PayloadErasedAtUtc IS NOT NULL
             OR NEW.PayloadErasureGeneration IS NOT NULL
             OR NEW.PayloadJson = '{"contentErased":true}'
        BEGIN
            SELECT RAISE(ABORT, 'Agent lifecycle outbox events must be inserted with available payloads');
        END;

        CREATE TRIGGER TR_AgentLifecycleOutbox_ImmutableUpdate
        BEFORE UPDATE ON AgentLifecycleOutbox
        WHEN NOT (
            OLD.PayloadState = 'Available'
            AND OLD.OriginalPayloadHash IS NULL
            AND OLD.PayloadErasedAtUtc IS NULL
            AND OLD.PayloadErasureGeneration IS NULL
            AND NEW.PayloadState = 'Erased'
            AND NEW.OriginalPayloadHash = OLD.PayloadHash
            AND NEW.PayloadErasedAtUtc IS NOT NULL
            AND NEW.PayloadErasureGeneration = (
                SELECT CurrentGeneration FROM AgentLifecycleGenerationState WHERE SingletonId = 1)
            AND NEW.PayloadJson = '{"contentErased":true}'
            AND NEW.PayloadHash = '2cab2434dff48e3639c975cd3ac9693994e3e1ec44a4921628100a047add6950'
            AND NEW.Sequence IS OLD.Sequence
            AND NEW.EventId IS OLD.EventId
            AND NEW.SourceKey IS OLD.SourceKey
            AND NEW.EventType IS OLD.EventType
            AND NEW.OrderingKey IS OLD.OrderingKey
            AND NEW.WorkspaceId IS OLD.WorkspaceId
            AND NEW.SessionId IS OLD.SessionId
            AND NEW.CreatedAtUtc IS OLD.CreatedAtUtc
        )
        BEGIN
            SELECT RAISE(ABORT, 'Agent lifecycle outbox events are immutable except for canonical content erasure');
        END;

        CREATE TRIGGER TR_AgentLifecycleOutbox_ImmutableDelete
        BEFORE DELETE ON AgentLifecycleOutbox
        WHEN (SELECT AllowCompaction FROM AgentLifecycleMaintenanceState WHERE SingletonId = 1) IS NOT 1
        BEGIN
            SELECT RAISE(ABORT, 'Agent lifecycle outbox events may only be deleted by bounded compaction');
        END;
        """;

    internal const string BaselineChecksum = "4dc0cc925c4f25e7ba9cff537d434a733c017953042d97c8ab55ee9624ab5028";

    internal const int ApprovalIntentVersion = 2;
    internal const string ApprovalIntentName = "durable-permission-approval-intent";
    private const string ApprovalIntentSql = "ALTER TABLE AgentPendingPermissionRequests ADD COLUMN ApproveForSession INTEGER NOT NULL DEFAULT 0 CHECK (ApproveForSession IN (0, 1));";
    internal static readonly string ApprovalIntentChecksum = Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ApprovalIntentSql)));

    private void ApplySchemaMigrations()
    {
        using var connection = CreateConnection();
        connection.Open();
        if (IsEmptyDatabase(connection))
        {
            InitializeEmptyDatabase(connection);
        }
        else
        {
            ValidateCurrentLedger(connection);
        }

        ApplyApprovalIntentMigration(connection);
        EnableWriteAheadLogging(connection);
        EnableSecureDelete(connection);
    }

    private static void InitializeEmptyDatabase(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!IsEmptyDatabase(connection, transaction))
        {
            ValidateCurrentLedger(connection, transaction);
            transaction.Commit();
            return;
        }

        using (var schema = connection.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText = BaselineSql;
            schema.ExecuteNonQuery();
        }

        using (var ledger = connection.CreateCommand())
        {
            ledger.Transaction = transaction;
            ledger.CommandText = """
                INSERT INTO SchemaMigrations (Version, Name, Checksum, AppliedAtUtc)
                VALUES ($version, $name, $checksum, $appliedAtUtc);
                """;
            ledger.Parameters.AddWithValue("$version", BaselineVersion);
            ledger.Parameters.AddWithValue("$name", BaselineName);
            ledger.Parameters.AddWithValue("$checksum", BaselineChecksum);
            ledger.Parameters.AddWithValue("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
            ledger.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static bool IsEmptyDatabase(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' LIMIT 1;";
        return command.ExecuteScalar() is null;
    }

    private static void ValidateCurrentLedger(
        SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        try
        {
            using var columns = connection.CreateCommand();
            columns.Transaction = transaction;
            columns.CommandText = "PRAGMA table_info(SchemaMigrations);";
            using var columnReader = columns.ExecuteReader();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (columnReader.Read()) names.Add(columnReader.GetString(1));
            if (!names.SetEquals(["Version", "Name", "Checksum", "AppliedAtUtc"]))
            {
                throw InvalidLedger("the ledger table does not have the exact current columns");
            }

            using var ledger = connection.CreateCommand();
            ledger.Transaction = transaction;
            ledger.CommandText = "SELECT Version, Name, Checksum FROM SchemaMigrations ORDER BY Version;";
            using var reader = ledger.ExecuteReader();
            if (!reader.Read()) throw InvalidLedger("the current baseline entry is missing");
            var version = reader.GetInt32(0);
            var name = reader.GetString(1);
            var checksum = reader.GetString(2);
            if (version != BaselineVersion
                || !string.Equals(name, BaselineName, StringComparison.Ordinal)
                || !string.Equals(checksum, BaselineChecksum, StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidLedger("the baseline identity or checksum does not match this build");
            }
            if (reader.Read()
                && (reader.GetInt32(0) != ApprovalIntentVersion
                    || reader.GetString(1) != ApprovalIntentName
                    || reader.GetString(2) != ApprovalIntentChecksum
                    || reader.Read()))
            {
                throw InvalidLedger("the approval-intent migration identity or checksum does not match this build");
            }
        }
        catch (InvalidOperationException exception) when (!exception.Message.StartsWith(
                   "Agent schema migration ledger validation failed:",
                   StringComparison.Ordinal))
        {
            throw InvalidLedger("the current ledger could not be read", exception);
        }
        catch (SqliteException exception)
        {
            throw InvalidLedger("the current ledger could not be read", exception);
        }
    }

    private static InvalidOperationException InvalidLedger(string reason, Exception? innerException = null)
        => new(
            $"Agent schema migration ledger validation failed: {reason}. "
            + "Only an absent or empty database, or the reviewed baseline with known additive migrations, is supported; "
            + "do not edit or delete migration ledger rows.",
            innerException);

    private static void ApplyApprovalIntentMigration(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        ValidateCurrentLedger(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM SchemaMigrations WHERE Version = $version;";
        command.Parameters.AddWithValue("$version", ApprovalIntentVersion);
        if (command.ExecuteScalar() is null)
        {
            command.CommandText = ApprovalIntentSql;
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO SchemaMigrations (Version, Name, Checksum, AppliedAtUtc) VALUES ($version, $name, $checksum, $now);";
            command.Parameters.AddWithValue("$name", ApprovalIntentName);
            command.Parameters.AddWithValue("$checksum", ApprovalIntentChecksum);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
