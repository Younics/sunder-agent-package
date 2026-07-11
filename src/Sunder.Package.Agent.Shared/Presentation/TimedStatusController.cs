namespace Sunder.Package.Agent.Shared.Presentation;

internal sealed class TimedStatusController : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly TimeProvider _timeProvider;
    private readonly IPresentationDispatcher _dispatcher;
    private CancellationTokenSource? _cancellation;
    private long _generation;
    private bool _disposed;

    public TimedStatusController(
        TimeProvider? timeProvider = null,
        IPresentationDispatcher? dispatcher = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _dispatcher = dispatcher ?? PresentationDispatcher.Capture();
    }

    public Task ScheduleAsync(TimeSpan delay, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        CancellationTokenSource? previous;
        CancellationTokenSource current;
        long generation;
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _cancellation;
            current = new CancellationTokenSource();
            _cancellation = current;
            generation = ++_generation;
        }

        CancelAndDispose(previous);
        return RunAsync(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, action, current, generation);
    }

    public void Cancel()
    {
        CancellationTokenSource? cancellation;
        lock (_syncRoot)
        {
            cancellation = _cancellation;
            _cancellation = null;
            _generation++;
        }

        CancelAndDispose(cancellation);
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

        Cancel();
    }

    private async Task RunAsync(
        TimeSpan delay,
        Action action,
        CancellationTokenSource cancellation,
        long generation)
    {
        try
        {
            await Task.Delay(delay, _timeProvider, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                lock (_syncRoot)
                {
                    if (_disposed
                        || generation != _generation
                        || !ReferenceEquals(_cancellation, cancellation)
                        || cancellation.IsCancellationRequested)
                    {
                        return;
                    }

                    _cancellation = null;
                }

                action();
            }).ConfigureAwait(false);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private static void CancelAndDispose(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        finally
        {
            cancellation.Dispose();
        }
    }
}
