using Sunder.Sdk.Abstractions;
using Sunder.Package.Agent.Shared.Presentation;
using System.Runtime.ExceptionServices;

namespace Sunder.Package.Agent.Builder;

// Emitted only after the host has confirmed cancellation and the admitted delegate has drained.
// The App boundary supplies the request identity when translating this into a terminal snapshot.
internal sealed class BuilderQueueCanceledException()
    : Exception("Builder operation cancelled before backend execution.");

public sealed class BuilderOperationQueue(IBackgroundProcessQueue backgroundProcesses)
{
    public const string GroupKey = "sunder-package-builder";

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Waits for both shell termination and delegate cleanup, including cancellation while still queued.</summary>
    public Task<BuilderJobSnapshot> EnqueueAsync(string title, BackgroundProcessIndicator indicator,
        IReadOnlyDictionary<string, string> metadata, Func<BackgroundProcessContext, Task<BuilderJobSnapshot>> operation,
        CancellationToken cancellationToken = default)
        => new ScheduledJob(this, backgroundProcesses, operation, cancellationToken).RunAsync(title, indicator, metadata);

    public BackgroundProcessSnapshot Enqueue(
        string title,
        Func<BackgroundProcessContext, Task> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(operation);
        return backgroundProcesses.Enqueue(new BackgroundProcessRequest(
            title,
            GroupKey,
            BackgroundProcessIndicator.Main,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup,
            CanCancel: true,
            context => RunAsync(() => operation(context), context.CancellationToken)));
    }

