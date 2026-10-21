using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;

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
    private readonly IPackageAppStateUpdates _updates;
    private readonly TimeSpan _debounceDelay;
    private readonly CancellationTokenSource _flush = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly HashSet<Task> _pending = [];
    private IReadOnlyList<BuilderProjectRecord> _latestProjects = [];
    private long _generation;
    private long _persistedGeneration;
    private Exception? _lastFailure;
    private bool _disposed;
    private Task? _disposeTask;

    [ActivatorUtilitiesConstructor]
    public BuilderProjectPersistence(IBuilderProjectStore store, IPackageAppStateUpdates updates)
        : this(store, updates, DefaultDebounceDelay) { }

    public BuilderProjectPersistence(IBuilderProjectStore store, IPackageAppStateUpdates updates, TimeSpan debounceDelay)
    {
        _store = store;
        _updates = updates;
        _debounceDelay = debounceDelay < TimeSpan.Zero ? TimeSpan.Zero : debounceDelay;
    }

    public event EventHandler<BuilderPersistenceFailedEventArgs>? SaveFailed;
    public event EventHandler? SaveSucceeded;

    public void RequestSave(IReadOnlyList<BuilderProjectRecord> projects)
        => Accept(projects, immediate: false, reportFailure: true);

    public async Task SaveNowAsync(IReadOnlyList<BuilderProjectRecord> projects, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Caller cancellation abandons only the wait, not an already accepted save.
        await Accept(projects, immediate: true, reportFailure: false).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task Accept(IReadOnlyList<BuilderProjectRecord> projects, bool immediate, bool reportFailure)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var snapshot = projects.ToArray();
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var generation = _generation + 1;
            // Admission is synchronous and the callback is asynchronous. If the App
            // has closed admission, no new dirty generation is accepted locally.
            var work = _updates.RunAsync("builder.bookmarks", context => PersistAcceptedAsync(generation, immediate, context));
            _latestProjects = snapshot;
            _generation = generation;
            var observed = ObserveAsync(work, generation, reportFailure);
            _pending.Add(observed);
            _ = observed.ContinueWith(completed =>
            {
                lock (_syncRoot) _pending.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return work;
        }
    }

    private async Task PersistAcceptedAsync(long generation, bool immediate, PackageAppStateUpdateContext context)
    {
        if (!immediate)
        {
            using var delay = CancellationTokenSource.CreateLinkedTokenSource(context.SettlementRequested, _flush.Token, context.CancellationToken);
            try { await Task.Delay(_debounceDelay, delay.Token).ConfigureAwait(false); }
            catch (OperationCanceledException error) when (error.CancellationToken == delay.Token
                && !context.CancellationToken.IsCancellationRequested
                && (context.SettlementRequested.IsCancellationRequested || _flush.IsCancellationRequested)) { }
        }
        context.CancellationToken.ThrowIfCancellationRequested();
        await _writeGate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<BuilderProjectRecord> projects;
            lock (_syncRoot)
            {
                if (generation != _generation || generation <= _persistedGeneration) return;
                projects = _latestProjects;
            }
            await _store.SaveAsync(projects, context.CancellationToken).ConfigureAwait(false);
            lock (_syncRoot)
            {
                if (generation != _generation) return;
                _persistedGeneration = generation;
                _lastFailure = null;
            }
            SaveSucceeded?.Invoke(this, EventArgs.Empty);
        }
        finally { _writeGate.Release(); }
    }

    private async Task ObserveAsync(Task work, long generation, bool reportFailure)
    {
        await Task.Yield();
        try { await work.ConfigureAwait(false); }
        catch (Exception error)
        {
            lock (_syncRoot)
            {
                if (generation != _generation) return;
                _lastFailure = error;
            }
            if (reportFailure)
            {
                try { SaveFailed?.Invoke(this, new(error)); }
                catch (Exception callbackError)
                {
                    lock (_syncRoot)
                        if (generation == _generation) _lastFailure = new AggregateException(error, callbackError);
                }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_syncRoot)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true;
            var pending = _pending.ToArray();
            return new(_disposeTask = Task.Run(() => DisposeCoreAsync(pending)));
        }
    }

    private async Task DisposeCoreAsync(Task[] pending)
    {
        try
        {
            await _flush.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(pending).ConfigureAwait(false);
            Exception? failure;
            bool dirty;
            lock (_syncRoot) { failure = _lastFailure; dirty = _generation > _persistedGeneration; }
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            if (dirty) throw new InvalidOperationException("The final accepted Builder bookmark update did not confirm persistence.");
        }
        finally { _flush.Dispose(); _writeGate.Dispose(); }
        // All writes belonged to work registered at acceptance. Disposal never
        // starts a new storage mutation or invents post-revocation authority.
    }
}
