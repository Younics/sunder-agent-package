namespace Sunder.Package.Agent.HistorySearch;

internal sealed class HistorySemanticOperationFence : IDisposable
{
    private readonly object _lock = new();
    private CancellationTokenSource _revisionCancellation = new();
    private TaskCompletionSource? _drained;
    private long _revision;
    private int _activeOperations;
    private bool _suspended;
    private bool _disposed;

    public HistorySemanticOperationFence(HistorySearchStore store)
    {
        _revision = store.GetConfiguration().Revision;
    }

    internal Lease Begin(long expectedRevision, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_suspended || expectedRevision != _revision)
            {
                throw new OperationCanceledException("The semantic configuration was superseded.");
            }

            _activeOperations++;
            var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _revisionCancellation.Token);
            return new Lease(this, expectedRevision, linked);
        }
    }

    internal async Task SuspendAndDrainAsync()
    {
        CancellationTokenSource cancellation;
        Task drain;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _suspended = true;
            cancellation = _revisionCancellation;
            if (_activeOperations == 0)
            {
                drain = Task.CompletedTask;
            }
            else
            {
                _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                drain = _drained.Task;
            }
        }

        await cancellation.CancelAsync().ConfigureAwait(false);
        await drain.ConfigureAwait(false);
    }

    internal void Activate(long revision)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeOperations != 0)
            {
                throw new InvalidOperationException("Semantic operations must drain before a revision is activated.");
            }

            _revisionCancellation.Dispose();
            _revisionCancellation = new CancellationTokenSource();
            _revision = revision;
            _suspended = false;
            _drained = null;
        }
    }

    internal void ThrowIfCurrent(long revision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (_disposed || _suspended || revision != _revision)
            {
                throw new OperationCanceledException("The semantic configuration was superseded.");
            }
        }
    }

    public void Dispose()
    {
        CancellationTokenSource cancellation;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _suspended = true;
            cancellation = _revisionCancellation;
        }
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void End()
    {
        TaskCompletionSource? drained = null;
        lock (_lock)
        {
            _activeOperations--;
            if (_activeOperations == 0)
            {
                drained = _drained;
                _drained = null;
            }
        }
        drained?.TrySetResult();
    }

    internal sealed class Lease : IDisposable
    {
        private HistorySemanticOperationFence? _owner;
        private readonly CancellationTokenSource _linked;

        internal Lease(
            HistorySemanticOperationFence owner,
            long revision,
            CancellationTokenSource linked)
        {
            _owner = owner;
            Revision = revision;
            _linked = linked;
        }

        internal long Revision { get; }
        internal CancellationToken CancellationToken => _linked.Token;

        internal void ThrowIfCurrent()
            => _owner?.ThrowIfCurrent(Revision, _linked.Token);

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null)
            {
                return;
            }
            _linked.Dispose();
            owner.End();
        }
    }
}
