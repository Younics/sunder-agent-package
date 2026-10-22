using System.Runtime.ExceptionServices;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Builder;

/// <summary>One project's producer, development handle and readiness monitor. Views borrow its state.</summary>
internal sealed class BuilderProjectSession(
    BuilderProjectViewModel row, BuilderProjectApplicationService application, IPackageDevelopmentService development, CancellationToken lifetime,
    Func<Action, Task> update, Func<Task> refreshSource)
{
    private IPackageDevelopmentSession? _handle;
    private CancellationTokenSource? _watchCancellation;
    private Task? _watchTask;
    private string? _watchJobId;
    private BuilderSourceChangeMonitor? _pausedSourceChanges;
    private CancellationTokenSource? _monitorCancellation;
    private Task _monitorTask = Task.CompletedTask;

    internal BuilderProjectViewModel Row { get; } = row;
    internal string SourcePackageId { get; } = row.PackageId;
    internal string? SourceEntryPoint { get; } = row.EntryPoint;
    internal IPackageDevelopmentSession Handle => _handle ?? throw new InvalidOperationException("No development handle has been acquired.");
    internal bool HasHandle => _handle is not null;
    internal bool WatchStopConfirmed { get; private set; } = true;
    internal bool ReleaseStarted { get; private set; }
    internal bool Released { get; private set; }

    internal async Task LoadAsync(string folder, bool watch, CancellationToken token)
    {
        if (ReleaseStarted) throw new InvalidOperationException("Retry Unload before acquiring another development handle.");
        token.ThrowIfCancellationRequested();
        if (_handle is not null)
        {
            // Reload retires only the old development handle, not the new source producer.
            // A lost release acknowledgement retains the exact handle for Unload retry.
            ReleaseStarted = true;
            List<Exception> errors = [];
            await CaptureAsync(errors, _monitorCancellation!.CancelAsync).ConfigureAwait(false);
            await CaptureAsync(errors, () => _monitorTask).ConfigureAwait(false);
            await CaptureAsync(errors, () => update(Row.SetReleasePending)).ConfigureAwait(false);
            await CaptureAsync(errors, async () =>
            {
                await _handle.DisposeAsync().ConfigureAwait(false);
                _handle = null;
                _monitorCancellation.Dispose();
                _monitorCancellation = null;
                ReleaseStarted = false;
                await update(() => Row.SetSession(false)).ConfigureAwait(false);
            }).ConfigureAwait(false);
            ThrowFailures(errors);
        }
        token.ThrowIfCancellationRequested();
        _handle = await development.LoadAsync(folder, watch, token).ConfigureAwait(false);
        _monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _monitorTask = Task.Run(MonitorAsync);
        await update(() => { Row.SetSession(true); Row.SetStatus("Development session acquired. Checking runtime and App readiness…"); }).ConfigureAwait(false);
    }

    internal long ObservePause()
    {
        // Only the manual-operation handoff needs an App-side source subscription.
        // Normal source observation and compiler-error recovery belong to the Worker.
        if (_pausedSourceChanges is null && Directory.Exists(Row.ProjectFolder))
            _pausedSourceChanges = new(Row.ProjectFolder);
        return _pausedSourceChanges?.Revision ?? 0;
    }

    internal void StartWatch(bool waitForSourceChange = false, long sourceRevision = 0)
        => StartWatchCore(waitForSourceChange, sourceRevision, null, waitForRecovery: true);

    internal async Task<BuilderJobSnapshot> StartAndWaitForBuildAsync(CancellationToken token, bool waitForRecovery)
    {
        var ready = new TaskCompletionSource<BuilderJobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartWatchCore(false, 0, ready, waitForRecovery);
        try { return await ready.Task.WaitAsync(token).ConfigureAwait(false); }
        catch
        {
            ready.TrySetCanceled(token);
            _ = ready.Task.Exception; // A late startup fault cannot become an unobserved readiness task.
            throw;
        }
    }

    private void StartWatchCore(bool waitForSourceChange, long sourceRevision, TaskCompletionSource<BuilderJobSnapshot>? ready, bool waitForRecovery)
    {
        lifetime.ThrowIfCancellationRequested();
        if (_watchCancellation is not null || ReleaseStarted) throw new InvalidOperationException("Stop the owned source watcher before starting another one.");
        var cancellation = _watchCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _watchTask = Task.Run(async () =>
        {
            try
            {
                if (waitForSourceChange)
                {
                    if (_pausedSourceChanges is null) throw new IOException("The source folder cannot be monitored. Retry rebuilding when it is accessible.");
                    await update(() => Row.SetWatchWaitingForChanges(true)).ConfigureAwait(false);
                    await _pausedSourceChanges.WaitForChangeAsync(sourceRevision, cancellation.Token).ConfigureAwait(false);
                }
                _pausedSourceChanges?.Dispose();
                _pausedSourceChanges = null;
                cancellation.Token.ThrowIfCancellationRequested();
                var request = new BuilderJobRequest { Id = Guid.NewGuid().ToString("N"), Operation = "watch", Kind = Row.Kind!.Value,
                    ProjectFolder = Row.ProjectFolder, EntryPoint = SourceEntryPoint, PackageId = SourcePackageId, DisplayName = Row.DisplayName };
                await update(() => { Row.SetWatchWaitingForChanges(false); Row.SetWatchRunning(true); }).ConfigureAwait(false);
                _watchJobId = request.Id;
                WatchStopConfirmed = false;
                var result = await application.RunJobAsync(request, async snapshot =>
                {
                    await update(() =>
                    {
                        if (!cancellation.IsCancellationRequested) Row.ApplySnapshot(snapshot);
                    }).ConfigureAwait(false);
                    // Polling can miss the transient BuildReady phase. A published, validated
                    // folder plus fresh successful-build evidence also arrives in Watching.
                    if (snapshot.State == "running" && (!waitForRecovery && snapshot.Phase == BuilderJobPhase.BuildFailed
                        || snapshot.DevelopmentFolder is not null && snapshot.LastSuccessfulBuildAtUtc is not null
                            && snapshot.Phase is BuilderJobPhase.BuildReady or BuilderJobPhase.Watching))
                        ready?.TrySetResult(snapshot);
                }, cancellation.Token, Row.Id).ConfigureAwait(false);
                WatchStopConfirmed = true;
                ready?.TrySetResult(result);
                cancellation.Token.ThrowIfCancellationRequested();
                await update(() => Row.SetWatchError("Source watch stopped: " + (result.Error ?? result.Message))).ConfigureAwait(false);
                // Compiler errors recover inside the same Worker job. A terminal infrastructure
                // failure requires an explicit retry; source edits must not launch new jobs forever.
            }
            catch (OperationCanceledException error) when (PresentationCancellation.IsExpected(error, cancellation.Token))
            {
                WatchStopConfirmed |= application.IsJobStopConfirmed(_watchJobId);
                ready?.TrySetCanceled(cancellation.Token);
            }
            catch (Exception error)
            {
                WatchStopConfirmed |= application.IsJobStopConfirmed(_watchJobId);
                ready?.TrySetException(error);
                if (error is OperationCanceledException || !BuilderProjectApplicationService.IsRecoverable(error)) throw;
                await update(() =>
                {
                    if (!WatchStopConfirmed) Row.SetWatchConnectionState(BuilderJobConnectionState.StopUnconfirmed);
                    Row.SetWatchError("Automatic rebuilding stopped: " + error.Message);
                }).ConfigureAwait(false);
            }
            finally
            {
                await update(() => { Row.SetWatchRunning(false); Row.SetWatchWaitingForChanges(false); }).ConfigureAwait(false);
            }
        });
    }

    internal async Task StopWatchAsync()
    {
        if (_watchCancellation is not { } cancellation) return;
        List<Exception> errors = [];
        await CaptureAsync(errors, cancellation.CancelAsync).ConfigureAwait(false);
        if (_watchTask is { } watching) await CaptureAsync(errors, () => watching).ConfigureAwait(false);
        if (!WatchStopConfirmed && _watchJobId is { } jobId)
        {
            await CaptureAsync(errors, async () =>
            {
                var stopped = await application.StopJobAsync(jobId).ConfigureAwait(false);
                if (stopped.JobId != jobId) throw new InvalidDataException("Builder reconciled a different watch job.");
                WatchStopConfirmed = true;
            }).ConfigureAwait(false);
        }
        if (WatchStopConfirmed)
        {
            cancellation.Dispose();
            _watchCancellation = null;
            _watchTask = null;
            _watchJobId = null;
            await CaptureAsync(errors, () => update(() => Row.SetWatchConnectionState(BuilderJobConnectionState.Connected))).ConfigureAwait(false);
        }
        ThrowFailures(errors);
    }

    private async Task MonitorAsync()
    {
        var token = _monitorCancellation!.Token;
        PackageDevelopmentStatus? last = null;
        var observations = 0;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    last = await Handle.GetStatusAsync(token).ConfigureAwait(false);
                    await update(() => { if (!token.IsCancellationRequested) Row.SetDevelopmentStatus(last); }).ConfigureAwait(false);
                    // Live Watch already reports fresh inspections after builds. Do not rescan
                    // the authored/generated tree every three seconds alongside the compiler.
                    if (++observations % 3 == 0 && _watchTask is null) await refreshSource().ConfigureAwait(false);
                }
                catch (OperationCanceledException error) when (PresentationCancellation.IsExpected(error, token)) { throw; }
                catch (Exception error) when (error is not OperationCanceledException && BuilderProjectApplicationService.IsRecoverable(error))
                {
                    var unavailable = (last ?? new(Handle.PackageId, false, false, PackageDevelopmentRuntimeReadiness.Unknown, null, false))
                        with { IsLoaded = false, HostState = PackageDevelopmentHostState.Reconnecting,
                            RuntimeReadiness = PackageDevelopmentRuntimeReadiness.Unknown, AppReadiness = PackageDevelopmentAppReadiness.Unavailable,
                            RuntimeRevision = null, AppliedRevision = null, ErrorMessage = error.Message };
                    await update(() => { if (!token.IsCancellationRequested) Row.SetDevelopmentStatus(unavailable); }).ConfigureAwait(false);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException error) when (PresentationCancellation.IsExpected(error, token)) { }
    }

    internal async Task ReleaseAsync()
    {
        if (Released) return;
        ReleaseStarted = true;
        List<Exception> errors = [];
        if (_monitorCancellation is not null) await CaptureAsync(errors, _monitorCancellation.CancelAsync).ConfigureAwait(false);
        await CaptureAsync(errors, StopWatchAsync).ConfigureAwait(false);
        await CaptureAsync(errors, () => _monitorTask).ConfigureAwait(false);
        await CaptureAsync(errors, () => update(Row.SetReleasePending)).ConfigureAwait(false);
        if (!WatchStopConfirmed)
            throw new BuilderJobCleanupException(
                $"Builder watch job '{_watchJobId}' has not confirmed shutdown. The development session for '{SourcePackageId}' remains owned. Reconnect and retry Unload before changing this project or SDK.",
                new AggregateException("Builder watch cleanup could not be proven.", errors));
        await CaptureAsync(errors, async () =>
        {
            if (_handle is not null) await _handle.DisposeAsync().ConfigureAwait(false);
            _handle = null;
            Released = true;
        }).ConfigureAwait(false);
        if (Released)
        {
            _monitorCancellation?.Dispose();
            _watchCancellation?.Dispose();
            try { _pausedSourceChanges?.Dispose(); }
            catch (Exception error) { errors.Add(error); }
        }
        ThrowFailures(errors);
    }

    private static async Task CaptureAsync(List<Exception> errors, Func<Task> operation)
    {
        try { await operation().ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
    }

    private static void ThrowFailures(List<Exception> errors)
    {
        if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException("Builder development session cleanup failed.", errors);
    }
}
