namespace Sunder.Package.Agent.Builder;

public sealed class BuilderPersistenceFailedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}

public sealed class BuilderProjectPersistence : IAsyncDisposable
{
    private static readonly TimeSpan DefaultDebounceDelay = TimeSpan.FromMilliseconds(300);

    private readonly object _syncRoot = new();
    private readonly IBuilderProjectStore _store;
    private readonly TimeSpan _debounceDelay;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _changed = new(0);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Task _worker;
    private IReadOnlyList<BuilderProjectRecord> _latestProjects = [];
    private long _generation;
    private long _persistedGeneration;
    private bool _disposed;

    public BuilderProjectPersistence(IBuilderProjectStore store)
        : this(store, DefaultDebounceDelay)
    {
    }

    public BuilderProjectPersistence(IBuilderProjectStore store, TimeSpan debounceDelay)
    {
        _store = store;
        _debounceDelay = debounceDelay < TimeSpan.Zero ? TimeSpan.Zero : debounceDelay;
        _worker = RunWorkerAsync();
    }

    public event EventHandler<BuilderPersistenceFailedEventArgs>? SaveFailed;

    public void RequestSave(IReadOnlyList<BuilderProjectRecord> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _latestProjects = projects.ToArray();
            _generation++;
        }

        _changed.Release();
    }

    public async Task SaveNowAsync(
        IReadOnlyList<BuilderProjectRecord> projects,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projects);
        long generation;
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _latestProjects = projects.ToArray();
            generation = ++_generation;
        }

        // If the caller is cancelled while waiting for the writer, the owned worker still persists this generation.
        _changed.Release();
        await PersistAsync(generation, reportFailure: false, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _shutdown.Cancel();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }

        long generation;
        lock (_syncRoot)
        {
            generation = _generation;
        }

        if (generation > Volatile.Read(ref _persistedGeneration))
        {
            await PersistAsync(generation, reportFailure: false, CancellationToken.None).ConfigureAwait(false);
        }

        _shutdown.Dispose();
        _changed.Dispose();
        _writeGate.Dispose();
    }

    private async Task RunWorkerAsync()
    {
        while (true)
        {
            await _changed.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            DrainChanges();

            long generation;
            while (true)
            {
                lock (_syncRoot)
                {
                    generation = _generation;
                }

                await Task.Delay(_debounceDelay, _shutdown.Token).ConfigureAwait(false);
                DrainChanges();
                lock (_syncRoot)
                {
                    if (generation == _generation)
                    {
                        break;
                    }
                }
            }

            try
            {
                await PersistAsync(generation, reportFailure: true, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Autosave failures are reported through SaveFailed; the worker remains available for later generations.
            }
        }
    }

    private async Task PersistAsync(long generation, bool reportFailure, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<BuilderProjectRecord> projects;
            lock (_syncRoot)
            {
                if (generation != _generation || generation <= Volatile.Read(ref _persistedGeneration))
                {
                    return;
                }

                projects = _latestProjects;
            }

            try
            {
                await _store.SaveAsync(projects, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (reportFailure && IsCurrentGeneration(generation))
                {
                    SaveFailed?.Invoke(this, new BuilderPersistenceFailedEventArgs(ex));
                }

                throw;
            }

            if (IsCurrentGeneration(generation))
            {
                Volatile.Write(ref _persistedGeneration, generation);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private bool IsCurrentGeneration(long generation)
    {
        lock (_syncRoot)
        {
            return generation == _generation;
        }
    }

    private void DrainChanges()
    {
        while (_changed.Wait(0))
        {
        }
    }
}
