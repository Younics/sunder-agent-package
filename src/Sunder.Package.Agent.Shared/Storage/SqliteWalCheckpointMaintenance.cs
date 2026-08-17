using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Sunder.Package.Agent.Shared.Storage;

internal sealed class SqliteWalCheckpointMaintenance : IDisposable, IAsyncDisposable
{
    private const int DefaultMaximumAttempts = 20;
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DefaultMaximumRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultFailureLogInterval = TimeSpan.FromMinutes(1);

    private readonly object _syncRoot = new();
    private readonly string _databasePath;
    private readonly string _storeName;
    private readonly ILogger _logger;
    private readonly int _maximumAttempts;
    private readonly TimeSpan _retryDelay;
    private readonly TimeSpan _maximumRetryDelay;
    private readonly TimeSpan _failureLogInterval;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _retrySignal = new(0, 1);
    private TaskCompletionSource<SqliteWalCheckpointAttempt> _nextAttempt = CreateAttemptCompletion();
    private Task _worker = Task.CompletedTask;
    private long _requestedGeneration;
    private int _attemptCount;
    private bool _lastAttemptSucceeded;
    private bool _running;
    private bool _disposed;
    private long? _lastFailureLogTimestamp;

    public SqliteWalCheckpointMaintenance(
        string databasePath,
        string storeName,
        ILogger logger,
        int maximumAttempts = DefaultMaximumAttempts,
        TimeSpan? retryDelay = null,
        TimeSpan? maximumRetryDelay = null,
        TimeSpan? failureLogInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);
        ArgumentNullException.ThrowIfNull(logger);
        if (maximumAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        _databasePath = Path.GetFullPath(databasePath);
        _storeName = storeName;
        _logger = logger;
        _maximumAttempts = maximumAttempts;
        _retryDelay = retryDelay ?? DefaultRetryDelay;
        _maximumRetryDelay = maximumRetryDelay ?? DefaultMaximumRetryDelay;
        _failureLogInterval = failureLogInterval ?? DefaultFailureLogInterval;
        if (_retryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
        }
        if (_maximumRetryDelay < _retryDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRetryDelay));
        }
        if (_failureLogInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(failureLogInterval));
        }
    }

    internal int AttemptCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _attemptCount;
            }
        }
    }

    public void ScheduleCheckpoint()
    {
        var wakeWorker = false;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _requestedGeneration++;
            if (_running)
            {
                wakeWorker = true;
            }
            else
            {
                _running = true;
                try
                {
                    _worker = Task.Run(() => RunAsync(_lifetime.Token));
                }
                catch (Exception exception)
                {
                    _running = false;
                    TryLogFailure(exception, attemptCount: 0);
                }
            }
        }

        if (wakeWorker)
        {
            TryWakeWorker();
        }
    }

    internal async Task<bool> WaitForAttemptAfterAsync(
        int attemptCount,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task<SqliteWalCheckpointAttempt> nextAttempt;
            lock (_syncRoot)
            {
                if (_attemptCount > attemptCount)
                {
                    return _lastAttemptSucceeded;
                }
                nextAttempt = _nextAttempt.Task;
            }

            var attempt = await nextAttempt.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (attempt.Count > attemptCount)
            {
                return attempt.Succeeded;
            }
        }
    }

    internal async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task worker;
            lock (_syncRoot)
            {
                if (!_running)
                {
                    return;
                }
                worker = _worker;
            }

            await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task worker;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            worker = _worker;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        TryWakeWorker();
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _retrySignal.Dispose();
            _lifetime.Dispose();
        }
    }

    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        var failedAttempts = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long targetGeneration;
                lock (_syncRoot)
                {
                    targetGeneration = _requestedGeneration;
                }

                bool succeeded;
                try
                {
                    succeeded = TryCheckpoint();
                    lastFailure = succeeded
                        ? null
                        : new InvalidOperationException("SQLite reported an active WAL reader.");
                }
                catch (Exception exception)
                {
                    succeeded = false;
                    lastFailure = exception;
                }
                RecordAttempt(succeeded);

                if (succeeded)
                {
                    failedAttempts = 0;
                    lock (_syncRoot)
                    {
                        if (_requestedGeneration <= targetGeneration)
                        {
                            _running = false;
                            return;
                        }
                    }
                    continue;
                }

                failedAttempts++;
                var hasNewRequest = false;
                lock (_syncRoot)
                {
                    hasNewRequest = _requestedGeneration > targetGeneration;
                }
                if (hasNewRequest)
                {
                    failedAttempts = 0;
                }
                else if (failedAttempts >= _maximumAttempts)
                {
                    TryLogFailureRateLimited(lastFailure, failedAttempts);
                }

                await _retrySignal.WaitAsync(
                    GetRetryDelay(failedAttempts),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_syncRoot)
            {
                _running = false;
            }
        }
        catch (Exception exception)
        {
            lock (_syncRoot)
            {
                _running = false;
            }
            TryLogFailureRateLimited(exception, failedAttempts);
        }
    }

    private TimeSpan GetRetryDelay(int failedAttempts)
    {
        if (failedAttempts < _maximumAttempts)
        {
            return _retryDelay;
        }

        var delayTicks = Math.Max(_retryDelay.Ticks, TimeSpan.FromMilliseconds(1).Ticks);
        var maximumTicks = _maximumRetryDelay.Ticks;
        var doublings = Math.Min(failedAttempts - _maximumAttempts + 1, 62);
        for (var index = 0; index < doublings && delayTicks < maximumTicks; index++)
        {
            delayTicks = delayTicks > maximumTicks / 2
                ? maximumTicks
                : delayTicks * 2;
        }
        return TimeSpan.FromTicks(Math.Min(delayTicks, maximumTicks));
    }

    private bool TryCheckpoint()
    {
        if (!File.Exists(_databasePath + "-wal"))
        {
            return true;
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = false,
            Mode = SqliteOpenMode.ReadWrite,
            DefaultTimeout = 1,
        }.ToString());
        connection.Open();
        using (var timeout = connection.CreateCommand())
        {
            timeout.CommandText = "PRAGMA busy_timeout = 0;";
            timeout.ExecuteNonQuery();
        }
        using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using var reader = checkpoint.ExecuteReader();
        return reader.Read() && reader.GetInt64(0) == 0;
    }

    private void RecordAttempt(bool succeeded)
    {
        TaskCompletionSource<SqliteWalCheckpointAttempt> completion;
        SqliteWalCheckpointAttempt attempt;
        lock (_syncRoot)
        {
            attempt = new SqliteWalCheckpointAttempt(++_attemptCount, succeeded);
            _lastAttemptSucceeded = succeeded;
            completion = _nextAttempt;
            _nextAttempt = CreateAttemptCompletion();
        }
        completion.TrySetResult(attempt);
    }

    private void TryWakeWorker()
    {
        try
        {
            _retrySignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void TryLogFailureRateLimited(Exception? exception, int attemptCount)
    {
        var now = Environment.TickCount64;
        lock (_syncRoot)
        {
            if (_lastFailureLogTimestamp is { } previous
                && TimeSpan.FromMilliseconds(now - previous) < _failureLogInterval)
            {
                return;
            }
            _lastFailureLogTimestamp = now;
        }
        TryLogFailure(exception, attemptCount);
    }

    private void TryLogFailure(Exception? exception, int attemptCount)
    {
        try
        {
            _logger.LogWarning(
                exception,
                "{StoreName} WAL truncation remains pending after {AttemptCount} consecutive attempts; maintenance will retry automatically.",
                _storeName,
                attemptCount);
        }
        catch
        {
            // Logging must not fault maintenance or a committed operation.
        }
    }

    private static TaskCompletionSource<SqliteWalCheckpointAttempt> CreateAttemptCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly record struct SqliteWalCheckpointAttempt(int Count, bool Succeeded);
}
