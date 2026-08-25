using System.Runtime.ExceptionServices;

namespace Sunder.Package.Agent.Memory.Semantic.Services;

public sealed partial class SemanticMemoryIndexingBackgroundService
{
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AbandonWorkerWait();
            throw;
        }

        try
        {
            if (!_prepared
                && _generation is null
                && !_active
                && !_workersStarted
                && _processingTask is null
                && _monitoringTask is null)
            {
                return;
            }

            if (_workersStarted || _processingTask is not null || _monitoringTask is not null)
            {
                await StopWorkersAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                ResetQueue();
                UpdateStatus(status => status with { IsRunning = false, PendingItemCount = 0 });
            }
            _prepared = false;
            _active = false;
            _workersStarted = false;
            _generation = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AbandonWorkerWait();
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        Exception? stopFailure = null;
        if (!_workerWaitAbandoned)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await StopAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (deadline.IsCancellationRequested)
            {
                AbandonWorkerWait();
                stopFailure = new TimeoutException(
                    "Semantic memory indexing did not stop before its disposal deadline.",
                    exception);
            }
            catch (Exception exception)
            {
                stopFailure = exception;
            }
        }

        _disposed = true;
        lock (_queueSync)
        {
            _requests.Writer.TryComplete();
            _scheduled.Clear();
            _retryNotBefore.Clear();
        }
        if (!_workerWaitAbandoned)
        {
            _lifecycleGate.Dispose();
        }
        GC.SuppressFinalize(this);
        if (stopFailure is not null)
        {
            ExceptionDispatchInfo.Capture(stopFailure).Throw();
        }
    }

    private async Task StopWorkersAsync(CancellationToken cancellationToken)
    {
        lock (_queueSync)
        {
            _requests.Writer.TryComplete();
        }

        TryCancelStoppingWorkers();
        var tasks = new[] { _processingTask, _monitoringTask }
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        var completion = Task.WhenAll(tasks);
        try
        {
            await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (completion.IsCompleted)
            {
                _stoppingCts?.Dispose();
                _stoppingCts = null;
                _processingTask = null;
                _monitoringTask = null;
                Volatile.Write(ref _generationCompletion, Task.CompletedTask);
                _workersStarted = false;
                _workerWaitAbandoned = false;
                ResetQueue();
                UpdateStatus(status => status with { IsRunning = false, PendingItemCount = 0 });
            }
        }
    }

    private void AbandonWorkerWait()
    {
        if (_workerWaitAbandoned)
        {
            return;
        }

        _workerWaitAbandoned = true;
        TryCancelStoppingWorkers();
        _metricsService.RecordWorkerFailure();
        UpdateStatus(status => status with
        {
            LastFailureAtUtc = DateTimeOffset.UtcNow,
            LastFailureMessage = "Semantic memory indexing did not stop before the shutdown deadline.",
        });
        var tasks = new[] { _processingTask, _monitoringTask }
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (tasks.Length > 0 && _abandonedWorkerObservation.IsCompleted)
        {
            _abandonedWorkerObservation = ObserveAbandonedWorkersAsync(Task.WhenAll(tasks));
        }
    }

    private async Task ObserveAbandonedWorkersAsync(Task completion)
    {
        Exception? failure = null;
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (failure is not null)
            {
                _metricsService.RecordWorkerFailure();
            }
            UpdateStatus(status => status with
            {
                IsRunning = false,
                PendingItemCount = 0,
                LastFailureAtUtc = failure is null ? status.LastFailureAtUtc : DateTimeOffset.UtcNow,
                LastFailureMessage = failure is null
                    ? status.LastFailureMessage
                    : $"Semantic memory indexing failed during shutdown ({failure.GetType().Name}).",
            });
            _stoppingCts?.Dispose();
        }
    }

    private void TryCancelStoppingWorkers()
    {
        try
        {
            _stoppingCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
