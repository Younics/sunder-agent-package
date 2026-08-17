namespace Sunder.Package.Agent.Shared.Presentation;

internal sealed class PresentationTaskScope : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _cancellationToken;
    private readonly HashSet<Task> _operations = [];
    private readonly Func<Exception, Task>? _failureHandler;
    private bool _disposed;

    public PresentationTaskScope(Func<Exception, Task>? failureHandler = null)
    {
        _failureHandler = failureHandler;
        _cancellationToken = _lifetime.Token;
    }

    public CancellationToken CancellationToken => _cancellationToken;

    public void Run(Func<CancellationToken, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        CancellationToken cancellationToken;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            cancellationToken = _cancellationToken;
        }

        Task task;
        try
        {
            task = operation(cancellationToken);
        }
        catch (Exception ex)
        {
            task = Task.FromException(ex);
        }

        lock (_syncRoot)
        {
            _operations.Add(task);
        }

        OnTaskCompletedAsync(task, cancellationToken);
    }

    public void Run(Task operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Run(_ => operation);
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async void OnTaskCompletedAsync(Task task, CancellationToken cancellationToken)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (PresentationExceptionPolicy.IsFatal(ex))
            {
                throw;
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                await ReportFailureAsync(ex).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_syncRoot)
            {
                _operations.Remove(task);
            }
        }
    }

    private async Task ReportFailureAsync(Exception exception)
    {
        if (_failureHandler is null)
        {
            throw exception;
        }

        try
        {
            await _failureHandler(exception).ConfigureAwait(false);
        }
        catch (Exception handlerException) when (!PresentationExceptionPolicy.IsFatal(handlerException))
        {
            // The handler failure is observed here so failure reporting cannot recurse.
        }
    }
}
