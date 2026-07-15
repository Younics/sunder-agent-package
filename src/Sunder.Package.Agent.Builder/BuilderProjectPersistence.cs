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
    private int _activeSaves;
    private TaskCompletionSource? _activeSavesCompleted;
    private bool _disposed;
    private Task? _disposeTask;

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
            _activeSaves++;
        }

        try
        {
            // If the caller is cancelled while waiting for the writer, the owned worker still persists this generation.
            _changed.Release();
            await PersistAsync(generation, reportFailure: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CompleteActiveSave();
        }
    }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_syncRoot)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposed = true;
            disposeTask = _disposeTask = DisposeCoreAsync();
        }

        return new ValueTask(disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        Task activeSaves;
        lock (_syncRoot)
        {
            activeSaves = _activeSaves == 0
                ? Task.CompletedTask
                : (_activeSavesCompleted ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        _shutdown.Cancel();
        try
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }

            await activeSaves.ConfigureAwait(false);

            long generation;
            lock (_syncRoot)
            {
                generation = _generation;
            }

            if (generation > Volatile.Read(ref _persistedGeneration))
            {
                await PersistAsync(generation, reportFailure: false, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _shutdown.Dispose();
            _changed.Dispose();
            _writeGate.Dispose();
        }
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

    private void CompleteActiveSave()
    {
        TaskCompletionSource? completed = null;
        lock (_syncRoot)
        {
            _activeSaves--;
            if (_activeSaves == 0)
            {
                completed = _activeSavesCompleted;
                _activeSavesCompleted = null;
            }
        }

        completed?.TrySetResult();
    }

    private void DrainChanges()
    {
        while (_changed.Wait(0))
        {
        }
    }
}
