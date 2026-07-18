using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentLocalStoreDurableRunTests
{
    [Fact]
    public void AgentLocalStore_BootstrapsVersionedMigrationsAndAgentRuns()
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);

        using var connection = OpenDatabase(store.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Version, Name, Checksum FROM SchemaMigrations ORDER BY Version;";
        using var reader = command.ExecuteReader();
        var migrations = new List<(long Version, string Name, string Checksum)>();
        while (reader.Read())
        {
            migrations.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        }

        Assert.Equal(
            [
                (1L, "legacy-schema-baseline"),
                (2L, "agent-runs"),
                (3L, "pending-permission-state"),
                (4L, "typed-run-suspensions"),
                (5L, "permission-claim-recovery"),
                (6L, "parent-continuation-work"),
                (7L, "permission-execution-snapshot"),
                (8L, "turn-content-revisions"),
                (9L, "turn-run-ownership"),
            ],
            migrations.Select(static migration => (migration.Version, migration.Name)));
        Assert.All(migrations, migration => Assert.Matches("^[0-9a-f]{64}$", migration.Checksum));
    }

    [Fact]
    public async Task ReserveRun_ConcurrentWritersAllocateUniqueRevisionsAfterLegacyCheckpoint()
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var session = CreateSession(store);
        store.SaveCheckpoint(
            session.SessionId,
            7,
            AgentRunStatus.Completed,
            "Legacy checkpoint before durable runs.");

        const int writerCount = 8;
        using var startBarrier = new Barrier(writerCount + 1);
        var reservations = Enumerable.Range(0, writerCount)
            .Select(index => Task.Run(() =>
            {
                startBarrier.SignalAndWait();
                return store.ReserveRun(session.SessionId, "profile.test", $"message-{index}");
            }))
            .ToArray();

        startBarrier.SignalAndWait();
        var runs = await Task.WhenAll(reservations);

        Assert.Equal(writerCount, runs.Select(run => run.Key.RunId).Distinct().Count());
        Assert.Equal(
            Enumerable.Range(8, writerCount).Select(value => (long)value),
            runs.Select(run => run.Key.RunRevision).Order());
        Assert.All(runs, run => Assert.Equal(AgentDurableRunStatus.Preparing, run.Status));
    }

    [Fact]
    public void SaveCheckpoint_ProjectsRunAndSessionInSameTransaction()
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var session = CreateSession(store);
        var run = store.ReserveRun(session.SessionId, "profile.test", "project this run");

        var runningCheckpoint = store.SaveCheckpoint(
            session.SessionId,
            run.Key.RunRevision,
            AgentRunStatus.Running,
            "Running.");
        var runningRun = Assert.IsType<AgentDurableRunRecord>(store.GetRun(run.Key.RunId));

        Assert.Equal(AgentDurableRunStatus.Running, runningRun.Status);
        Assert.Equal(runningCheckpoint.CreatedAtUtc, runningRun.UpdatedAtUtc);
        Assert.Null(runningRun.FinishedAtUtc);
        Assert.Equal(AgentSessionState.Active, store.GetSession(session.SessionId)?.State);

        var completedCheckpoint = store.SaveCheckpoint(
            session.SessionId,
            run.Key.RunRevision,
            AgentRunStatus.Completed,
            "Completed.");
        var completedRun = Assert.IsType<AgentDurableRunRecord>(store.GetRun(run.Key.RunId));

        Assert.Equal(AgentDurableRunStatus.Completed, completedRun.Status);
        Assert.Equal(completedCheckpoint.CreatedAtUtc, completedRun.UpdatedAtUtc);
        Assert.Equal(completedCheckpoint.CreatedAtUtc, completedRun.FinishedAtUtc);
        Assert.Equal(AgentSessionState.Completed, store.GetSession(session.SessionId)?.State);
    }

    [Fact]
    public void SaveCheckpoint_WhenRunProjectionFailsRollsBackCheckpointAndSessionTouch()
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var session = CreateSession(store);
        var run = store.ReserveRun(session.SessionId, "profile.test", "fail projection");
        var sessionBefore = Assert.IsType<AgentSessionRecord>(store.GetSession(session.SessionId));

        using (var connection = OpenDatabase(store.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $$"""
                CREATE TRIGGER RejectAgentRunProjection
                BEFORE UPDATE OF Status ON AgentRuns
                WHEN OLD.RunId = '{{run.Key.RunId}}'
                BEGIN
                    SELECT RAISE(ABORT, 'projection rejected');
                END;
                """;
            command.ExecuteNonQuery();
        }

        Assert.Throws<SqliteException>(() => store.SaveCheckpoint(
            session.SessionId,
            run.Key.RunRevision,
            AgentRunStatus.Running,
            "Must roll back."));

        var persistedRun = Assert.IsType<AgentDurableRunRecord>(store.GetRun(run.Key.RunId));
        var sessionAfter = Assert.IsType<AgentSessionRecord>(store.GetSession(session.SessionId));
        Assert.Equal(AgentDurableRunStatus.Preparing, persistedRun.Status);
        Assert.Equal(sessionBefore.State, sessionAfter.State);
        Assert.Equal(sessionBefore.UpdatedAtUtc, sessionAfter.UpdatedAtUtc);

        using var verificationConnection = OpenDatabase(store.DatabasePath);
        using var verificationCommand = verificationConnection.CreateCommand();
        verificationCommand.CommandText = """
            SELECT COUNT(*)
            FROM AgentRunCheckpoints
            WHERE SessionId = $sessionId AND RunRevision = $runRevision;
            """;
        verificationCommand.Parameters.AddWithValue("$sessionId", session.SessionId.ToString());
        verificationCommand.Parameters.AddWithValue("$runRevision", run.Key.RunRevision);
        Assert.Equal(0L, Convert.ToInt64(verificationCommand.ExecuteScalar()));
    }

    [Fact]
    public void TerminalRun_RejectsStaleEpochAndEveryNonterminalTransition()
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var session = CreateSession(store);
        var run = store.ReserveRun(session.SessionId, "profile.test", "terminal fence");
        var running = Assert.IsType<AgentRunTransitionResult>(store.TryTransitionRun(
            run.Key,
            run.Epoch,
            AgentRunStatus.Running,
            "Running."));

        Assert.Null(store.TryTransitionRun(
            run.Key,
            run.Epoch,
            AgentRunStatus.Completed,
            "Stale epoch."));
        var completed = Assert.IsType<AgentRunTransitionResult>(store.TryTransitionRun(
            run.Key,
            running.Run.Epoch,
            AgentRunStatus.Completed,
            "Completed."));

        Assert.Null(store.TryTransitionRun(
            run.Key,
            completed.Run.Epoch,
            AgentRunStatus.Running,
            "Must not reopen."));
        Assert.Throws<InvalidOperationException>(() => store.SaveCheckpoint(
            session.SessionId,
            run.Key.RunRevision,
            AgentRunStatus.Running,
            "Legacy projection must not reopen either."));
        Assert.Equal(AgentDurableRunStatus.Completed, store.GetRun(run.Key.RunId)?.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartupRecovery_InterruptsUnownedOrdinaryActiveRun(bool transitionToRunning)
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var session = CreateSession(store);
        var run = store.ReserveRun(session.SessionId, "profile.test", "orphaned run");
        if (transitionToRunning)
        {
            Assert.NotNull(store.TryTransitionRun(
                run.Key,
                run.Epoch,
                AgentRunStatus.Running,
                "Running."));
        }

        var recovered = new AgentLocalStore(scope.Context);

        Assert.Equal(AgentDurableRunStatus.Interrupted, recovered.GetRun(run.Key.RunId)?.Status);
        var checkpoint = recovered.GetLatestCheckpoint(session.SessionId);
        Assert.Equal(AgentRunStatus.Interrupted, checkpoint?.Status);
        Assert.Equal(run.Key.RunRevision, checkpoint?.RunRevision);
    }

    [Fact]
    public void StartupRecovery_CompletesStreamingAssistantTurns()
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var session = CreateSession(store);
        var run = store.ReserveRun(session.SessionId, "profile.test", "orphaned stream");
        var running = Assert.IsType<AgentRunTransitionResult>(store.TryTransitionRun(
            run.Key,
            run.Epoch,
            AgentRunStatus.Running,
            "Running."));
        var turn = Assert.IsType<AgentTurnRecord>(store.TryAppendTextTurn(
            run.Key,
            running.Run.Epoch,
            AgentMessageRole.Assistant,
            "partial"));
        Assert.True(turn.IsStreaming);

        var recovered = new AgentLocalStore(scope.Context);

        var recoveredTurn = Assert.IsType<AgentTurnRecord>(recovered.GetTurn(turn.TurnId));
        Assert.False(recoveredTurn.IsStreaming);
        Assert.Equal(turn.ContentRevision + 1, recoveredTurn.ContentRevision);
    }

    [Fact]
    public void StopRun_CompletesStreamingAssistantTurns()
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var session = CreateSession(store);
        var run = store.ReserveRun(session.SessionId, "profile.test", "stopped stream");
        var running = Assert.IsType<AgentRunTransitionResult>(store.TryTransitionRun(
            run.Key,
            run.Epoch,
            AgentRunStatus.Running,
            "Running."));
        var turn = Assert.IsType<AgentTurnRecord>(store.TryAppendTextTurn(
            run.Key,
            running.Run.Epoch,
            AgentMessageRole.Assistant,
            "partial"));

        var stopped = Assert.IsType<AgentRunStopPersistenceResult>(store.TryStopRunAndActivePermissions(
            run.Key,
            running.Run.Epoch,
            "Stopped."));

        var completed = Assert.Single(stopped.CompletedStreamingTurns);
        Assert.Equal(turn.TurnId, completed.Turn.TurnId);
        Assert.Equal("partial".Length, completed.ContentLength);
        Assert.False(Assert.IsType<AgentTurnRecord>(store.GetTurn(turn.TurnId)).IsStreaming);
    }

    [Fact]
    public void TerminalTransition_CompletesOnlyTurnsOwnedByThatRun()
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var session = CreateSession(store);
        var firstRun = store.ReserveRun(session.SessionId, "profile.test", "first");
        var firstRunning = Assert.IsType<AgentRunTransitionResult>(store.TryTransitionRun(
            firstRun.Key,
            firstRun.Epoch,
            AgentRunStatus.Running,
            "First running."));
        var firstTurn = Assert.IsType<AgentTurnRecord>(store.TryAppendTextTurn(
            firstRun.Key,
            firstRunning.Run.Epoch,
            AgentMessageRole.Assistant,
            "first partial"));
        var secondRun = store.ReserveRun(session.SessionId, "profile.test", "second");
        var secondRunning = Assert.IsType<AgentRunTransitionResult>(store.TryTransitionRun(
            secondRun.Key,
            secondRun.Epoch,
            AgentRunStatus.Running,
            "Second running."));
        var secondTurn = Assert.IsType<AgentTurnRecord>(store.TryAppendTextTurn(
            secondRun.Key,
            secondRunning.Run.Epoch,
            AgentMessageRole.Assistant,
            "second partial"));

        var interrupted = Assert.IsType<AgentRunTransitionResult>(store.TryTransitionRun(
            firstRun.Key,
            firstRunning.Run.Epoch,
            AgentRunStatus.Interrupted,
            "First superseded."));

        var completed = Assert.Single(interrupted.CompletedStreamingTurns);
        Assert.Equal(firstTurn.TurnId, completed.Turn.TurnId);
        Assert.False(Assert.IsType<AgentTurnRecord>(store.GetTurn(firstTurn.TurnId)).IsStreaming);
        Assert.True(Assert.IsType<AgentTurnRecord>(store.GetTurn(secondTurn.TurnId)).IsStreaming);
    }

    [Fact]
    public async Task ReentrantStopFromTurnCallbackDoesNotDeadlockOrReorderMutations()
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var session = CreateSession(store);
        var service = new AgentSessionService(store);
        var reserved = store.ReserveRun(session.SessionId, "profile.test", "reentrant stop");
        var lease = new AgentDurableRunLease(reserved);
        Assert.NotNull(service.TryTransitionRun(
            lease,
            AgentRunStatus.Running,
            "Running."));
        var mutationKinds = new List<AgentTurnMutationKind>();
        var stopCompletedInsideCallback = false;
        Task<AgentRunTransitionResult?>? stop = null;
        service.TurnMutated += mutation => mutationKinds.Add(mutation.Kind);
        service.TurnChanged += (_, turn) =>
        {
            if (!turn.IsStreaming || stop is not null)
            {
                return;
            }

            stop = Task.Run(() => service.TryStopRun(lease, "Stopped from callback."));
            stopCompletedInsideCallback = SpinWait.SpinUntil(
                () => stop.IsCompleted,
                TimeSpan.FromSeconds(2));
        };

        var streamingTurn = service.AppendTextTurn(
            lease,
            AgentMessageRole.Assistant,
            "partial");

        Assert.True(stopCompletedInsideCallback);
        Assert.NotNull(await stop!);
        Assert.Equal(
            [AgentTurnMutationKind.Add, AgentTurnMutationKind.Complete],
            mutationKinds);
        Assert.False(Assert.IsType<AgentTurnRecord>(store.GetTurn(streamingTurn.TurnId)).IsStreaming);
    }

    [Fact]
    public void RollbackRunStart_NotifiesResetBeforeReplacementTurn()
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var session = CreateSession(store);
        var anchor = store.AppendTextTurn(
            session.SessionId,
            AgentMessageRole.User,
            "original request");
        store.AppendTextTurn(
            session.SessionId,
            AgentMessageRole.Assistant,
            "original response");
        var service = new AgentSessionService(store);
        var lease = new AgentDurableRunLease(
            store.ReserveRun(session.SessionId, "profile.test", "replacement request"));
        var notifications = new List<string>();
        service.TranscriptReset += _ => notifications.Add("reset");
        service.TurnMutated += mutation =>
        {
            if (mutation.Kind == AgentTurnMutationKind.Add)
            {
                notifications.Add("add");
            }
        };

        var result = service.TryStartRun(
            lease,
            "replacement request",
            [],
            anchor.TurnId,
            "Running.");

        Assert.NotNull(result);
        Assert.Equal(["reset", "add"], notifications);
        Assert.Equal("replacement request", Assert.Single(result!.UserTurn.Items).TextContent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FencedTranscriptInsert_RejectsRunCasBeforeTransaction(bool writeToolResult)
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var session = CreateSession(store);
        var run = store.ReserveRun(session.SessionId, "profile.test", "fenced write");
        var running = Assert.IsType<AgentRunTransitionResult>(store.TryTransitionRun(
            run.Key,
            run.Epoch,
            AgentRunStatus.Running,
            "Running."));
        var writeAttempted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeFencedTranscriptTransaction = _ =>
        {
            writeAttempted.TrySetResult();
            releaseWrite.Task.GetAwaiter().GetResult();
        };

        var write = Task.Run(() => writeToolResult
            ? store.TryAppendToolResultTurn(
                run.Key,
                running.Run.Epoch,
                "call-1",
                "test-tool",
                "{}",
                "stale result",
                "stale result",
                structuredPayloadJson: null,
                sourcesJson: null,
                wasTruncated: false,
                isError: false,
                errorCode: null,
                backendId: null)
            : store.TryAppendTextTurn(
                run.Key,
                running.Run.Epoch,
                AgentMessageRole.Assistant,
                "stale assistant"));
        await writeAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var interrupted = store.TryTransitionRun(
            run.Key,
            running.Run.Epoch,
            AgentRunStatus.Interrupted,
            "Superseded before transcript transaction.");
        releaseWrite.TrySetResult();

        Assert.NotNull(interrupted);
        Assert.Null(await write.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(store.ListTurns(session.SessionId));
    }

    [Fact]
    public async Task FencedRunStart_NewerReservationPreservesTranscriptAndPreparingState()
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var session = CreateSession(store);
        var anchor = store.AppendTextTurn(
            session.SessionId,
            AgentMessageRole.User,
            "original user turn");
        var response = store.AppendTextTurn(
            session.SessionId,
            AgentMessageRole.Assistant,
            "original response");
        var staleRun = store.ReserveRun(
            session.SessionId,
            "profile.test",
            "replacement user turn");
        var transactionAttempted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTransaction = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeFencedTranscriptTransaction = kind =>
        {
            if (kind != AgentTranscriptMutationKind.UserRunStart)
            {
                return;
            }

            transactionAttempted.TrySetResult();
            releaseTransaction.Task.GetAwaiter().GetResult();
        };

        var start = Task.Run(() => store.TryStartRun(
            staleRun.Key,
            staleRun.Epoch,
            "replacement user turn",
            [],
            anchor.TurnId,
            "Running."));
        await transactionAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var newerRun = store.ReserveRun(
            session.SessionId,
            "profile.test",
            "newer user turn");
        releaseTransaction.TrySetResult();

        Assert.Null(await start.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(
            [anchor.TurnId, response.TurnId],
            store.ListTurns(session.SessionId).Select(turn => turn.TurnId));
        Assert.Equal(AgentDurableRunStatus.Preparing, store.GetRun(staleRun.Key.RunId)?.Status);
        Assert.Equal(AgentDurableRunStatus.Preparing, store.GetRun(newerRun.Key.RunId)?.Status);
        Assert.Null(store.GetLatestCheckpoint(session.SessionId));
    }

    private static AgentSessionRecord CreateSession(AgentLocalStore store)
    {
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Durable run tests");
        return store.CreateSession("Session", workspaceId: workspace.WorkspaceId);
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
}

internal sealed class DurableRunTestScope : IDisposable
{
    private DurableRunTestScope(string rootPath)
    {
        RootPath = rootPath;
        Context = new DurableRunTestPackageContext(rootPath);
    }

    public string RootPath { get; }

    public IPackageContext Context { get; }

    public static DurableRunTestScope Create()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-agent-durable-run-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        return new DurableRunTestScope(rootPath);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(RootPath, recursive: true);
        }
        catch
        {
            // Cleanup must not hide an assertion failure.
        }
    }
}

