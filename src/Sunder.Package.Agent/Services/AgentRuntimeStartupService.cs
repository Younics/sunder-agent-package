using System.Diagnostics;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Services;

internal sealed class AgentRuntimeStartupService(
    AgentLocalStore store,
    AgentAttachmentService attachmentService,
    AgentBackgroundWorkService backgroundWork,
    AgentSessionCleanupDispatcher sessionCleanup,
    AgentLifecycleDispatcher lifecycleDispatcher,
    AgentRunDispatcher runDispatcher,
    AgentParentRunContinuationService parentContinuations,
    HistorySearchIndexingService historySearch,
    AgentRuntimeGenerationOptions generationOptions,
    IPackageContext packageContext)
    : IPackageRuntimeGenerationParticipant, IAsyncDisposable
{
    private static readonly TimeSpan AttachmentOrphanGrace = TimeSpan.FromHours(1);
    private static readonly AgentRuntimeProcessIdentity ProcessIdentity = CreateProcessIdentity();

    private readonly AgentRuntimeGenerationOptions _generationOptions = ValidateOptions(generationOptions);
    private readonly IPackageEventLogger _eventLogger = packageContext.Logging.Events;
    private readonly object _syncRoot = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private TaskCompletionSource _generationCompletion = CreateCompletion();
    private Task? _shutdownTask;
    private bool _started;
    private bool _workersStarted;
    private bool _disposed;
    private PackageRuntimeGeneration? _generation;
    private RuntimeGenerationLease? _generationLease;

    public Task GenerationCompletion
    {
        get
        {
            lock (_syncRoot)
            {
                return _generationCompletion.Task;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_syncRoot)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_started)
                {
                    return;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            lock (_syncRoot)
            {
                if (_generationCompletion.Task.IsCompleted)
                {
                    _generationCompletion = CreateCompletion();
                }
                _started = true;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task CommitGenerationAsync(
        PackageRuntimeGeneration generation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (generation.ActivationId == Guid.Empty || generation.SessionGeneration < 1)
        {
            throw new ArgumentException("A committed Runtime generation requires a non-empty activation id and positive session generation.", nameof(generation));
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var historyStarted = false;
        var backgroundStarted = false;
        var cleanupStarted = false;
        var lifecycleStarted = false;
        var dispatcherStarted = false;
        RuntimeGenerationLease? generationLease = null;
        try
        {
            lock (_syncRoot)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_started)
                {
                    throw new InvalidOperationException("Agent Runtime startup must be prepared before its generation is committed.");
                }
                if (_workersStarted)
                {
                    if (_generation != generation)
                    {
                        throw new InvalidOperationException("Agent Runtime startup is already committed to another generation.");
                    }
                    return;
                }
                if (_generation is not null && _generation != generation)
                {
                    throw new InvalidOperationException("Agent Runtime startup cannot change generation identity after commit begins.");
                }
                _generation = generation;
            }

            generationLease = await AcquireRuntimeGenerationAsync(generation, cancellationToken).ConfigureAwait(false);
            store.BindRuntimeGeneration(generation.ActivationId, generationLease.FenceToken);
            generationLease.Start();
            store.EnsureRuntimeGenerationCurrent();
            historySearch.BindRuntimeGeneration(generation.ActivationId);

            store.RecoverToolExecutions();
            store.RecoverInterruptedPermissionClaims();
            store.RecoverAmbiguousParentContinuationWork();
            store.RecoverUnownedActiveRuns();
            attachmentService.CleanupOrphans(
                store.ListReferencedAttachmentPaths(),
                DateTimeOffset.UtcNow - AttachmentOrphanGrace);

            backgroundStarted = true;
            await backgroundWork.StartAsync(cancellationToken).ConfigureAwait(false);
            cleanupStarted = true;
            await sessionCleanup.StartAsync(cancellationToken).ConfigureAwait(false);
            lifecycleStarted = true;
            await lifecycleDispatcher.StartAsync(cancellationToken).ConfigureAwait(false);
            dispatcherStarted = true;
            await runDispatcher.StartAsync(cancellationToken).ConfigureAwait(false);
            parentContinuations.StartRecovery();

            // Local-only History maintenance mutates shared derived state, so it runs last.
            historyStarted = true;
            await historySearch.StartAsync(cancellationToken).ConfigureAwait(false);

            lock (_syncRoot)
            {
                _generationLease = generationLease;
                _workersStarted = true;
            }
            generationLease = null;
        }
        catch
        {
            await StopWorkersAsync(
                dispatcherStarted,
                lifecycleStarted,
                cleanupStarted,
                historyStarted,
                backgroundStarted,
                suppressErrors: true).ConfigureAwait(false);
            if (generationLease is not null)
            {
                try
                {
                    await generationLease.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    store.ClearRuntimeGeneration(generation.ActivationId, generationLease.FenceToken);
                }
            }
            lock (_syncRoot)
            {
                _workersStarted = false;
            }
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
        => BeginShutdown();

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
        await BeginShutdown().ConfigureAwait(false);
    }

    private Task BeginShutdown()
    {
        TaskCompletionSource? completion = null;
        lock (_syncRoot)
        {
            if (_shutdownTask is not null)
            {
                return _shutdownTask;
            }

            completion = CreateCompletion();
            _shutdownTask = completion.Task;
        }

        _ = CompleteShutdownAsync(completion);
        return completion.Task;
    }

    private async Task CompleteShutdownAsync(TaskCompletionSource completion)
    {
        try
        {
            await StopCoreAsync(completion.Task).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task StopCoreAsync(Task shutdownTask)
    {
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        RuntimeGenerationLease? generationLease = null;
        Exception? failure = null;
        try
        {
            var workersStarted = false;
            lock (_syncRoot)
            {
                _started = false;
                workersStarted = _workersStarted;
                _workersStarted = false;
                generationLease = _generationLease;
                _generationLease = null;
                _generation = null;
            }

            if (workersStarted)
            {
                try
                {
                    await StopWorkersAsync(
                        stopDispatcher: true,
                        stopLifecycle: true,
                        stopCleanup: true,
                        stopHistory: true,
                        stopBackground: true,
                        suppressErrors: false).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            }

            if (generationLease is not null)
            {
                try
                {
                    await generationLease.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failure = failure is null
                        ? exception
                        : new AggregateException(failure, exception);
                }
                finally
                {
                    store.ClearRuntimeGeneration(generationLease.Epoch, generationLease.FenceToken);
                }
            }

            lock (_syncRoot)
            {
                if (ReferenceEquals(_shutdownTask, shutdownTask))
                {
                    _shutdownTask = null;
                }
                if (!_generationCompletion.Task.IsCompleted)
                {
                    if (failure is null)
                    {
                        _generationCompletion.TrySetResult();
                    }
                    else
                    {
                        _generationCompletion.TrySetException(failure);
                    }
                }
            }

            if (failure is not null)
            {
                throw failure;
            }
        }
        finally
        {
            lock (_syncRoot)
            {
                if (ReferenceEquals(_shutdownTask, shutdownTask))
                {
                    _shutdownTask = null;
                }
            }
            _lifecycleGate.Release();
        }
    }

    private async Task StopWorkersAsync(
        bool stopDispatcher,
        bool stopLifecycle,
        bool stopCleanup,
        bool stopHistory,
        bool stopBackground,
        bool suppressErrors)
    {
        var failures = new List<Exception>();
        var stops = new (bool Required, Func<Task> Signal, Func<Task> Stop)[]
        {
            (stopHistory, historySearch.SignalStopAsync, () => historySearch.StopAsync(CancellationToken.None)),
            (stopDispatcher, runDispatcher.SignalStopAsync, () => runDispatcher.StopAsync(CancellationToken.None)),
            (stopLifecycle, lifecycleDispatcher.SignalStopAsync, () => lifecycleDispatcher.StopAsync(CancellationToken.None)),
            (stopCleanup, sessionCleanup.SignalStopAsync, () => sessionCleanup.StopAsync(CancellationToken.None)),
            (stopBackground, backgroundWork.SignalStopAsync, () => backgroundWork.StopAsync(CancellationToken.None)),
        };
        var signals = new Task?[stops.Length];
        var drains = new Task?[stops.Length];
        for (var index = 0; index < stops.Length; index++)
        {
            var stop = stops[index];
            if (!stop.Required)
            {
                continue;
            }

            try
            {
                signals[index] = stop.Signal();
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        for (var index = 0; index < stops.Length; index++)
        {
            var stop = stops[index];
            if (!stop.Required)
            {
                continue;
            }

            try
            {
                drains[index] = Task.Run(stop.Stop, CancellationToken.None);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        for (var index = 0; index < drains.Length; index++)
        {
            if (signals[index] is { } signal)
            {
                try
                {
                    await signal.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }

            if (drains[index] is not { } drain)
            {
                continue;
            }

            try
            {
                await drain.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        if (!suppressErrors && failures.Count > 0)
        {
            throw failures.Count == 1
                ? failures[0]
                : new AggregateException("One or more Agent runtime workers failed to stop.", failures);
        }
    }

    private async Task<RuntimeGenerationLease> AcquireRuntimeGenerationAsync(
        PackageRuntimeGeneration generation,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = _generationOptions.TimeProvider.GetUtcNow();
            var current = store.GetCurrentRuntimeGeneration();
            if (current is null
                || current.Epoch == generation.ActivationId
                || current.Status != "Committed"
                || current.LeaseExpiresAtUtc <= now)
            {
                if (store.TryCommitRuntimeGeneration(
                        generation,
                        ProcessIdentity,
                        current?.Epoch,
                        now,
                        now + _generationOptions.LeaseDuration))
                {
                    return new RuntimeGenerationLease(
                        store,
                        generation.ActivationId,
                        now + _generationOptions.LeaseDuration,
                        _generationOptions,
                        ReportGenerationLeaseFault);
                }
                continue;
            }

            if (!IsProcessAlive(current))
            {
                _ = store.TryMarkRuntimeGenerationDead(current, now);
                continue;
            }

            await _generationOptions.Delay(
                _generationOptions.ClaimPollInterval,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private void ReportGenerationLeaseFault(Exception exception)
    {
        var beginShutdown = false;
        Guid? epoch;
        lock (_syncRoot)
        {
            epoch = _generation?.ActivationId;
            if (epoch is null || _generationCompletion.Task.IsCompleted)
            {
                return;
            }

            _generationCompletion.TrySetException(exception);
            beginShutdown = true;
        }

        _ = LogGenerationLeaseFaultAsync(epoch.Value, exception);
        if (beginShutdown)
        {
            _ = BeginShutdown();
        }
    }

    private async Task LogGenerationLeaseFaultAsync(Guid epoch, Exception exception)
    {
        try
        {
            await _eventLogger.CriticalAsync(
                "runtime.generation.ownership-lost",
                "The Agent Runtime generation lost durable ownership and is draining all generation workers.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["runtime.generation.epoch"] = epoch.ToString("N"),
                    ["exception.type"] = exception.GetType().FullName,
                },
                exception,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Runtime observes GenerationCompletion independently of package logging.
        }
    }

    private static AgentRuntimeProcessIdentity CreateProcessIdentity()
    {
        using var process = Process.GetCurrentProcess();
        return new AgentRuntimeProcessIdentity(
            process.Id,
            new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero));
    }

    private static AgentRuntimeGenerationOptions ValidateOptions(AgentRuntimeGenerationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return options;
    }

    private static TaskCompletionSource CreateCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static bool IsProcessAlive(AgentRuntimeGenerationOwnership generation)
    {
        try
        {
            using var process = Process.GetProcessById(generation.ProcessId);
            if (process.HasExited)
            {
                return false;
            }
            var startedAtUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return (startedAtUtc - generation.ProcessStartedAtUtc).Duration() < TimeSpan.FromSeconds(1);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true;
        }
        catch (NotSupportedException)
        {
            return true;
        }
    }

    private sealed class RuntimeGenerationLease : IAsyncDisposable
    {
        private readonly AgentLocalStore _store;
        private readonly Guid _epoch;
        private readonly AgentRuntimeGenerationOptions _options;
        private readonly Action<Exception> _faulted;
        private readonly CancellationTokenSource _stopping = new();
        private readonly Guid _fenceToken = Guid.NewGuid();
        private Task _heartbeat = Task.CompletedTask;
        private DateTimeOffset _leaseExpiresAtUtc;
        private int _started;
        private int _disposed;
        private int _faultReported;

        public Guid Epoch => _epoch;

        public Guid FenceToken => _fenceToken;

        public RuntimeGenerationLease(
            AgentLocalStore store,
            Guid epoch,
            DateTimeOffset leaseExpiresAtUtc,
            AgentRuntimeGenerationOptions options,
            Action<Exception> faulted)
        {
            _store = store;
            _epoch = epoch;
            _leaseExpiresAtUtc = leaseExpiresAtUtc;
            _options = options;
            _faulted = faulted;
        }

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                throw new InvalidOperationException("The Agent Runtime generation lease heartbeat is already started.");
            }

            _heartbeat = HeartbeatAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _store.FenceRuntimeGeneration(_epoch, _fenceToken);
            await _stopping.CancelAsync().ConfigureAwait(false);
            try
            {
                await _heartbeat.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
            finally
            {
                _store.StopRuntimeGeneration(_epoch, _fenceToken, _options.TimeProvider.GetUtcNow());
                _stopping.Dispose();
            }
        }

        private async Task HeartbeatAsync()
        {
            try
            {
                await _options.Delay(
                    _options.HeartbeatInterval,
                    _stopping.Token).ConfigureAwait(false);
                var retryAttempt = 0;
                while (true)
                {
                    var now = _options.TimeProvider.GetUtcNow();
                    var renewalDeadline = _leaseExpiresAtUtc - _options.LeaseSafetyMargin;
                    if (now >= renewalDeadline)
                    {
                        ReportFault(new AgentRuntimeGenerationHeartbeatException(
                            _epoch,
                            new TimeoutException("The Runtime generation lease reached its renewal safety deadline.")));
                        return;
                    }

                    try
                    {
                        var nextExpiration = now + _options.LeaseDuration;
                        if (!await RenewBeforeDeadlineAsync(
                                now,
                                nextExpiration,
                                renewalDeadline).ConfigureAwait(false))
                        {
                            ReportFault(new AgentRuntimeGenerationOwnershipLostException(_epoch));
                            return;
                        }

                        _leaseExpiresAtUtc = nextExpiration;
                        retryAttempt = 0;
                        await _options.Delay(
                            _options.HeartbeatInterval,
                            _stopping.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        now = _options.TimeProvider.GetUtcNow();
                        renewalDeadline = _leaseExpiresAtUtc - _options.LeaseSafetyMargin;
                        if (now >= renewalDeadline)
                        {
                            ReportFault(new AgentRuntimeGenerationHeartbeatException(
                                _epoch,
                                exception));
                            return;
                        }

                        var retryDelay = CalculateRetryDelay(retryAttempt++);
                        var remaining = renewalDeadline - now;
                        if (retryDelay > remaining)
                        {
                            retryDelay = remaining;
                        }
                        await _options.Delay(retryDelay, _stopping.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                ReportFault(exception);
            }
        }

        private async Task<bool> RenewBeforeDeadlineAsync(
            DateTimeOffset now,
            DateTimeOffset nextExpiration,
            DateTimeOffset renewalDeadline)
        {
            using var deadlineCancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            var renewal = Task.Run(
                () => _store.RenewRuntimeGeneration(
                    _epoch,
                    _fenceToken,
                    _leaseExpiresAtUtc,
                    now,
                    nextExpiration,
                    renewalDeadline,
                    _options.TimeProvider,
                    _options.RenewalCommandTimeoutSeconds,
                    _options.RenewalCommandStarting),
                CancellationToken.None);
            var deadline = _options.DelayUntilDeadline(
                renewalDeadline - now,
                deadlineCancellation.Token);
            var completed = await Task.WhenAny(renewal, deadline).ConfigureAwait(false);
            if (ReferenceEquals(completed, renewal) || renewal.IsCompleted)
            {
                await deadlineCancellation.CancelAsync().ConfigureAwait(false);
                try
                {
                    await deadline.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (deadlineCancellation.IsCancellationRequested)
                {
                }
                return await renewal.ConfigureAwait(false);
            }

            try
            {
                await deadline.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                try
                {
                    _ = await renewal.ConfigureAwait(false);
                }
                catch
                {
                    // Disposal still observes the bounded renewal before completing the lease heartbeat.
                }
                _stopping.Token.ThrowIfCancellationRequested();
                throw;
            }
            ReportFault(new AgentRuntimeGenerationHeartbeatException(
                _epoch,
                new TimeoutException("The Runtime generation lease renewal did not finish before its safety deadline.")));

            try
            {
                _ = await renewal.ConfigureAwait(false);
            }
            catch
            {
                // The deadline fault is authoritative; the bounded renewal is observed only to avoid an orphan task.
            }
            return false;
        }

        private TimeSpan CalculateRetryDelay(int attempt)
        {
            var shift = Math.Min(attempt, 20);
            var multiplier = 1L << shift;
            var baseTicks = _options.RetryInitialDelay.Ticks > long.MaxValue / multiplier
                ? _options.RetryMaximumDelay.Ticks
                : Math.Min(
                    _options.RetryMaximumDelay.Ticks,
                    _options.RetryInitialDelay.Ticks * multiplier);
            var epochBytes = _epoch.ToByteArray();
            var jitterPercent = 90 + (epochBytes[attempt % epochBytes.Length] + attempt) % 21;
            var jitteredTicks = Math.Min(
                _options.RetryMaximumDelay.Ticks,
                baseTicks * jitterPercent / 100);
            return TimeSpan.FromTicks(Math.Max(1, jitteredTicks));
        }

        private void ReportFault(Exception exception)
        {
            if (Interlocked.Exchange(ref _faultReported, 1) == 0)
            {
                _store.FenceRuntimeGeneration(_epoch, _fenceToken);
                _faulted(exception);
            }
        }
    }
}
