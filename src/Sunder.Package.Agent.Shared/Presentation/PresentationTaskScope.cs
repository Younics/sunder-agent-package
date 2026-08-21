using System.Runtime.ExceptionServices;

namespace Sunder.Package.Agent.Shared.Presentation;

internal sealed class PresentationTaskScope : IDisposable, IAsyncDisposable
{
    private readonly object _syncRoot = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _cancellationToken;
    private readonly HashSet<OperationRegistration> _operations = [];
    private readonly Func<Exception, Task> _failureHandler;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;
    private bool _cancellationCompleted;
    private bool _lifetimeFinalizationClaimed;

    public PresentationTaskScope(Func<Exception, Task> failureHandler)
    {
        ArgumentNullException.ThrowIfNull(failureHandler);
        _failureHandler = failureHandler;
        _cancellationToken = _lifetime.Token;
    }

    public static Task IgnoreExpectedFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Task.CompletedTask;
    }

    public static Task PropagateExpectedFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Task.FromException(exception);
    }

    public CancellationToken CancellationToken => _cancellationToken;

    public void Run(Func<CancellationToken, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Start(operation, reportUnhandledFailure: true, SynchronizationContext.Current);
    }

    public Task Track(Func<CancellationToken, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return Start(operation, reportUnhandledFailure: true, SynchronizationContext.Current);
    }

    public Task RunAsync(Func<CancellationToken, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return Start(operation, reportUnhandledFailure: false, synchronizationContext: null);
    }

    private Task Start(
        Func<CancellationToken, Task> operation,
        bool reportUnhandledFailure,
        SynchronizationContext? synchronizationContext)
    {
        var registration = new OperationRegistration();
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            _operations.Add(registration);
        }

        Task task;
        try
        {
            task = operation(_cancellationToken)
                ?? Task.FromException(new InvalidOperationException("A presentation operation returned a null task."));
        }
        catch (Exception ex)
        {
            task = Task.FromException(ex);
        }

        var observation = ObserveAsync(task, _cancellationToken);
        registration.DrainTask = DrainObservationAsync(
            registration,
            observation,
            reportUnhandledFailure,
            synchronizationContext);
        return observation;
    }

    public void Dispose()
        => _ = BeginDispose();

    public async ValueTask DisposeAsync()
        => await BeginDispose().ConfigureAwait(false);

    private Task BeginDispose()
    {
        var cancel = false;
        lock (_syncRoot)
        {
            if (!_disposed)
            {
                _disposed = true;
                cancel = true;
            }

            if (!cancel)
            {
                return _drained.Task;
            }
        }

        try
        {
            // Cancellation callbacks can invoke application code, so cancellation stays outside the scope lock.
            _lifetime.Cancel();
        }
        finally
        {
            CompleteCancellation();
        }

        return _drained.Task;
    }

    private async Task ObserveAsync(Task task, CancellationToken cancellationToken)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            cancellationToken.IsCancellationRequested
            && !PresentationExceptionPolicy.IsFatal(exception))
        {
        }
        catch (Exception ex)
        {
            if (!PresentationExceptionPolicy.IsExpectedOperationFailure(ex))
            {
                throw;
            }

            await _failureHandler(ex).ConfigureAwait(false);
        }
    }

    private async Task DrainObservationAsync(
        OperationRegistration registration,
        Task observation,
        bool reportUnhandledFailure,
        SynchronizationContext? synchronizationContext)
    {
        try
        {
            await observation.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (reportUnhandledFailure)
            {
                DispatchUnhandledFailure(exception, synchronizationContext);
            }
        }
        finally
        {
            CompleteOperation(registration);
        }
    }

    private static void DispatchUnhandledFailure(
        Exception exception,
        SynchronizationContext? synchronizationContext)
    {
        if (synchronizationContext is not null)
        {
            try
            {
                synchronizationContext.Post(
                    static state => ExceptionDispatchInfo.Capture((Exception)state!).Throw(),
                    exception);
                return;
            }
            catch (Exception dispatchException)
            {
                exception = new AggregateException(
                    "The captured synchronization context could not report a presentation failure.",
                    exception,
                    dispatchException);
            }
        }

        ThreadPool.QueueUserWorkItem(
            static state => ExceptionDispatchInfo.Capture((Exception)state!).Throw(),
            exception);
    }

    private void CompleteCancellation()
    {
        var finalizeLifetime = false;
        lock (_syncRoot)
        {
            _cancellationCompleted = true;
            finalizeLifetime = TryClaimLifetimeFinalization();
        }

        if (finalizeLifetime)
        {
            FinalizeLifetime();
        }
    }

    private void CompleteOperation(OperationRegistration registration)
    {
        var finalizeLifetime = false;
        lock (_syncRoot)
        {
            _operations.Remove(registration);
            finalizeLifetime = TryClaimLifetimeFinalization();
        }

        if (finalizeLifetime)
        {
            FinalizeLifetime();
        }
    }

    private bool TryClaimLifetimeFinalization()
    {
        if (!_disposed
            || !_cancellationCompleted
            || _operations.Count != 0
            || _lifetimeFinalizationClaimed)
        {
            return false;
        }

        _lifetimeFinalizationClaimed = true;
        return true;
    }

    private void FinalizeLifetime()
    {
        _lifetime.Dispose();
        _drained.TrySetResult();
    }

    private sealed class OperationRegistration
    {
        public Task DrainTask { get; set; } = Task.CompletedTask;
    }
}