internal sealed class DurableRunTestPackageContext(string rootPath) : IPackageContext
{
    public string PackageId => "test.package.agent.durable-runs";

    public string Version { get; } = "1.0.0";

    public string ContentRootPath => AppContext.BaseDirectory;

    public IPackageStorageContext Storage { get; } = new DurableRunTestStorageContext(rootPath);

    public IPackageSettings Settings { get; } = new DurableRunTestSettings();

    public IPackageSecrets Secrets { get; } = new DurableRunTestSecrets();


    public Sunder.Sdk.Logging.IPackageLogging Logging { get; } =
        Sunder.Sdk.Logging.NullPackageLogging.Instance;
}

internal sealed class DurableRunTestStorageContext : IPackageStorageContext
{
    public DurableRunTestStorageContext(string rootPath)
    {
        Directory.CreateDirectory(rootPath);
        Files = new DurableRunTestFileStore(Path.Combine(rootPath, "files"));
        RoleLocalWorkspace = new TestPackageRoleLocalWorkspace(rootPath);
    }

    public IPackageFileStore Files { get; }

    public IPackageKeyValueStore State { get; } = new DurableRunTestKeyValueStore();

    public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; }
}

internal sealed class DurableRunTestFileStore(string rootPath) : TestPackageFileStoreBase(rootPath);

internal sealed class DurableRunTestKeyValueStore : IPackageKeyValueStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(_values.GetValueOrDefault(key));

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(_values.ContainsKey(key));

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
    {
        _values.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(
        string? prefix = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(
            _values.Keys
                .Where(key => prefix is null || key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToArray());
}

internal sealed class DurableRunTestSettings : EmptyPackageSettings;

internal sealed class DurableRunTestSecrets : InMemoryPackageSecrets;
