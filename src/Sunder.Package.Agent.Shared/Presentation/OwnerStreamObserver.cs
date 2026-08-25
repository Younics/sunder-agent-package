namespace Sunder.Package.Agent.Shared.Presentation;

internal interface IOwnerStreamAvailability
{
    bool IsOwnerStreamAvailable { get; }
    event Action<bool>? OwnerStreamAvailabilityChanged;
}

internal sealed class OwnerStreamObserver
{
    private static readonly TimeSpan MinimumRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(5);
    private readonly Action<Exception, TimeSpan>? _retrying;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Func<double> _nextJitter;
    private int _isAvailable;

    internal OwnerStreamObserver(Action<Exception, TimeSpan>? retrying = null)
        : this(
            retrying,
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken),
            static () => Random.Shared.NextDouble())
    {
    }

    internal OwnerStreamObserver(
        Action<Exception, TimeSpan>? retrying,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Func<double> nextJitter)
    {
        _retrying = retrying;
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
        _nextJitter = nextJitter ?? throw new ArgumentNullException(nameof(nextJitter));
    }

    internal bool IsAvailable => Volatile.Read(ref _isAvailable) != 0;

    internal event Action<bool>? AvailabilityChanged;

    internal async Task RunAsync(
        Func<Action, CancellationToken, Task> observeOnceAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observeOnceAsync);
        var consecutiveFailures = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Exception failure;
                try
                {
                    await observeOnceAsync(MarkAvailable, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    failure = new EndOfStreamException("The owner change stream ended.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (!PresentationExceptionPolicy.IsFatal(exception))
                {
                    failure = exception;
                }

                SetAvailability(false);
                var retryDelay = CalculateRetryDelay(consecutiveFailures++, _nextJitter());
                try
                {
                    _retrying?.Invoke(failure, retryDelay);
                }
                catch (Exception exception) when (!PresentationExceptionPolicy.IsFatal(exception))
                {
                    // A logging sink failure must not disable owner observation.
                }

                try
                {
                    await _delayAsync(retryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                void MarkAvailable()
                {
                    consecutiveFailures = 0;
                    SetAvailability(true);
                }
            }
        }
        finally
        {
            SetAvailability(false);
        }
    }

    internal static TimeSpan CalculateRetryDelay(int consecutiveFailures, double jitter)
    {
        var exponent = Math.Clamp(consecutiveFailures, 0, 4);
        var baseMilliseconds = MinimumRetryDelay.TotalMilliseconds * (1 << exponent);
        var jitterFactor = 0.8 + (Math.Clamp(jitter, 0, 1) * 0.4);
        return TimeSpan.FromMilliseconds(Math.Min(
            MaximumRetryDelay.TotalMilliseconds,
            baseMilliseconds * jitterFactor));
    }

    private void SetAvailability(bool available)
    {
        var value = available ? 1 : 0;
        if (Interlocked.Exchange(ref _isAvailable, value) != value)
        {
            var handlers = AvailabilityChanged;
            if (handlers is null)
            {
                return;
            }

            foreach (Action<bool> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(available);
                }
                catch (Exception exception) when (!PresentationExceptionPolicy.IsFatal(exception))
                {
                    // Availability listeners are independent presentation hints.
                }
            }
        }
    }
}
