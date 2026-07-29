using System.Collections.Concurrent;

namespace Sunder.Agent.Execution.Common;

internal sealed class HostMutationGate(IEqualityComparer<string> comparer)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(comparer);

    public int Count => _entries.Count;

    public bool Contains(string key) => _entries.ContainsKey(key);

    public async ValueTask<IDisposable> EnterAsync(string key, CancellationToken cancellationToken)
    {
        while (true)
        {
            var entry = _entries.GetOrAdd(key, static _ => new Entry());
            if (!entry.TryAddReference())
            {
                continue;
            }

            try
            {
                await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new Releaser(this, key, entry);
            }
            catch
            {
                ReleaseReference(key, entry);
                throw;
            }
        }
    }

    private void Release(string key, Entry entry)
    {
        entry.Gate.Release();
        ReleaseReference(key, entry);
    }

    private void ReleaseReference(string key, Entry entry)
    {
        if (!entry.ReleaseReference())
        {
            return;
        }
        _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
        entry.Gate.Dispose();
    }

    private sealed class Entry
    {
        private readonly object _sync = new();
        private int _references;
        private bool _retired;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public bool TryAddReference()
        {
            lock (_sync)
            {
                if (_retired)
                {
                    return false;
                }
                _references++;
                return true;
            }
        }

        public bool ReleaseReference()
        {
            lock (_sync)
            {
                _references--;
                if (_references != 0)
                {
                    return false;
                }
                _retired = true;
                return true;
            }
        }
    }

    private sealed class Releaser(HostMutationGate owner, string key, Entry entry) : IDisposable
    {
        private HostMutationGate? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(key, entry);
    }
}

internal static class HostMutationCoordinator
{
    private static readonly HostMutationGate Gates = new(StringComparer.Ordinal);

    public static int Count => Gates.Count;

    public static bool IsEntered(LocalSecureIdentity parentIdentity)
        => Gates.Contains(parentIdentity.ToString());

    public static IDisposable Enter(LocalSecureIdentity parentIdentity, CancellationToken cancellationToken)
        => Gates.EnterAsync(parentIdentity.ToString(), cancellationToken).AsTask().GetAwaiter().GetResult();
}
