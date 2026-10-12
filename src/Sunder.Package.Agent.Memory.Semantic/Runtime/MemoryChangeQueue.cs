using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Sunder.Package.Agent.Memory.Semantic.Runtime;

internal sealed class MemoryChangeQueue
{
    // Larger distinct-session bursts collapse to a full refresh instead of retaining unbounded detail.
    internal const int PendingSessionCapacity = 16;

    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly object _gate = new();
    private readonly HashSet<Guid> _sessions = [];
    private bool _worker;
    private bool _reset;
    private bool _completed;

    public void PublishSession(Guid sessionId)
    {
        lock (_gate)
        {
            if (_completed || _reset || _sessions.Contains(sessionId))
            {
                return;
            }
            if (_sessions.Count == PendingSessionCapacity)
            {
                _sessions.Clear();
                _worker = false;
                _reset = true;
            }
            else
            {
                _sessions.Add(sessionId);
            }
        }
        _signals.Writer.TryWrite(true);
    }

    public void PublishWorker()
    {
        lock (_gate)
        {
            if (_completed || _reset || _worker)
            {
                return;
            }
            _worker = true;
        }
        _signals.Writer.TryWrite(true);
    }

    public void PublishReset()
    {
        lock (_gate)
        {
            if (_completed || _reset)
            {
                return;
            }
            _sessions.Clear();
            _worker = false;
            _reset = true;
        }
        _signals.Writer.TryWrite(true);
    }

    public async IAsyncEnumerable<MemoryChanged> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var _ in _signals.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            while (TakePending() is { Length: > 0 } pending)
            {
                foreach (var change in pending)
                {
                    yield return change;
                }
            }
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
        }
        _signals.Writer.TryComplete();
    }

    private MemoryChanged[] TakePending()
    {
        lock (_gate)
        {
            if (_reset)
            {
                _reset = false;
                _sessions.Clear();
                _worker = false;
                return [new MemoryChanged(MemoryChangeKind.ResetRequired)];
            }

            if (_sessions.Count == 0 && !_worker)
            {
                return [];
            }

            var pending = new MemoryChanged[_sessions.Count + (_worker ? 1 : 0)];
            var index = 0;
            foreach (var sessionId in _sessions)
            {
                pending[index++] = new MemoryChanged(MemoryChangeKind.Session, sessionId);
            }
            if (_worker)
            {
                pending[index] = new MemoryChanged(MemoryChangeKind.Worker);
            }
            _sessions.Clear();
            _worker = false;
            return pending;
        }
    }
}
