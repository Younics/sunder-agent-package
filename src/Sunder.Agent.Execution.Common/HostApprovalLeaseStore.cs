using System.Security.Cryptography;

namespace Sunder.Agent.Execution.Common;

internal sealed class HostApprovalLeaseStore<T> : IDisposable
    where T : class
{
    private const int MaximumActiveLeases = 4096;
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.Ordinal);
    private readonly TimeSpan _lifetime;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Timer _expiryTimer;
    private bool _disposed;

    public HostApprovalLeaseStore(
        TimeSpan? lifetime = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _lifetime = lifetime is { } configured && configured > TimeSpan.Zero
            ? configured
            : TimeSpan.FromMinutes(30);
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
        var sweepInterval = _lifetime < TimeSpan.FromMinutes(1)
            ? _lifetime
            : TimeSpan.FromMinutes(1);
        _expiryTimer = new Timer(
            static state => ((HostApprovalLeaseStore<T>)state!).SweepExpired(),
            this,
            sweepInterval,
            sweepInterval);
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    public string Issue(string key, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (_syncRoot)
        {
            if (_disposed)
            {
                DisposeValue(value);
                throw new ObjectDisposedException(nameof(HostApprovalLeaseStore<T>));
            }
            var now = _utcNow();
            RemoveExpired(now);
            if (_leases.Count >= MaximumActiveLeases)
            {
                DisposeValue(value);
                throw new InvalidOperationException(
                    $"The process has reached the {MaximumActiveLeases}-lease secure resource approval limit.");
            }

            string token;
            do
            {
                token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            }
            while (_leases.ContainsKey(token));
            _leases[token] = new Lease(key, value, now + _lifetime);
            return token;
        }
    }

    public bool TryRedeem(string token, Func<T, bool> predicate, out T? value)
    {
        value = null;
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        lock (_syncRoot)
        {
            if (_disposed)
            {
                return false;
            }
            var now = _utcNow();
            RemoveExpired(now);
            if (!_leases.TryGetValue(token, out var lease) || !predicate(lease.Value))
            {
                return false;
            }

            Remove(token, lease, disposeValue: false);
            value = lease.Value;
            return true;
        }
    }

    public bool TryRevoke(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        lock (_syncRoot)
        {
            if (_disposed)
            {
                return false;
            }
            RemoveExpired(_utcNow());
            if (!_leases.TryGetValue(token, out var lease))
            {
                return false;
            }

            Remove(token, lease, disposeValue: true);
            return true;
        }
    }

    public bool Contains(string token, Func<T, bool> predicate)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        lock (_syncRoot)
        {
            if (_disposed)
            {
                return false;
            }
            RemoveExpired(_utcNow());
            return _leases.TryGetValue(token, out var lease) && predicate(lease.Value);
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
            _expiryTimer.Dispose();
            foreach (var item in _leases.ToArray())
            {
                Remove(item.Key, item.Value, disposeValue: true);
            }
        }
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var item in _leases.Where(item => item.Value.ExpiresAt <= now).ToArray())
        {
            Remove(item.Key, item.Value, disposeValue: true);
        }
    }

    private void SweepExpired()
    {
        lock (_syncRoot)
        {
            if (!_disposed)
            {
                RemoveExpired(_utcNow());
            }
        }
    }

    private void Remove(string token, Lease lease, bool disposeValue)
    {
        _leases.Remove(token);
        if (disposeValue)
        {
            DisposeValue(lease.Value);
        }
    }

    private void OnProcessExit(object? sender, EventArgs eventArgs) => Dispose();

    private static void DisposeValue(T value)
    {
        if (value is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static string Base64UrlEncode(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record Lease(string Key, T Value, DateTimeOffset ExpiresAt);
}
