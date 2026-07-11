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
        command.CommandText = "SELECT Version, Name FROM SchemaMigrations ORDER BY Version;";
        using var reader = command.ExecuteReader();
        var migrations = new List<(long Version, string Name)>();
        while (reader.Read())
        {
            migrations.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        Assert.Equal(
            [
                (1L, "legacy-schema-baseline"),
                (2L, "agent-runs"),
                (3L, "pending-permission-state"),
                (4L, "typed-run-suspensions"),
                (5L, "permission-claim-recovery"),
                (6L, "parent-continuation-work"),
            ],
            migrations);
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

    public string InstallPath => AppContext.BaseDirectory;

    public IPackageStorageContext Storage { get; } = new DurableRunTestStorageContext(rootPath);

    public IPackageConfiguration Configuration { get; } = new DurableRunTestConfiguration();

    public IPackageSecrets Secrets { get; } = new DurableRunTestSecrets();

    public Microsoft.Extensions.Logging.ILoggerFactory LoggerFactory => Logging.LoggerFactory;

    public Sunder.Sdk.Logging.IPackageLogging Logging { get; } =
        Sunder.Sdk.Logging.NullPackageLogging.Instance;
}

internal sealed class DurableRunTestStorageContext : IPackageStorageContext
{
    public DurableRunTestStorageContext(string rootPath)
    {
        Directory.CreateDirectory(rootPath);
        Files = new DurableRunTestFileStore(Path.Combine(rootPath, "files"));
        LocalWorkspace = new TestPackageWorkspaceLease(rootPath);
    }

    public IPackageFileStore Files { get; }

    public IPackageKeyValueStore State { get; } = new DurableRunTestKeyValueStore();

    public IPackageLocalWorkspaceLease LocalWorkspace { get; }
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

internal sealed class DurableRunTestConfiguration : EmptyPackageConfiguration;

internal sealed class DurableRunTestSecrets : InMemoryPackageSecrets;
