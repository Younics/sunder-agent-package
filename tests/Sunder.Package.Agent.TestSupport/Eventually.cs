namespace Sunder.Package.Agent.TestSupport;

public static class Eventually
{
    public static async Task<T> ObserveAsync<T>(
        Func<T> observe,
        Func<T, bool> predicate,
        TimeSpan? timeout = null,
        TimeSpan? interval = null,
        Func<T, string>? describe = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observe);
        ArgumentNullException.ThrowIfNull(predicate);
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(5);
        var effectiveInterval = interval ?? TimeSpan.FromMilliseconds(10);
        if (effectiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        if (effectiveInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        var clock = timeProvider ?? TimeProvider.System;
        var started = clock.GetTimestamp();
        T lastObserved;
        Exception? lastException = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                lastObserved = observe();
                lastException = null;
                if (predicate(lastObserved))
                {
                    return lastObserved;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastObserved = default!;
                lastException = exception;
            }

            if (clock.GetElapsedTime(started) >= effectiveTimeout)
            {
                var state = lastException is null
                    ? describe?.Invoke(lastObserved) ?? lastObserved?.ToString() ?? "<null>"
                    : $"observation threw {lastException.GetType().Name}: {lastException.Message}";
                throw new TimeoutException(
                    $"Condition was not observed within {effectiveTimeout}. Last observed state: {state}",
                    lastException);
            }

            await Task.Delay(effectiveInterval, clock, cancellationToken).ConfigureAwait(false);
        }
    }

    public static Task<bool> IsTrueAsync(
        Func<bool> predicate,
        string description,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => ObserveAsync(
            predicate,
            static value => value,
            timeout,
            describe: value => $"{description}={value}",
            cancellationToken: cancellationToken);
}
