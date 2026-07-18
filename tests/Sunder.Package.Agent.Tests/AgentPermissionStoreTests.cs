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
        var databasePath = scope.Context.Storage.RoleLocalWorkspace.GetLocalPath("agent/agent.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using (var connection = OpenDatabase(databasePath))
        using (var command = connection.CreateCommand())
        {
            AgentLocalStore.ApplySchemaMigrations(connection, 2);
            command.CommandText = """
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
    public async Task ClaimAndDenyRace_CommitsExactlyOnePermissionTransition()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Permission decision race");
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
        using var barrier = new Barrier(3);

        var claim = Task.Run(() =>
        {
            barrier.SignalAndWait();
            return store.TryClaimPendingPermissionRequest(session.SessionId, "request-1");
        });
        var deny = Task.Run(() =>
        {
            barrier.SignalAndWait();
            return store.TryDenyPendingPermissionRequest(session.SessionId, "request-1", "Denied.");
        });
        barrier.SignalAndWait();
        await Task.WhenAll(claim, deny);
        var claimResult = await claim;
        var denyResult = await deny;

        var persisted = Assert.IsType<AgentPendingPermissionRequestRecord>(
            store.GetPermissionRequest(session.SessionId, "request-1"));
        if (claimResult.Outcome == AgentPendingPermissionClaimOutcome.Claimed)
        {
            Assert.Equal(AgentPendingPermissionDecisionOutcome.AlreadyClaimed, denyResult.Outcome);
            Assert.Equal(AgentPendingPermissionStatus.Claimed, persisted.Status);
            Assert.Equal(AgentDurableRunStatus.WaitingForApproval, store.GetRun(run.Key.RunId)?.Status);
        }
        else
        {
            Assert.Equal(AgentPendingPermissionClaimOutcome.AlreadyDecided, claimResult.Outcome);
            Assert.Equal(AgentPendingPermissionDecisionOutcome.Decided, denyResult.Outcome);
            Assert.Equal(AgentPendingPermissionStatus.Denied, persisted.Status);
            Assert.Equal(AgentDurableRunStatus.Stopped, store.GetRun(run.Key.RunId)?.Status);
        }
    }

    [Theory]
    [InlineData("deny")]
    [InlineData("claimed-finalize")]
    [InlineData("expire")]
    public void PermissionTerminalPaths_ReturnOrderedCompletedStreamingTurns(string terminalPath)
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Permission completion");
        var session = store.CreateSession("Session", workspaceId: workspace.WorkspaceId);
        var run = store.ReserveRun(session.SessionId, "profile", "message");
        var running = Assert.IsType<AgentRunTransitionResult>(store.TryTransitionRun(
            run.Key,
            run.Epoch,
            AgentRunStatus.Running,
            "Running."));
        var first = Assert.IsType<AgentTurnRecord>(store.TryAppendTextTurn(
            run.Key,
            running.Run.Epoch,
            AgentMessageRole.Assistant,
            "first partial"));
        var second = Assert.IsType<AgentTurnRecord>(store.TryAppendTextTurn(
            run.Key,
            running.Run.Epoch,
            AgentMessageRole.Assistant,
            "second partial"));
        using (var connection = OpenDatabase(store.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE AgentTurns
                SET CreatedAtUtc = CASE TurnId
                    WHEN $firstTurnId THEN $firstCreatedAtUtc
                    ELSE $secondCreatedAtUtc
                END
                WHERE TurnId IN ($firstTurnId, $secondTurnId);
                """;
            command.Parameters.AddWithValue("$firstTurnId", first.TurnId.ToString());
            command.Parameters.AddWithValue("$secondTurnId", second.TurnId.ToString());
            command.Parameters.AddWithValue("$firstCreatedAtUtc", "2026-01-01T00:00:00.0000000+00:00");
            command.Parameters.AddWithValue("$secondCreatedAtUtc", "2026-01-01T00:00:01.0000000+00:00");
            command.ExecuteNonQuery();
        }
        var request = Assert.IsType<AgentPendingPermissionRequestRecord>(
            store.SavePendingPermissionRequestAndSuspendRun(
                CreateRequest(session.SessionId, run.Key.RunId, run.Key.RunRevision),
                running.Run.Epoch));

        AgentCheckpointPersistenceResult finalization;
        switch (terminalPath)
        {
            case "deny":
                var decision = store.TryDenyPendingPermissionRequest(
                    session.SessionId,
                    request.RequestId,
                    "Denied.");
                finalization = Assert.IsType<AgentCheckpointPersistenceResult>(decision.Finalization);
                var toolResultTurn = Assert.IsType<AgentTurnRecord>(decision.ToolResultTurn);
                Assert.Equal(AgentTurnKind.ToolResult, toolResultTurn.Kind);
                Assert.Equal("permission-denied", Assert.Single(toolResultTurn.Items).ErrorCode);
                Assert.NotNull(store.GetTurn(toolResultTurn.TurnId));
                var repeatedDecision = store.TryDenyPendingPermissionRequest(
                    session.SessionId,
                    request.RequestId,
                    "Denied again.");
                Assert.Equal(
                    AgentPendingPermissionDecisionOutcome.AlreadyDecided,
                    repeatedDecision.Outcome);
                Assert.Null(repeatedDecision.ToolResultTurn);
                Assert.Single(
                    store.ListTurns(session.SessionId),
                    turn => turn.Kind == AgentTurnKind.ToolResult);
                break;
            case "claimed-finalize":
                var claim = store.TryClaimPendingPermissionRequest(
                    session.SessionId,
                    request.RequestId);
                finalization = Assert.IsType<AgentCheckpointPersistenceResult>(
                    store.FinalizeClaimedPermissionRequest(
                        Assert.IsType<AgentPendingPermissionRequestRecord>(claim.Request),
                        AgentPendingPermissionStatus.Failed,
                        AgentRunStatus.Failed,
                        "Failed."));
                break;
            case "expire":
                var expiration = store.ExpireActivePermissionRequest(
                    session.SessionId,
                    request.RequestId,
                    "Expired.");
                Assert.True(expiration.Changed);
                finalization = Assert.IsType<AgentCheckpointPersistenceResult>(expiration.Finalization);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(terminalPath));
        }

        Assert.Equal(
            [first.TurnId, second.TurnId],
            finalization.CompletedStreamingTurns.Select(item => item.Turn.TurnId));
        Assert.Equal(
            ["first partial".Length, "second partial".Length],
            finalization.CompletedStreamingTurns.Select(item => item.ContentLength));
        Assert.All(finalization.CompletedStreamingTurns, item => Assert.False(item.Turn.IsStreaming));
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
