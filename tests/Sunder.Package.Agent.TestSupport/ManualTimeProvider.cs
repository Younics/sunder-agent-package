namespace Sunder.Package.Agent.TestSupport;

public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _syncRoot = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow;

    public ManualTimeProvider(DateTimeOffset? utcNow = null)
    {
        _utcNow = utcNow ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_syncRoot)
        {
            return _utcNow;
        }
    }

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        lock (_syncRoot)
        {
            _timers.Add(timer);
            timer.ChangeCore(_utcNow, dueTime, period);
        }
        return timer;
    }

    public void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        var target = GetUtcNow() + amount;
        while (true)
        {
            ManualTimer[] due;
            lock (_syncRoot)
            {
                var next = _timers
                    .Where(static timer => timer.IsScheduled)
                    .MinBy(static timer => timer.DueAtUtc);
                if (next is null || next.DueAtUtc > target)
                {
                    _utcNow = target;
                    return;
                }

                _utcNow = next.DueAtUtc;
                due = _timers
                    .Where(timer => timer.IsScheduled && timer.DueAtUtc <= _utcNow)
                    .ToArray();
                foreach (var timer in due)
                {
                    timer.RescheduleCore(_utcNow);
                }
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_syncRoot)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(
        ManualTimeProvider owner,
        TimerCallback callback,
        object? state) : ITimer
    {
        private bool _disposed;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset DueAtUtc { get; private set; } = DateTimeOffset.MaxValue;

        public bool IsScheduled => !_disposed && DueAtUtc != DateTimeOffset.MaxValue;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._syncRoot)
            {
                if (_disposed)
                {
                    return false;
                }
                ChangeCore(owner._utcNow, dueTime, period);
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner._syncRoot)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                DueAtUtc = DateTimeOffset.MaxValue;
            }
            owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Fire()
        {
            lock (owner._syncRoot)
            {
                if (_disposed)
                {
                    return;
                }
            }
            callback(state);
        }

        public void ChangeCore(DateTimeOffset now, TimeSpan dueTime, TimeSpan period)
        {
            ValidateTimeout(dueTime, nameof(dueTime));
            ValidateTimeout(period, nameof(period));
            _period = period;
            DueAtUtc = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : now + dueTime;
        }

        public void RescheduleCore(DateTimeOffset now)
            => DueAtUtc = _period == Timeout.InfiniteTimeSpan
                ? DateTimeOffset.MaxValue
                : now + _period;

        private static void ValidateTimeout(TimeSpan value, string parameterName)
        {
            if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }
    }
}
