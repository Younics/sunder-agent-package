namespace Sunder.Package.Agent.Shared.Presentation;

internal sealed class PresentationTaskScope : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _operations = [];
    private readonly Func<Exception, Task>? _failureHandler;
    private bool _disposed;

    public PresentationTaskScope(Func<Exception, Task>? failureHandler = null)
    {
        _failureHandler = failureHandler;
    }

    public CancellationToken CancellationToken => _lifetime.Token;

    public void Run(Func<CancellationToken, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Task task;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                task = operation(_lifetime.Token);
            }
            catch (Exception ex)
            {
                task = Task.FromException(ex);
            }

            _operations.Add(task);
        }

        _ = ObserveAsync(task);
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

    private async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await ReportFailureAsync(ex).ConfigureAwait(false);
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
        if (!_lifetime.IsCancellationRequested && _failureHandler is not null)
        {
            await _failureHandler(exception).ConfigureAwait(false);
        }
    }
}
