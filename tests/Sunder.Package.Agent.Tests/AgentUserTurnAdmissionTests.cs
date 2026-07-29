using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentUserTurnAdmissionTests
{
    [Fact]
    public async Task SameIdSequentialAdmission_ReturnsOneDurableRunAndTurn()
    {
        using var fixture = AdmissionFixture.Create();
        var userTurnId = Guid.NewGuid();

        var first = await fixture.AdmitAsync(userTurnId, "same request");
        var duplicate = await fixture.AdmitAsync(userTurnId, "same request");

        Assert.False(first.IsExisting);
        Assert.True(duplicate.IsExisting);
        Assert.Equal(first.Run.Key, duplicate.Run.Key);
        Assert.Equal(first.Checkpoint.CheckpointId, duplicate.Checkpoint.CheckpointId);
        Assert.Single(fixture.Store.ListTurns(fixture.Session.SessionId));
        Assert.Equal(1, CountRows(fixture.Store.DatabasePath, "AgentRuns"));
        Assert.Equal(1, CountRows(fixture.Store.DatabasePath, "AgentRunCheckpoints"));
        Assert.Single(
            fixture.Store.ListLifecycleOutboxEvents(),
            item => item.Kind == AgentLifecycleEventKind.UserTurnAdded);
    }

    [Fact]
    public async Task SameIdConcurrentAdmission_HasOneWinnerAndOneIdempotentResult()
    {
        using var fixture = AdmissionFixture.Create();
        var userTurnId = Guid.NewGuid();
        var request = fixture.CreateStoreRequest(userTurnId, "concurrent request");
        using var barrier = new Barrier(3);

        var admissions = Enumerable.Range(0, 2)
            .Select(_ => Task.Run(() =>
            {
                barrier.SignalAndWait();
                return fixture.Store.AdmitUserTurn(request);
            }))
            .ToArray();
        barrier.SignalAndWait();
        var results = await Task.WhenAll(admissions);

        Assert.Single(results, result => !result.IsExisting);
        Assert.Single(results, result => result.IsExisting);
        Assert.Single(results.Select(result => result.Run.Key.RunId).Distinct());
        Assert.Equal(1, CountRows(fixture.Store.DatabasePath, "AgentRuns"));
        Assert.Single(fixture.Store.ListTurns(fixture.Session.SessionId));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("profile")]
    [InlineData("workspace")]
    [InlineData("message")]
    [InlineData("rollback")]
    public async Task SameIdDifferentRequestIdentity_FailsClosed(string difference)
    {
        using var fixture = AdmissionFixture.Create();
        var userTurnId = Guid.NewGuid();
        await fixture.AdmitAsync(userTurnId, "original");
        var secondSession = difference == "session"
            ? fixture.Sessions.CreateSession(
                "Other session",
                workspaceId: fixture.Workspace.WorkspaceId).SessionId
            : fixture.Session.SessionId;
        var profileId = difference == "profile" ? "profile.other" : fixture.ProfileId;
        var workspaceId = difference == "workspace" ? "workspace.other" : fixture.Workspace.WorkspaceId;
        var message = difference == "message" ? "changed" : "original";
        var rollbackAnchor = difference == "rollback" ? Guid.NewGuid() : (Guid?)null;

        await Assert.ThrowsAsync<AgentUserTurnConflictException>(() =>
            fixture.Admission.AdmitAsync(
                secondSession,
                profileId,
                message,
                workspaceId,
                [],
                userTurnId,
                rollbackAnchor,
                CancellationToken.None));

        Assert.Equal(1, CountRows(fixture.Store.DatabasePath, "AgentRuns"));
        Assert.Equal("original", Assert.Single(
            fixture.Store.GetTurn(userTurnId)!.Items).TextContent);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("name")]
    [InlineData("media-type")]
    public async Task SameIdDifferentAttachmentFingerprint_FailsAndCleansLosingAdoption(string difference)
    {
        using var fixture = AdmissionFixture.Create();
        var userTurnId = Guid.NewGuid();
        var first = new AgentAttachmentUploadRequest(
            "note.txt",
            "text/plain",
            Encoding.UTF8.GetBytes("one"));
        await fixture.Admission.AdmitAsync(
            fixture.Session.SessionId,
            fixture.ProfileId,
            "with attachment",
            fixture.Workspace.WorkspaceId,
            [first],
            userTurnId,
            rollbackAnchorTurnId: null,
            CancellationToken.None);
        var second = difference switch
        {
            "hash" => first with { Content = Encoding.UTF8.GetBytes("two") },
            "name" => first with { FileName = "other.txt" },
            "media-type" => first with { MediaType = "application/octet-stream" },
            _ => throw new ArgumentOutOfRangeException(nameof(difference)),
        };

        await Assert.ThrowsAsync<AgentUserTurnConflictException>(() =>
            fixture.Admission.AdmitAsync(
                fixture.Session.SessionId,
                fixture.ProfileId,
                "with attachment",
                fixture.Workspace.WorkspaceId,
                [second],
                userTurnId,
                rollbackAnchorTurnId: null,
                CancellationToken.None));

        Assert.Single(fixture.Store.ListReferencedAttachmentPaths());
        Assert.Equal(1, CountAttachmentFiles(fixture.Scope.Context));
    }

    [Fact]
    public async Task RollbackAdmissionFailure_RollsBackTranscriptSupersessionAndOutbox()
    {
        using var fixture = AdmissionFixture.Create();
        var anchor = fixture.Store.AppendTextTurn(
            fixture.Session.SessionId,
            AgentMessageRole.User,
            "keep me");
        var response = fixture.Store.AppendTextTurn(
            fixture.Session.SessionId,
            AgentMessageRole.Assistant,
            "keep this too");
        var oldRun = fixture.Store.ReserveRun(
            fixture.Session.SessionId,
            fixture.ProfileId,
            "unfinished");
        var running = Assert.IsType<AgentRunTransitionResult>(fixture.Store.TryTransitionRun(
            oldRun.Key,
            oldRun.Epoch,
            AgentRunStatus.Running,
            "Running."));
        var userTurnId = Guid.NewGuid();
        using (var connection = OpenDatabase(fixture.Store.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $$"""
                CREATE TRIGGER RejectAdmissionTurn
                BEFORE INSERT ON AgentTurns
                WHEN NEW.TurnId = '{{userTurnId}}'
                BEGIN
                    SELECT RAISE(ABORT, 'reject admitted turn');
                END;
                """;
            command.ExecuteNonQuery();
        }

        await Assert.ThrowsAsync<SqliteException>(() => fixture.AdmitAsync(
            userTurnId,
            "replacement",
            anchor.TurnId));

        Assert.Equal(
            [anchor.TurnId, response.TurnId],
            fixture.Store.ListTurns(fixture.Session.SessionId).Select(turn => turn.TurnId));
        Assert.Equal(AgentDurableRunStatus.Running, fixture.Store.GetRun(oldRun.Key.RunId)?.Status);
        Assert.Equal(running.Checkpoint.CheckpointId, fixture.Store.GetLatestCheckpoint(
            fixture.Session.SessionId)?.CheckpointId);
        Assert.DoesNotContain(
            fixture.Store.ListLifecycleOutboxEvents(),
            item => item.Kind == AgentLifecycleEventKind.TranscriptRolledBack);
        Assert.Null(fixture.Store.GetRunByUserTurnId(userTurnId));
    }

    [Fact]
    public async Task AttachmentBytes_ReopenDurablyAndGraceBoundedGcRemovesOnlyOrphans()
    {
        using var fixture = AdmissionFixture.Create();
        var bytes = Encoding.UTF8.GetBytes("durable attachment bytes");
        var userTurnId = Guid.NewGuid();
        await fixture.Admission.AdmitAsync(
            fixture.Session.SessionId,
            fixture.ProfileId,
            "persist attachment",
            fixture.Workspace.WorkspaceId,
            [new AgentAttachmentUploadRequest("durable.txt", "text/plain", bytes)],
            userTurnId,
            rollbackAnchorTurnId: null,
            CancellationToken.None);
        var metadata = ReadAttachmentMetadata(fixture.Store.GetTurn(userTurnId)!);
        var reopenedStore = new AgentLocalStore(fixture.Scope.Context);
        var reopenedAttachments = new AgentAttachmentService(fixture.Scope.Context);
        var attachmentRoot = fixture.Scope.Context.Storage.RoleLocalWorkspace.GetLocalPath("agent/attachments");
        var orphanDirectory = Path.Combine(attachmentRoot, "orphan", "turn");
        Directory.CreateDirectory(orphanDirectory);
        var orphanPath = Path.Combine(orphanDirectory, "orphan.bin");
        File.WriteAllBytes(orphanPath, [1, 2, 3]);
        File.SetLastWriteTimeUtc(orphanPath, DateTime.UtcNow.AddHours(-2));

        var reopenedBytes = await reopenedAttachments.ReadAttachmentBytesAsync(metadata);
        var deleted = reopenedAttachments.CleanupOrphans(
            reopenedStore.ListReferencedAttachmentPaths(),
            DateTimeOffset.UtcNow.AddHours(-1));

        Assert.Equal(bytes, reopenedBytes);
        Assert.Equal(1, deleted);
        Assert.False(File.Exists(orphanPath));
        Assert.Equal(bytes, await reopenedAttachments.ReadAttachmentBytesAsync(metadata));
    }

    [Fact]
    public async Task CompletedTransfer_IsDetachedOnlyAfterDurableAdmissionCommit()
    {
        using var fixture = AdmissionFixture.Create();
        using var transfers = new AgentAttachmentTransferService(fixture.Scope.Context);
        var bytes = Encoding.UTF8.GetBytes("transferred bytes");
        var descriptor = new AgentAttachmentUploadDescriptor(
            "transfer.txt",
            "text/plain",
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var started = transfers.BeginUpload(descriptor);
        transfers.WriteUploadChunk(started.TransferId!, 0, bytes);
        transfers.CompleteUpload(started.TransferId!);
        var handle = new AgentAttachmentUploadHandle(started.TransferId!);
        var userTurnId = Guid.NewGuid();

        var admission = await fixture.Admission.AdmitTransferredAsync(
            fixture.Session.SessionId,
            fixture.ProfileId,
            "transferred attachment",
            fixture.Workspace.WorkspaceId,
            [handle],
            transfers,
            userTurnId,
            rollbackAnchorTurnId: null,
            CancellationToken.None);
        var metadata = ReadAttachmentMetadata(admission.UserTurn!);

        Assert.Throws<InvalidOperationException>(() => transfers.BeginAdoption([handle]));
        var reopenedAttachments = new AgentAttachmentService(fixture.Scope.Context);
        Assert.Equal(bytes, await reopenedAttachments.ReadAttachmentBytesAsync(metadata));
    }

    [Fact]
    public async Task ConflictingTransferredRetry_CleansAdoptedCopyButRetainsTransferHandle()
    {
        using var fixture = AdmissionFixture.Create();
        var userTurnId = Guid.NewGuid();
        await fixture.Admission.AdmitAsync(
            fixture.Session.SessionId,
            fixture.ProfileId,
            "attachment conflict",
            fixture.Workspace.WorkspaceId,
            [new AgentAttachmentUploadRequest(
                "transfer.txt",
                "text/plain",
                Encoding.UTF8.GetBytes("first"))],
            userTurnId,
            rollbackAnchorTurnId: null,
            CancellationToken.None);
        using var transfers = new AgentAttachmentTransferService(fixture.Scope.Context);
        var bytes = Encoding.UTF8.GetBytes("other");
        var started = transfers.BeginUpload(new AgentAttachmentUploadDescriptor(
            "transfer.txt",
            "text/plain",
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
        transfers.WriteUploadChunk(started.TransferId!, 0, bytes);
        transfers.CompleteUpload(started.TransferId!);
        var handle = new AgentAttachmentUploadHandle(started.TransferId!);

        await Assert.ThrowsAsync<AgentUserTurnConflictException>(() =>
            fixture.Admission.AdmitTransferredAsync(
                fixture.Session.SessionId,
                fixture.ProfileId,
                "attachment conflict",
                fixture.Workspace.WorkspaceId,
                [handle],
                transfers,
                userTurnId,
                rollbackAnchorTurnId: null,
                CancellationToken.None));

        var retryable = transfers.BeginAdoption([handle]);
        transfers.ReleaseAdoption(retryable);
        Assert.Equal(1, CountAttachmentFiles(fixture.Scope.Context));
    }

    [Fact]
    public async Task CorrelationSurvivesRollbackAndReopenAfterOriginatingTurnIsDeleted()
    {
        using var fixture = AdmissionFixture.Create();
        var firstUserTurnId = Guid.NewGuid();
        var first = await fixture.AdmitAsync(firstUserTurnId, "first");
        var replacementUserTurnId = Guid.NewGuid();
        await fixture.AdmitAsync(
            replacementUserTurnId,
            "replacement",
            firstUserTurnId);

        Assert.Null(fixture.Store.GetTurn(firstUserTurnId));
        Assert.Equal(AgentDurableRunStatus.Interrupted, fixture.Store.GetRun(first.Run.Key.RunId)?.Status);
        var reopenedStore = new AgentLocalStore(fixture.Scope.Context);
        var reopenedSessions = new AgentSessionService(reopenedStore);
        var reopenedAdmission = new AgentUserTurnAdmissionService(
            reopenedSessions,
            new AgentRunAttachmentStore(new AgentAttachmentService(fixture.Scope.Context)),
            new AgentActiveRunRegistry());

        Assert.Equal(
            AgentRunCommandStatus.Committed,
            reopenedAdmission.GetCommandStatus(fixture.Session.SessionId, firstUserTurnId));
        Assert.Equal(firstUserTurnId, reopenedStore.GetRunByUserTurnId(firstUserTurnId)?.UserTurnId);
        Assert.Equal(replacementUserTurnId, reopenedStore.GetTurn(replacementUserTurnId)?.TurnId);
    }

    [Fact]
    public async Task RunningAdmission_IsInterruptedOnRecoveryAndNeverReturnsToPreparingScan()
    {
        using var fixture = AdmissionFixture.Create();
        var admission = await fixture.AdmitAsync(Guid.NewGuid(), "start exactly once");
        var running = Assert.IsType<AgentRunTransitionResult>(
            fixture.Store.TryBeginAdmittedRunExecution(
                admission.Run.Key,
                admission.Run.Epoch,
                "Execution starting."));
        Assert.NotNull(running.Run.ExecutionStartedAtUtc);

        var reopened = new AgentLocalStore(fixture.Scope.Context);
        Assert.Equal(AgentDurableRunStatus.Running, reopened.GetRun(admission.Run.Key.RunId)?.Status);
        reopened.RecoverRuntimeState();

        Assert.Equal(AgentDurableRunStatus.Interrupted, reopened.GetRun(admission.Run.Key.RunId)?.Status);
        Assert.Empty(reopened.ListPreparingAdmissions(10));
        Assert.Contains(
            reopened.ListLifecycleOutboxEvents(),
            item => item.Kind == AgentLifecycleEventKind.RunInterrupted);
    }

    private static AgentAttachmentMetadata ReadAttachmentMetadata(AgentTurnRecord turn)
        => JsonSerializer.Deserialize<AgentAttachmentMetadata>(
               Assert.Single(turn.Items, item => item.Kind == AgentTurnItemKind.Attachment)
                   .StructuredPayloadJson!)
           ?? throw new Xunit.Sdk.XunitException("Attachment metadata was not persisted.");

    private static int CountRows(string databasePath, string tableName)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static int CountAttachmentFiles(Sunder.Sdk.Abstractions.IPackageContext context)
    {
        var root = context.Storage.RoleLocalWorkspace.GetLocalPath("agent/attachments");
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count()
            : 0;
    }

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

    private sealed class AdmissionFixture : IDisposable
    {
        private AdmissionFixture(
            DurableRunTestScope scope,
            AgentLocalStore store,
            AgentSessionService sessions,
            AgentWorkspaceRecord workspace,
            AgentSessionRecord session,
            AgentUserTurnAdmissionService admission)
        {
            Scope = scope;
            Store = store;
            Sessions = sessions;
            Workspace = workspace;
            Session = session;
            Admission = admission;
        }

        internal string ProfileId { get; } = "profile.test";

        internal DurableRunTestScope Scope { get; }
        internal AgentLocalStore Store { get; }
        internal AgentSessionService Sessions { get; }
        internal AgentWorkspaceRecord Workspace { get; }
        internal AgentSessionRecord Session { get; }
        internal AgentUserTurnAdmissionService Admission { get; }

        internal static AdmissionFixture Create()
        {
            var scope = DurableRunTestScope.Create();
            var store = new AgentLocalStore(scope.Context);
            var sessions = new AgentSessionService(store);
            var workspaces = new AgentWorkspaceService(store, sessionService: sessions);
            var workspace = workspaces.CreateWorkspace("Admission tests");
            var session = sessions.CreateSession(
                "Admission session",
                profileId: "profile.test",
                workspaceId: workspace.WorkspaceId);
            var attachmentService = new AgentAttachmentService(scope.Context);
            var admission = new AgentUserTurnAdmissionService(
                sessions,
                new AgentRunAttachmentStore(attachmentService),
                new AgentActiveRunRegistry(),
                new AgentSessionTransitionGate(),
                new AgentSessionDeletionFence());
            return new AdmissionFixture(scope, store, sessions, workspace, session, admission);
        }

        internal Task<AgentUserTurnAdmissionResult> AdmitAsync(
            Guid userTurnId,
            string message,
            Guid? rollbackAnchorTurnId = null)
            => Admission.AdmitAsync(
                Session.SessionId,
                ProfileId,
                message,
                Workspace.WorkspaceId,
                [],
                userTurnId,
                rollbackAnchorTurnId,
                CancellationToken.None);

        internal AgentUserTurnAdmissionRequest CreateStoreRequest(Guid userTurnId, string message)
        {
            var fingerprint = AgentUserTurnRequestFingerprint.Compute(
                Session.SessionId,
                ProfileId,
                Workspace.WorkspaceId,
                message,
                AgentRunAdmissionKind.Normal,
                rollbackAnchorTurnId: null,
                []);
            return new AgentUserTurnAdmissionRequest(
                userTurnId,
                Session.SessionId,
                ProfileId,
                Workspace.WorkspaceId,
                message,
                AgentRunAdmissionKind.Normal,
                RollbackAnchorTurnId: null,
                fingerprint,
                []);
        }

        public void Dispose() => Scope.Dispose();
    }
}
