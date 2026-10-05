using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Xunit;

namespace Sunder.Package.Agent.Tests;

extern alias MemoryWorker;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic;

public sealed class SqlitePostCommitMaintenanceTests
{
    [Fact]
    public async Task DeleteLastSession_ReturnsSuccessWhileWalReaderIsPinned()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        try
        {
            var workspace = SaveWorkspace(store);
            var session = store.CreateSession(
                "Only session",
                profileId: "profile.test",
                workspaceId: workspace.WorkspaceId);
            await store.WaitForWalCheckpointIdleAsync();
            var initialAttemptCount = store.WalCheckpointAttemptCount;
            using var reader = PinnedWalReader.Open(
                store.DatabasePath,
                "SELECT SessionId FROM AgentSessions WHERE SessionId = $id;",
                command => command.Parameters.AddWithValue("$id", session.SessionId.ToString()));

            var exception = Record.Exception(() => store.DeleteSessionTree(session.SessionId));

            Assert.Null(exception);
            Assert.Null(store.GetSession(session.SessionId));
            await DrainAgentWalAsync(store, reader, initialAttemptCount);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task DeleteLastSession_RetriesPastLegacyAttemptBudgetWithoutAnotherSignal()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        try
        {
            var workspace = SaveWorkspace(store);
            var session = store.CreateSession(
                "Autonomous retry session",
                profileId: "profile.test",
                workspaceId: workspace.WorkspaceId);
            await store.WaitForWalCheckpointIdleAsync();
            var initialAttemptCount = store.WalCheckpointAttemptCount;
            using var reader = PinnedWalReader.Open(
                store.DatabasePath,
                "SELECT SessionId FROM AgentSessions WHERE SessionId = $id;",
                command => command.Parameters.AddWithValue("$id", session.SessionId.ToString()));

            store.DeleteSessionTree(session.SessionId);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (store.WalCheckpointAttemptCount < initialAttemptCount + 20)
            {
                var observed = store.WalCheckpointAttemptCount;
                Assert.False(await store.WaitForWalCheckpointAttemptAfterAsync(observed, timeout.Token));
            }

            reader.Dispose();
            var retryAfter = store.WalCheckpointAttemptCount;
            Assert.True(await store.WaitForWalCheckpointAttemptAfterAsync(retryAfter, timeout.Token));
            await store.WaitForWalCheckpointIdleAsync(timeout.Token);
            AssertWalDrained(store.DatabasePath);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("workspace-session-trees")]
    [InlineData("workspace-delete")]
    [InlineData("transcript-rollback")]
    [InlineData("rollback-admission")]
    [InlineData("rollback-run-start")]
    public async Task DestructiveAgentMutationGroups_DoNotReportCheckpointBusyAsFailure(string operation)
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        try
        {
            var workspace = SaveWorkspace(store);
            var session = store.CreateSession(
                "Mutation session",
                profileId: "profile.test",
                workspaceId: workspace.WorkspaceId);
            Action mutation = operation switch
            {
                "workspace-session-trees" => () =>
                    Assert.Contains(session.SessionId, store.DeleteSessionTreesForWorkspace(workspace.WorkspaceId)),
                "workspace-delete" => () =>
                    Assert.Contains(session.SessionId, store.DeleteWorkspaceWithSessions(workspace.WorkspaceId)),
                "transcript-rollback" => CreateTranscriptRollback(store, session),
                "rollback-admission" => CreateRollbackAdmission(store, session, workspace.WorkspaceId),
                "rollback-run-start" => CreateRollbackRunStart(store, session),
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            };
            await store.WaitForWalCheckpointIdleAsync();
            var initialAttemptCount = store.WalCheckpointAttemptCount;
            using var reader = PinnedWalReader.Open(
                store.DatabasePath,
                "SELECT SessionId FROM AgentSessions WHERE SessionId = $id;",
                command => command.Parameters.AddWithValue("$id", session.SessionId.ToString()));

            var exception = Record.Exception(mutation);

            Assert.Null(exception);
            await DrainAgentWalAsync(store, reader, initialAttemptCount);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task AmbiguousCommittedAdmission_PreservesAttachmentAndStartupRetriesMissedSignal()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = SaveWorkspace(store);
        var sessions = new AgentSessionService(store);
        var session = sessions.CreateSession(
            "Attachment session",
            profileId: "profile.test",
            workspaceId: workspace.WorkspaceId);
        var anchor = store.AppendTextTurn(session.SessionId, AgentMessageRole.User, "replace me");
        store.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, "stale response");
        var attachments = new AgentAttachmentService(scope.Context);
        var activeRuns = new AgentActiveRunRegistry();
        using var supersededCancellation = new CancellationTokenSource();
        Assert.True(activeRuns.Activate(
            session.SessionId,
            new AgentActiveRunHandle(
                Guid.NewGuid(),
                0,
                DateTimeOffset.UtcNow,
                "profile.test",
                "stale run",
                supersededCancellation)).IsAccepted);
        var admission = new AgentUserTurnAdmissionService(
            sessions,
            new AgentRunAttachmentStore(attachments),
            activeRuns,
            new AgentSessionTransitionGate(),
            new AgentSessionDeletionFence());
        var bytes = Encoding.UTF8.GetBytes("durable attachment after ambiguous commit");
        var userTurnId = Guid.NewGuid();
        await store.WaitForWalCheckpointIdleAsync();
        using var reader = PinnedWalReader.Open(
            store.DatabasePath,
            "SELECT SessionId FROM AgentSessions WHERE SessionId = $id;",
            command => command.Parameters.AddWithValue("$id", session.SessionId.ToString()));
        store.AfterUserTurnAdmissionCommit = static () =>
            throw new InvalidOperationException("Injected failure after admission commit.");

        await Assert.ThrowsAsync<InvalidOperationException>(() => admission.AdmitAsync(
            session.SessionId,
            "profile.test",
            "replacement with attachment",
            workspace.WorkspaceId,
            [new AgentAttachmentUploadRequest("evidence.txt", "text/plain", bytes)],
            userTurnId,
            anchor.TurnId,
            CancellationToken.None));

        store.AfterUserTurnAdmissionCommit = null;
        Assert.Equal(AgentRunCommandStatus.Committed, admission.GetCommandStatus(session.SessionId, userTurnId));
        Assert.True(supersededCancellation.IsCancellationRequested);
        var committedTurn = Assert.IsType<AgentTurnRecord>(store.GetTurn(userTurnId));
        var metadata = ReadAttachmentMetadata(committedTurn);
        Assert.Equal(bytes, await attachments.ReadAttachmentBytesAsync(metadata));
        await store.DisposeAsync();

        var restarted = new AgentLocalStore(scope.Context);
        try
        {
            var observedAttempts = restarted.WalCheckpointAttemptCount;
            var startupSucceeded = observedAttempts == 0
                ? await restarted.WaitForWalCheckpointAttemptAfterAsync(0)
                : await restarted.WaitForWalCheckpointAttemptAfterAsync(observedAttempts - 1);
            Assert.False(startupSucceeded);
            reader.Dispose();
            var retryAfter = restarted.WalCheckpointAttemptCount;
            Assert.True(await restarted.WaitForWalCheckpointAttemptAfterAsync(retryAfter));
            await restarted.WaitForWalCheckpointIdleAsync();
            AssertWalDrained(restarted.DatabasePath);
            Assert.Equal(bytes, await new AgentAttachmentService(scope.Context).ReadAttachmentBytesAsync(metadata));
        }
        finally
        {
            await restarted.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("delete-session")]
    public async Task HistoryDeletionMutations_ReturnSuccessWhileWalReaderIsPinned(string operation)
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new HistorySearchStore(scope.Context);
        try
        {
            var generation = store.BeginTextGeneration();
            var document = CreateHistoryDocument();
            store.ReplaceTurnDocuments(generation, document.SessionId, document.TurnId, [document]);
            store.ActivateTextGeneration(generation);
            await store.WaitForWalCheckpointIdleAsync();
            var initialAttemptCount = store.WalCheckpointAttemptCount;
            using var reader = PinnedWalReader.Open(
                store.DatabasePath,
                "SELECT BodyText FROM HistoryDocuments WHERE DocumentId = $id;",
                command => command.Parameters.AddWithValue("$id", document.DocumentId));

            var exception = Record.Exception(() =>
            {
                if (operation == "clear")
                {
                    store.ClearDerivedIndex();
                }
                else
                {
                    Assert.True(store.DeleteSessionDocuments(document.SessionId));
                }
            });

            Assert.Null(exception);
            await DrainHistoryWalAsync(store, reader, initialAttemptCount);
            Assert.Equal(0, store.GetSnapshot().DocumentCount);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task DirectSemanticEmbeddingDeletion_ReturnsSuccessWhileWalReaderIsPinned()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new MemoryLocalStore(scope.Context);
        try
        {
            var sessionId = Guid.NewGuid();
            var memory = store.UpsertMemory(new MemoryUpsertRequest(
                sessionId,
                "project-fact",
                "The project uses cobalt.",
                "the project uses cobalt.",
                "Direct evidence.",
                Guid.NewGuid(),
                false,
                0.8f,
                0.9f));
            var now = DateTimeOffset.UtcNow;
            store.UpsertEmbedding(new StoredMemoryEmbeddingRecord(
                memory.MemoryId,
                sessionId,
                "provider",
                "model",
                new string('a', 64),
                2,
                [0.25f, 0.75f],
                now,
                now));
            await store.WaitForWalCheckpointIdleAsync();
            var initialAttemptCount = store.WalCheckpointAttemptCount;
            using var reader = PinnedWalReader.Open(
                store.DatabasePath,
                "SELECT MemoryId FROM SessionMemoryEmbeddings WHERE MemoryId = $id;",
                command => command.Parameters.AddWithValue("$id", memory.MemoryId.ToString()));

            var exception = Record.Exception(() => store.DeleteEmbeddings(sessionId));

            Assert.Null(exception);
            Assert.Null(store.GetEmbedding(memory.MemoryId));
            await DrainSemanticWalAsync(store, reader, initialAttemptCount);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task WorkspaceListeners_AreIsolatedAfterCommittedMutation()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        try
        {
            var workspaces = new AgentWorkspaceService(store);
            var laterListenerCalls = 0;
            workspaces.WorkspacesChanged += static () => throw new InvalidOperationException("listener failure");
            workspaces.WorkspacesChanged += () => laterListenerCalls++;

            var exception = Record.Exception(() => workspaces.CreateWorkspace("Listener isolation"));

            Assert.Null(exception);
            Assert.Equal(1, laterListenerCalls);
            Assert.Single(store.ListWorkspaces(), workspace => workspace.DisplayName == "Listener isolation");
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task WorkspaceListeners_PropagateWrappedFatalFailureAfterCommit()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        try
        {
            var workspaces = new AgentWorkspaceService(store);
            var fatal = new Exception(
                "Wrapped fatal workspace listener.",
                new OutOfMemoryException("Injected fatal workspace listener."));
            workspaces.WorkspacesChanged += () => throw fatal;

            var thrown = Assert.Throws<Exception>(() => workspaces.CreateWorkspace("Fatal listener workspace"));

            Assert.Same(fatal, thrown);
            Assert.Single(store.ListWorkspaces(), workspace => workspace.DisplayName == "Fatal listener workspace");
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task ProfileListeners_AreIsolatedAfterCommittedMutation()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        using var catalog = new RegressionTestExtensionCatalog();
        var sessions = new AgentSessionService(store, catalog);
        var workspaces = new AgentWorkspaceService(store, catalog, sessions);
        var tools = new AgentToolService(
            sessions,
            workspaces,
            new AgentExecutionTargetService(catalog),
            catalog);
        using var profiles = new AgentProfileService(
            store,
            tools,
            catalog,
            catalog.BehaviorLoops,
            refreshProviderSubscriptions: false);
        try
        {
            string? observedProfileId = null;
            profiles.ProfileChanged += static _ => throw new InvalidOperationException("listener failure");
            profiles.ProfileChanged += profileId => observedProfileId = profileId;

            var profile = await profiles.CreateProfileAsync("Listener isolation");

            Assert.Equal(profile.ProfileId, observedProfileId);
            var persisted = Assert.IsType<AgentProfileRecord>(store.GetProfile(profile.ProfileId));
            Assert.Equal(profile.ProfileId, persisted.ProfileId);
            Assert.Equal(profile.DisplayName, persisted.DisplayName);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task ProfileListeners_PropagateWrappedFatalFailureAfterCommit()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        using var catalog = new RegressionTestExtensionCatalog();
        var sessions = new AgentSessionService(store, catalog);
        var workspaces = new AgentWorkspaceService(store, catalog, sessions);
        var tools = new AgentToolService(
            sessions,
            workspaces,
            new AgentExecutionTargetService(catalog),
            catalog);
        using var profiles = new AgentProfileService(
            store,
            tools,
            catalog,
            catalog.BehaviorLoops,
            refreshProviderSubscriptions: false);
        try
        {
            var fatal = new Exception(
                "Wrapped fatal profile listener.",
                new OutOfMemoryException("Injected fatal profile listener."));
            profiles.ProfileChanged += _ => throw fatal;

            var thrown = await Assert.ThrowsAsync<Exception>(() => profiles.CreateProfileAsync("Fatal listener profile"));

            Assert.Same(fatal, thrown);
            Assert.Single(store.ListProfiles(), profile => profile.DisplayName == "Fatal listener profile");
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    private static Action CreateTranscriptRollback(AgentLocalStore store, AgentSessionRecord session)
    {
        var anchor = store.AppendTextTurn(session.SessionId, AgentMessageRole.User, "replace me");
        store.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, "stale response");
        return () => Assert.NotEmpty(store.RollbackTranscript(session.SessionId, anchor.TurnId).DeletedTurnIds);
    }

    private static Action CreateRollbackAdmission(
        AgentLocalStore store,
        AgentSessionRecord session,
        string workspaceId)
    {
        var anchor = store.AppendTextTurn(session.SessionId, AgentMessageRole.User, "replace me");
        store.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, "stale response");
        var userTurnId = Guid.NewGuid();
        const string message = "replacement admission";
        var fingerprint = AgentUserTurnRequestFingerprint.Compute(
            session.SessionId,
            "profile.test",
            workspaceId,
            message,
            AgentRunAdmissionKind.Rollback,
            anchor.TurnId,
            []);
        var request = new AgentUserTurnAdmissionRequest(
            userTurnId,
            session.SessionId,
            "profile.test",
            workspaceId,
            message,
            AgentRunAdmissionKind.Rollback,
            anchor.TurnId,
            fingerprint,
            []);
        return () => Assert.False(store.AdmitUserTurn(request).IsExisting);
    }

    private static Action CreateRollbackRunStart(AgentLocalStore store, AgentSessionRecord session)
    {
        var anchor = store.AppendTextTurn(session.SessionId, AgentMessageRole.User, "replace me");
        store.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, "stale response");
        var run = store.ReserveRun(session.SessionId, "profile.test", "replacement run");
        return () => Assert.NotNull(store.TryStartRun(
            run.Key,
            run.Epoch,
            run.UserMessage,
            [],
            anchor.TurnId,
            "Running."));
    }

    private static AgentWorkspaceRecord SaveWorkspace(AgentLocalStore store)
    {
        var now = DateTimeOffset.UtcNow;
        var workspace = new AgentWorkspaceRecord(
            Guid.NewGuid().ToString("N"),
            "Maintenance workspace",
            null,
            now,
            now);
        store.SaveWorkspace(workspace);
        return workspace;
    }

    private static HistoryProjectionDocument CreateHistoryDocument()
    {
        var itemId = Guid.NewGuid();
        var body = "history checkpoint canary";
        return new HistoryProjectionDocument(
            HistorySearchExtractor.CreateDocumentId(itemId, 0, HistoryAnchorKind.Text),
            "workspace",
            "Workspace",
            Guid.NewGuid(),
            "Session",
            RootSessionId: null,
            ParentSessionId: null,
            ProfileId: "profile.test",
            AgentMessageRole.User,
            HistoryActivityKind.None,
            Guid.NewGuid(),
            itemId,
            CallId: null,
            HistoryAnchorKind.Text,
            SourceContentRevision: 1,
            SourceIsStreaming: false,
            DateTimeOffset.UtcNow,
            body,
            body,
            []);
    }

    private static AgentAttachmentMetadata ReadAttachmentMetadata(AgentTurnRecord turn)
        => JsonSerializer.Deserialize<AgentAttachmentMetadata>(
               Assert.Single(turn.Items, item => item.Kind == AgentTurnItemKind.Attachment)
                   .StructuredPayloadJson!)
           ?? throw new Xunit.Sdk.XunitException("Attachment metadata was not persisted.");

    private static async Task DrainAgentWalAsync(
        AgentLocalStore store,
        PinnedWalReader reader,
        int initialAttemptCount)
        => await DrainWalAsync(
            store.DatabasePath,
            () => store.WalCheckpointAttemptCount,
            store.WaitForWalCheckpointAttemptAfterAsync,
            store.WaitForWalCheckpointIdleAsync,
            reader,
            initialAttemptCount);

    private static async Task DrainHistoryWalAsync(
        HistorySearchStore store,
        PinnedWalReader reader,
        int initialAttemptCount)
        => await DrainWalAsync(
            store.DatabasePath,
            () => store.WalCheckpointAttemptCount,
            store.WaitForWalCheckpointAttemptAfterAsync,
            store.WaitForWalCheckpointIdleAsync,
            reader,
            initialAttemptCount);

    private static async Task DrainSemanticWalAsync(
        MemoryLocalStore store,
        PinnedWalReader reader,
        int initialAttemptCount)
        => await DrainWalAsync(
            store.DatabasePath,
            () => store.WalCheckpointAttemptCount,
            store.WaitForWalCheckpointAttemptAfterAsync,
            store.WaitForWalCheckpointIdleAsync,
            reader,
            initialAttemptCount);

    private static async Task DrainWalAsync(
        string databasePath,
        Func<int> getAttemptCount,
        Func<int, CancellationToken, Task<bool>> waitForAttempt,
        Func<CancellationToken, Task> waitForIdle,
        PinnedWalReader reader,
        int initialAttemptCount)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.False(await waitForAttempt(initialAttemptCount, timeout.Token));
        reader.Dispose();
        var retryAfter = getAttemptCount();
        Assert.True(await waitForAttempt(retryAfter, timeout.Token));
        await waitForIdle(timeout.Token);
        AssertWalDrained(databasePath);
    }

    private static void AssertWalDrained(string databasePath)
    {
        var walPath = databasePath + "-wal";
        Assert.True(!File.Exists(walPath) || new FileInfo(walPath).Length == 0);
    }

    private sealed class PinnedWalReader : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;
        private readonly SqliteCommand _command;
        private readonly SqliteDataReader _reader;
        private bool _disposed;

        private PinnedWalReader(
            SqliteConnection connection,
            SqliteTransaction transaction,
            SqliteCommand command,
            SqliteDataReader reader)
        {
            _connection = connection;
            _transaction = transaction;
            _command = command;
            _reader = reader;
        }

        internal static PinnedWalReader Open(
            string databasePath,
            string query,
            Action<SqliteCommand> bind)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString());
            connection.Open();
            var transaction = connection.BeginTransaction(deferred: true);
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = query;
            bind(command);
            var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            return new PinnedWalReader(connection, transaction, command, reader);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _reader.Dispose();
            _command.Dispose();
            _transaction.Dispose();
            _connection.Dispose();
        }
    }
}
