using System.Runtime.ExceptionServices;

namespace Sunder.Package.Agent.Shared.Presentation;

internal sealed class AsyncOnce : IDisposable, IAsyncDisposable
{
    private readonly object _syncRoot = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _cancellationToken;
    private readonly AsyncLocal<OperationRegistration?> _operationScope = new();
    private readonly HashSet<OperationRegistration> _operations = [];
    private OperationRegistration? _operation;
    private Task? _disposal;
    private bool _disposed;

    public AsyncOnce()
    {
        _cancellationToken = _lifetime.Token;
    }

    public Task RunAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Task sharedOperation;
        OperationRegistration? registrationToStart = null;
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_operation is null)
            {
                var registration = new OperationRegistration();
                _operation = registration;
                _operations.Add(registration);
                _ = ObserveOperationAsync(registration);
                registrationToStart = registration;
                sharedOperation = registration.Task;
            }
            else
            {
                sharedOperation = _operation.Task;
            }
        }

        if (registrationToStart is not null)
        {
            StartOperation(registrationToStart, operation);
        }

        return cancellationToken.CanBeCanceled
            ? sharedOperation.WaitAsync(cancellationToken)
            : sharedOperation;
    }

    private void StartOperation(
        OperationRegistration registration,
        Func<CancellationToken, Task> operation)
    {
        Task operationTask;
        var previousOperation = _operationScope.Value;
        _operationScope.Value = registration;
        try
        {
            operationTask = operation(_cancellationToken)
                ?? Task.FromException(new InvalidOperationException(
                    "An async-once operation returned a null task."));
        }
        catch (Exception exception)
        {
            operationTask = Task.FromException(exception);
        }
        finally
        {
            _operationScope.Value = previousOperation;
        }

        _ = CompleteOperationAsync(registration, operationTask);
    }

    private async Task CompleteOperationAsync(
        OperationRegistration registration,
        Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
            registration.Completion.TrySetResult();
        }
        catch (OperationCanceledException exception) when (
            !PresentationExceptionPolicy.IsFatal(exception))
        {
            ResetFailedOperation(registration);
            registration.Completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            ResetFailedOperation(registration);
            registration.Completion.TrySetException(exception);
        }
    }

    private void ResetFailedOperation(OperationRegistration registration)
    {
        lock (_syncRoot)
        {
            if (ReferenceEquals(_operation, registration))
            {
                _operation = null;
            }
        }
    }

    private async Task ObserveOperationAsync(OperationRegistration registration)
    {
        try
        {
            await registration.Task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The shared task remains faulted for callers; this observation prevents abandonment.
            if (PresentationExceptionPolicy.IsFatal(exception))
            {
                ThreadPool.QueueUserWorkItem(
                    static state => ExceptionDispatchInfo.Capture((Exception)state!).Throw(),
                    exception);
            }
        }
        finally
        {
            lock (_syncRoot)
            {
                _operations.Remove(registration);
            }
        }
    }

    public void Dispose()
    {
        var disposal = BeginDispose();
        if (!disposal.IsCompletedSuccessfully)
        {
            _ = ObserveDisposalAsync(disposal);
        }
    }

    public ValueTask DisposeAsync()
    {
        var disposal = BeginDispose();
        if (_operationScope.Value is { Task.IsCompleted: false })
        {
            if (!disposal.IsCompletedSuccessfully)
            {
                _ = ObserveDisposalAsync(disposal);
            }
            return ValueTask.CompletedTask;
        }
        return new ValueTask(disposal);
    }

    private Task BeginDispose()
    {
        TaskCompletionSource completion;
        Task[] operations;
        lock (_syncRoot)
        {
            if (_disposal is not null)
            {
                return _disposal;
            }

            _disposed = true;
            operations = _operations.Select(static registration => registration.Task).ToArray();
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposal = completion.Task;
        }

        Exception? cancellationFailure = null;
        try
        {
            _lifetime.Cancel();
        }
        catch (Exception exception)
        {
            cancellationFailure = exception;
        }
        finally
        {
            _ = CompleteDisposalAsync(operations, completion, cancellationFailure);
        }
        return completion.Task;
    }

    private async Task CompleteDisposalAsync(
        IReadOnlyList<Task> operations,
        TaskCompletionSource completion,
        Exception? cancellationFailure)
    {
        try
        {
            await Task.WhenAll(operations).ConfigureAwait(false);
        }
        catch
        {
            // Callers retain the original shared task and its failure.
        }
        finally
        {
            _lifetime.Dispose();
        }
        if (cancellationFailure is null)
        {
            completion.TrySetResult();
        }
        else
        {
            completion.TrySetException(cancellationFailure);
        }
    }

    private static async Task ObserveDisposalAsync(Task disposal)
    {
        try
        {
            await disposal.ConfigureAwait(false);
        }
        catch
        {
            ThreadPool.QueueUserWorkItem(
                static state => ExceptionDispatchInfo.Capture((Exception)state!).Throw(),
                disposal.Exception?.GetBaseException()
                ?? new InvalidOperationException("Async-once disposal failed."));
        }
    }

    private sealed class OperationRegistration
    {
        public TaskCompletionSource Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Task => Completion.Task;
    }
}
