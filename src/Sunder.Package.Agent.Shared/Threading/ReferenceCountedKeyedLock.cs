using System.Collections.Concurrent;

namespace Sunder.Package.Agent.Shared.Threading;

internal sealed class ReferenceCountedKeyedLock<TKey>(IEqualityComparer<TKey>? comparer = null) where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Entry> _entries = new(comparer ?? EqualityComparer<TKey>.Default);

    internal int Count => _entries.Count;

    public async ValueTask<IDisposable> EnterAsync(TKey key, CancellationToken cancellationToken = default)
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

    public IDisposable Enter(TKey key)
    {
        while (true)
        {
            var entry = _entries.GetOrAdd(key, static _ => new Entry());
            if (!entry.TryAddReference())
            {
                continue;
            }

            entry.Gate.Wait();
            return new Releaser(this, key, entry);
        }
    }

    private void Release(TKey key, Entry entry)
    {
        entry.Gate.Release();
        ReleaseReference(key, entry);
    }

    private void ReleaseReference(TKey key, Entry entry)
    {
        if (!entry.ReleaseReference())
        {
            return;
        }

        _entries.TryRemove(new KeyValuePair<TKey, Entry>(key, entry));
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

    private sealed class Releaser(ReferenceCountedKeyedLock<TKey> owner, TKey key, Entry entry) : IDisposable
    {
        private ReferenceCountedKeyedLock<TKey>? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(key, entry);
    }
}
