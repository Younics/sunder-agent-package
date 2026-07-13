namespace Sunder.Package.Agent.Shared.Presentation;

internal sealed class AsyncOnce : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _operation;
    private bool _disposed;

    public Task RunAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Task sharedOperation;
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            sharedOperation = _operation ??= operation(_lifetime.Token);
        }

        var retryableOperation = AwaitAndResetOnFailureAsync(sharedOperation);
        return cancellationToken.CanBeCanceled
            ? retryableOperation.WaitAsync(cancellationToken)
            : retryableOperation;
    }

    private async Task AwaitAndResetOnFailureAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch
        {
            lock (_syncRoot)
            {
                if (ReferenceEquals(_operation, operation))
                {
                    _operation = null;
                }
            }

            throw;
        }
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
}
