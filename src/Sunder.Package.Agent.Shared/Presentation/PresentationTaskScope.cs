namespace Sunder.Package.Agent.Shared.Presentation;

internal sealed class PresentationTaskScope : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _operations = [];
    private readonly Action<Exception>? _failureHandler;
    private bool _disposed;

    public PresentationTaskScope(Action<Exception>? failureHandler = null)
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
                ReportFailure(ex);
                return;
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
            ReportFailure(ex);
        }
        finally
        {
            lock (_syncRoot)
            {
                _operations.Remove(task);
            }
        }
    }

    private void ReportFailure(Exception exception)
    {
        if (!_lifetime.IsCancellationRequested)
        {
            _failureHandler?.Invoke(exception);
        }
    }
}