    public async Task RunAsync(Func<Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await operation().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class ScheduledJob(BuilderOperationQueue owner, IBackgroundProcessQueue queue,
        Func<BackgroundProcessContext, Task<BuilderJobSnapshot>> operation, CancellationToken caller)
    {
        private const string CorrelationKey = "builder.queueId";
        private readonly string _correlation = Guid.NewGuid().ToString("N");
        private readonly object _sync = new();
        private readonly TaskCompletionSource<Guid> _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<BackgroundProcessSnapshot> _terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _stopping = new();
        private Guid? _processId;
        private bool _started;
        private bool _closed;
        private BuilderJobSnapshot? _result;
        private Exception? _error;
        private bool _hostCancellation;

        internal async Task<BuilderJobSnapshot> RunAsync(string title, BackgroundProcessIndicator indicator,
            IReadOnlyDictionary<string, string> metadata)
        {
            if (caller.IsCancellationRequested) { _stopping.Dispose(); caller.ThrowIfCancellationRequested(); }
            var description = new Dictionary<string, string>(metadata, StringComparer.Ordinal) { [CorrelationKey] = _correlation };
            queue.ProcessChanged += OnChanged;
            using var cancellation = caller.Register(() => RequestCancellation());
            try
            {
                var initial = queue.Enqueue(new(title, GroupKey, indicator,
                    BackgroundProcessConcurrencyMode.SequentialWithinGroup, CanCancel: true, ExecuteAsync, description));
                lock (_sync) _processId = initial.ProcessId;
                _accepted.TrySetResult(initial.ProcessId);
                Observe(initial);
                if (caller.IsCancellationRequested) RequestCancellation();

                // Subscribe before enqueue and reconcile after it: the host may finish or cancel before
                // Enqueue returns its original queued snapshot. Polling also covers missed/trimmed events.
                long? missingSince = null;
                while (!_terminal.Task.IsCompleted)
                {
                    var current = queue.ListProcesses(GroupKey).FirstOrDefault(item => item.ProcessId == initial.ProcessId);
                    if (current is not null) { Observe(current); missingSince = null; }
                    else
                    {
                        missingSince ??= Environment.TickCount64;
                        if (caller.IsCancellationRequested) caller.ThrowIfCancellationRequested();
                        if (Environment.TickCount64 - missingSince >= 5000)
                            throw new IOException("The host no longer reports this queued Builder operation.");
                    }
                    if (!_terminal.Task.IsCompleted)
                        await Task.WhenAny(_terminal.Task, Task.Delay(200)).ConfigureAwait(false);
                }

                var terminal = await _terminal.Task.ConfigureAwait(false);
                await CloseAndDrainAsync(cancel: false).ConfigureAwait(false);
                // The backend result retains partial source even when the shell delegate failed/cancelled.
                if (_result is not null) return _result;
                if (_error is not null && !(_hostCancellation && terminal.State == BackgroundProcessState.Cancelled))
                    ExceptionDispatchInfo.Capture(_error).Throw();
                if (terminal.State == BackgroundProcessState.Cancelled)
                {
                    if (caller.IsCancellationRequested)
                        throw new OperationCanceledException("Builder operation cancelled before it started.", _error, caller);
                    throw new BuilderQueueCanceledException();
                }
                throw new InvalidOperationException(terminal.ErrorMessage ?? "The host ended the Builder operation before executing it.");
            }
            catch
            {
                _accepted.TrySetCanceled(new CancellationToken(true));
                // An enqueue can lose its acknowledgement after the host accepted it.
                // Correlation is available even before the process ID was returned.
                try
                {
                    foreach (var snapshot in queue.ListProcesses(GroupKey))
                        if (snapshot.Metadata.TryGetValue(CorrelationKey, out var key) && key == _correlation)
                            lock (_sync) _processId ??= snapshot.ProcessId;
                }
                catch (ObjectDisposedException) { }
                RequestCancellation();
                await CloseAndDrainAsync(cancel: true).ConfigureAwait(false);
                throw;
            }
            finally
            {
                queue.ProcessChanged -= OnChanged;
                _stopping.Dispose();
            }
        }

        private async Task ExecuteAsync(BackgroundProcessContext context)
        {
            await _accepted.Task.ConfigureAwait(false);
            lock (_sync)
            {
                // A terminal queued snapshot seals admission. A late host callback must never start a backend job.
                if (_closed || _started) throw new OperationCanceledException(new CancellationToken(true));
                _started = true;
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, context.CancellationToken, _stopping.Token);
            try
            {
                var effective = new BackgroundProcessContext(linked.Token, context.ReportStatus, context.ReportProgress, context.ReportIndeterminate);
                _result = await owner.RunAsync(() => operation(effective), linked.Token).ConfigureAwait(false);
                switch (_result.State)
                {
                    case "succeeded":
                        context.ReportProgress(100, _result.Message);
                        break;
                    case "cancelled":
                        RequestCancellation();
                        throw new OperationCanceledException(_result.Message, context.CancellationToken);
                    case "failed":
                        throw new InvalidOperationException(_result.Error ?? _result.Message);
                    default:
                        throw new InvalidDataException("A finite Builder job returned without a terminal result.");
                }
            }
            catch (Exception error)
            {
                // Cancellation while waiting for the finite gate (or before the backend starts)
                // uses our private linked token. A foreign/tokenless callback failure is not proof
                // of host cancellation, even when the shell happens to be cancelling too.
                _hostCancellation = error is OperationCanceledException hostCancellation
                    && hostCancellation.CancellationToken == linked.Token
                    && context.CancellationToken.IsCancellationRequested
                    && !PresentationExceptionPolicy.IsFatal(error);
                _error = error is OperationCanceledException cancellation
                    && caller.IsCancellationRequested && cancellation.CancellationToken == linked.Token
                    && !PresentationExceptionPolicy.IsFatal(error)
                        ? new OperationCanceledException(error.Message, error, caller)
                        : error;
                if (error is OperationCanceledException) RequestCancellation();
                throw;
            }
            finally { _drained.TrySetResult(); }
        }

        private void OnChanged(object? sender, BackgroundProcessChangedEventArgs args) => Observe(args.Snapshot);

        private void Observe(BackgroundProcessSnapshot snapshot)
        {
            if (!snapshot.Metadata.TryGetValue(CorrelationKey, out var correlation) || correlation != _correlation) return;
            lock (_sync)
            {
                _processId ??= snapshot.ProcessId;
                if (!snapshot.IsTerminal) return;
                if (!_started) _closed = true;
                _terminal.TrySetResult(snapshot);
            }
        }

        private void RequestCancellation()
        {
            Guid? processId;
            lock (_sync)
            {
                if (_terminal.Task.IsCompleted) return;
                processId = _processId;
            }
            if (processId is null) return; // Replayed after acceptance if cancellation raced Enqueue.
            try { queue.Cancel(processId.Value); }
            catch (ObjectDisposedException) { } // List reconciliation will drain and report the retired queue.
        }

        private async Task CloseAndDrainAsync(bool cancel)
        {
            bool started;
            lock (_sync) { _closed = true; started = _started; }
            if (cancel) await _stopping.CancelAsync().ConfigureAwait(false);
            if (started) await _drained.Task.ConfigureAwait(false);
        }
    }
}
