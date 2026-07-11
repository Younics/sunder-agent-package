using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentPermissionStoreTests
{
    [Fact]
    public void Migration_ExpiresLegacyRequestsWithoutDurableContinuationState()
    {
        using var scope = RegressionTestPackageScope.Create();
        var databasePath = Path.Combine(scope.Context.Storage.DataRootPath, "agent.db");
        using (var connection = OpenDatabase(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE SchemaMigrations (
                    Version INTEGER PRIMARY KEY,
                    Name TEXT NOT NULL,
                    AppliedAtUtc TEXT NOT NULL
                );
                INSERT INTO SchemaMigrations VALUES (1, 'legacy-schema-baseline', '2026-01-01T00:00:00Z');
                INSERT INTO SchemaMigrations VALUES (2, 'agent-runs', '2026-01-01T00:00:00Z');

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
                    UNIQUE (SessionId, RunRevision)
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
                    RootSessionId TEXT NULL
                );
                INSERT INTO AgentPendingPermissionRequests (
                    RequestId, SessionId, ActionId, Summary, CreatedAtUtc)
                VALUES ('legacy-request', '11111111-1111-1111-1111-111111111111', 'legacy.action', 'Legacy request', '2026-01-01T00:00:00Z');
                """;
            command.ExecuteNonQuery();
        }

        var store = new AgentLocalStore(scope.Context);

        using var verificationConnection = OpenDatabase(store.DatabasePath);
        using var verificationCommand = verificationConnection.CreateCommand();
        verificationCommand.CommandText = """
            SELECT Status, ClaimToken, ClaimedAtUtc, DecidedAtUtc, DecisionSummary, ExecutionFingerprint
            FROM AgentPendingPermissionRequests
            WHERE RequestId = 'legacy-request';
            """;
        using var reader = verificationCommand.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Expired", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.True(reader.IsDBNull(2));
        Assert.False(reader.IsDBNull(3));
        Assert.Contains("Legacy permission request expired", reader.GetString(4), StringComparison.Ordinal);
        Assert.Equal(string.Empty, reader.GetString(5));
    }

    [Fact]
    public async Task TryClaimPendingPermissionRequest_ConcurrentClaimsHaveOneWinner()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Permission claim tests");
        var session = store.CreateSession("Session", workspaceId: workspace.WorkspaceId);
        var run = store.ReserveRun(session.SessionId, "profile", "message");
        store.SaveCheckpoint(session.SessionId, run.Key.RunRevision, AgentRunStatus.Running, "Running.");
        var request = CreateRequest(session.SessionId, run.Key.RunId, run.Key.RunRevision);
        Assert.NotNull(store.SavePendingPermissionRequestAndSuspendRun(request));
        using var barrier = new Barrier(3);

        Task<AgentPendingPermissionClaimResult> ClaimAsync() => Task.Run(() =>
        {
            barrier.SignalAndWait();
            return store.TryClaimPendingPermissionRequest(session.SessionId, "request-1");
        });

        var first = ClaimAsync();
        var second = ClaimAsync();
        barrier.SignalAndWait();
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.Outcome == AgentPendingPermissionClaimOutcome.Claimed);
        Assert.Single(results, result => result.Outcome == AgentPendingPermissionClaimOutcome.AlreadyClaimed);
        Assert.Equal(AgentPendingPermissionStatus.Claimed, store.GetPermissionRequest(session.SessionId, "request-1")?.Status);
    }

    [Fact]
    public void StartupRecovery_PreservesUnexpiredUnconsumedClaim()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Claim recovery");
        var session = store.CreateSession("Session", workspaceId: workspace.WorkspaceId);
        var run = store.ReserveRun(session.SessionId, "profile", "message");
        var running = Assert.IsType<AgentRunTransitionResult>(store.TryTransitionRun(
            run.Key,
            run.Epoch,
            AgentRunStatus.Running,
            "Running."));
        Assert.NotNull(store.SavePendingPermissionRequestAndSuspendRun(
            CreateRequest(session.SessionId, run.Key.RunId, run.Key.RunRevision),
            running.Run.Epoch));
        Assert.True(store.TryClaimPendingPermissionRequest(session.SessionId, "request-1").IsClaimed);

        var recovered = new AgentLocalStore(scope.Context);

        var request = recovered.GetPermissionRequest(session.SessionId, "request-1");
        Assert.Equal(AgentPendingPermissionStatus.Claimed, request?.Status);
        Assert.Null(request?.ContinuationConsumedAtUtc);
        Assert.Null(request?.ExecutionStartedAtUtc);
        Assert.Equal(AgentDurableRunStatus.WaitingForApproval, recovered.GetRun(run.Key.RunId)?.Status);
    }

    [Fact]
    public void TryClaimPendingPermissionRequest_ReclaimsOnlyExpiredUnconsumedClaim()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Expired claim recovery");
        var session = store.CreateSession("Session", workspaceId: workspace.WorkspaceId);
        var run = store.ReserveRun(session.SessionId, "profile", "message");
        var running = Assert.IsType<AgentRunTransitionResult>(store.TryTransitionRun(
            run.Key,
            run.Epoch,
            AgentRunStatus.Running,
            "Running."));
        Assert.NotNull(store.SavePendingPermissionRequestAndSuspendRun(
            CreateRequest(session.SessionId, run.Key.RunId, run.Key.RunRevision),
            running.Run.Epoch));
        var firstClaim = store.TryClaimPendingPermissionRequest(session.SessionId, "request-1");
        Assert.True(firstClaim.IsClaimed);

        using (var connection = OpenDatabase(store.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE AgentPendingPermissionRequests SET ClaimLeaseExpiresAtUtc = $expired WHERE RequestId = 'request-1';";
            command.Parameters.AddWithValue("$expired", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));
            command.ExecuteNonQuery();
        }

        var reclaimed = store.TryClaimPendingPermissionRequest(session.SessionId, "request-1");

        Assert.True(reclaimed.IsClaimed);
        Assert.NotEqual(firstClaim.Request?.ClaimToken, reclaimed.Request?.ClaimToken);
        Assert.Null(reclaimed.Request?.ContinuationConsumedAtUtc);
        Assert.Null(reclaimed.Request?.ExecutionStartedAtUtc);
    }

    [Fact]
    public void StartupRecovery_FailsConsumedClaimAsAmbiguousWithoutRetry()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Ambiguous recovery");
        var session = store.CreateSession("Session", workspaceId: workspace.WorkspaceId);
        var run = store.ReserveRun(session.SessionId, "profile", "message");
        var running = Assert.IsType<AgentRunTransitionResult>(store.TryTransitionRun(
            run.Key,
            run.Epoch,
            AgentRunStatus.Running,
            "Running."));
        Assert.NotNull(store.SavePendingPermissionRequestAndSuspendRun(
            CreateRequest(session.SessionId, run.Key.RunId, run.Key.RunRevision),
            running.Run.Epoch));
        var claim = store.TryClaimPendingPermissionRequest(session.SessionId, "request-1");
        var waitingRun = Assert.IsType<AgentDurableRunRecord>(store.GetRun(run.Key.RunId));
        Assert.NotNull(store.ResumeClaimedPermissionRequest(claim.Request!, waitingRun.Epoch));
        Assert.True(store.MarkClaimedPermissionExecutionStarted(
            session.SessionId,
            "request-1",
            claim.Request!.ClaimToken!));
        using (var connection = OpenDatabase(store.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE AgentPendingPermissionRequests SET ClaimLeaseExpiresAtUtc = $expired WHERE RequestId = 'request-1';";
            command.Parameters.AddWithValue("$expired", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));
            command.ExecuteNonQuery();
        }

        Assert.Equal(
            AgentPendingPermissionClaimOutcome.AlreadyClaimed,
            store.TryClaimPendingPermissionRequest(session.SessionId, "request-1").Outcome);

        var recovered = new AgentLocalStore(scope.Context);

        var request = recovered.GetPermissionRequest(session.SessionId, "request-1");
        Assert.Equal(AgentPendingPermissionStatus.Failed, request?.Status);
        Assert.Contains("ambiguous", request?.DecisionSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("will not be retried", request?.DecisionSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AgentDurableRunStatus.Failed, recovered.GetRun(run.Key.RunId)?.Status);
    }

    [Fact]
    public void StartupMigration_ExpiresMissingFingerprintAndTerminalizesWaitingRun()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Legacy migration");
        var session = store.CreateSession("Session", workspaceId: workspace.WorkspaceId);
        var run = store.ReserveRun(session.SessionId, "profile", "message");
        var running = Assert.IsType<AgentRunTransitionResult>(store.TryTransitionRun(
            run.Key,
            run.Epoch,
            AgentRunStatus.Running,
            "Running."));
        Assert.NotNull(store.SavePendingPermissionRequestAndSuspendRun(
            CreateRequest(session.SessionId, run.Key.RunId, run.Key.RunRevision),
            running.Run.Epoch));
        using (var connection = OpenDatabase(store.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE AgentPendingPermissionRequests SET ExecutionFingerprint = '' WHERE RequestId = 'request-1';";
            command.ExecuteNonQuery();
        }

        var migrated = new AgentLocalStore(scope.Context);

        Assert.Equal(
            AgentPendingPermissionStatus.Expired,
            migrated.GetPermissionRequest(session.SessionId, "request-1")?.Status);
        Assert.Equal(AgentDurableRunStatus.Interrupted, migrated.GetRun(run.Key.RunId)?.Status);
        Assert.Equal(AgentRunStatus.Interrupted, migrated.GetLatestCheckpoint(session.SessionId)?.Status);
    }

    private static AgentPendingPermissionRequestRecord CreateRequest(
        Guid sessionId,
        Guid runId,
        long runRevision)
        => new(
            "request-1",
            sessionId,
            runId,
            runRevision,
            "profile",
            Guid.NewGuid(),
            "message",
            "call-1",
            "test.action",
            "test.boundary",
            "Approve test action",
            "test-tool",
            "{}",
            null,
            null,
            "workspace",
            null,
            null,
            null,
            true,
            DateTimeOffset.UtcNow,
            ExecutionFingerprint: new string('a', 64));

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
}
