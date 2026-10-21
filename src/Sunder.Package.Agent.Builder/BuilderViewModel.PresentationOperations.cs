using Sunder.Sdk.Abstractions;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderViewModel
{
    public Task LoadSelectedProjectAsync() => ProjectOperationAsync("load");
    public Task UnloadSelectedProjectAsync() => ProjectOperationAsync("unload");
    public Task BuildSelectedProjectAsync() => ProjectOperationAsync("build");
    public Task CreatePackageArchiveAsync() => ProjectOperationAsync("archive");
    public Task RetrySelectedSetupAsync() => ProjectOperationAsync("setup");
    public Task SaveSelectedMetadataAsync() => ProjectOperationAsync("save-metadata");

    private Task ProjectOperationAsync(string operation)
    {
        var row = SelectedProject;
        return row is null ? Task.CompletedTask : RunOwnedAsync(lifetime => RunProjectActionAsync(row,
            (generation, token) => ExecuteProjectOperationAsync(row, generation, operation, token), lifetime, operation));
    }

    private async Task ExecuteProjectOperationAsync(BuilderProjectViewModel row, long generation, string operation, CancellationToken token)
    {
        if (operation == "unload")
        {
            if (_sessions.TryGetValue(row.Id, out var released))
            {
                await ReleaseSessionAsync(released).ConfigureAwait(false);
                _sessions.TryRemove(row.Id, out _);
            }
            await UpdateUiAsync(() =>
            {
                row.SetSession(false);
                row.SetStatus("This Builder session stopped. Other owners may still have the package loaded.");
            }).ConfigureAwait(false);
            return;
        }

        var inspection = await ReadUiAsync(() => row.Inspection).ConfigureAwait(false);
        if (inspection is null || row.IsMissing) throw new InvalidOperationException("Locate this project's source first.");
        if (operation == "load" && _sessions.ContainsKey(row.Id)) return;
        var owner = _sessions.GetValueOrDefault(row.Id);
        if (owner?.ReleaseStarted == true)
            throw new InvalidOperationException("This development session is awaiting release. Reconnect and retry Unload before changing this project.");
        var wasLoaded = owner is not null;
        var name = await ReadUiAsync(() => row.EditedDisplayName.Trim()).ConfigureAwait(false);
        var packageId = await ReadUiAsync(() => row.EditedPackageId.Trim()).ConfigureAwait(false);
        if (operation == "save-metadata")
        {
            if (row.HasSourceConflict) throw new InvalidOperationException("Source changed externally. Discard edits and review the current source before saving.");
            if (name != inspection.DisplayName && !inspection.CanEditDisplayName)
                throw new InvalidOperationException(inspection.ReadOnlyReason ?? "The package name is read-only.");
            if (packageId != inspection.PackageId && !inspection.CanEditPackageId)
                throw new InvalidOperationException(inspection.PackageIdReadOnlyReason ?? "The package ID is read-only.");
        }

        var original = owner;
        var paused = false;
        var succeeded = false;
        var uncertain = false;
        var sourceRevision = owner?.SourceChanges?.Revision ?? 0;
        try
        {
            if (owner is not null)
            {
                await StopWatcherAsync(owner).ConfigureAwait(false);
                paused = true;
                await owner.Handle.SetWatchAsync(false, token).ConfigureAwait(false);
                if (operation == "save-metadata" && packageId != inspection.PackageId)
                {
                    await ReleaseSessionAsync(owner).ConfigureAwait(false);
                    _sessions.TryRemove(row.Id, out _);
                    owner = null;
                    await UpdateUiAsync(() => row.SetSession(false)).ConfigureAwait(false);
                }
            }

            var result = await RunRowJobAsync(row, generation, new()
            {
                Operation = operation == "load" ? "setup" : operation, Kind = inspection.Kind,
                ProjectFolder = inspection.ProjectFolder, EntryPoint = inspection.EntryPoint,
                Expected = operation == "save-metadata" ? inspection : null,
                DisplayName = operation == "save-metadata" ? name : null,
                PackageId = operation == "save-metadata" ? packageId : null,
            }, token).ConfigureAwait(false);
            if (result.State != "succeeded")
            {
                if (operation == "save-metadata") await RefreshSourceAsync(row, generation, token).ConfigureAwait(false);
                return;
            }
            if (operation == "save-metadata")
            {
                await UpdateUiAsync(() =>
                {
                    if (IsCurrent(row, generation)) { row.ApplySnapshot(result, acceptEdits: true); SaveBookmarks(); }
                }).ConfigureAwait(false);
                if (wasLoaded)
                {
                    sourceRevision = original?.SourceChanges?.Revision ?? sourceRevision;
                    result = await RunRowJobAsync(row, generation, Request(row, "build"), token).ConfigureAwait(false);
                }
                if (result.State != "succeeded") return;
            }

            // Source remains authoritative, including edits made by generated tooling during a build.
            var refreshed = await _application.InspectAsync(row.EntryPoint!, token).ConfigureAwait(false);
            await UpdateUiAsync(() => { if (IsCurrent(row, generation)) { row.ApplyInspection(refreshed); SaveBookmarks(); } }).ConfigureAwait(false);
            if (operation == "load" || wasLoaded && (operation is "build" or "setup" or "save-metadata"))
            {
                if (string.IsNullOrWhiteSpace(result.DevelopmentFolder)) throw new InvalidDataException("The build did not return a validated development folder.");
                token.ThrowIfCancellationRequested();
                if (owner is not null)
                {
                    // Manual success explicitly replaces this session even when the bytes are
                    // unchanged or Watch is off. Await exact watch stop and Host/App retirement
                    // before acquiring another owner; reacquiring the same owner is only a heartbeat.
                    await ReleaseSessionAsync(owner).ConfigureAwait(false);
                    owner = null;
                    await UpdateUiAsync(() => row.SetSession(false)).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                IPackageDevelopmentSession handle;
                try { handle = await _development.LoadAsync(result.DevelopmentFolder, row.Watch, token).ConfigureAwait(false); }
                catch (Exception error) when (wasLoaded && error is not OperationCanceledException && BuilderProjectApplicationService.IsRecoverable(error))
                {
                    throw new InvalidOperationException("The previous session was released, but the new development output could not be loaded. Retry Load into Sunder. " + error.Message, error);
                }
                owner = new(row, handle, _tasks.CancellationToken);
                _sessions[row.Id] = owner;
                await UpdateUiAsync(() => { row.SetSession(true); row.SetStatus("Development session acquired. Checking runtime and App readiness…"); }).ConfigureAwait(false);
                owner.MonitorTask = Task.Run(() => MonitorSessionAsync(owner));
            }
            succeeded = true;
            if (owner is not null && !ReferenceEquals(owner, original))
            {
                await owner.Handle.SetWatchAsync(row.Watch, token).ConfigureAwait(false);
                if (row.Watch) StartWatcher(owner);
            }
        }
        catch (BuilderJobCleanupException) { uncertain = true; throw; }
        finally
        {
            // A failed/cancelled manual operation must not silently turn a retained session into
            // "watch checked, producer stopped". Never use that operation's canceled token to restore it.
            if (original is not null && paused && !uncertain && !original.ReleaseStarted)
                await RestoreRetainedWatchAsync(original, generation, succeeded, sourceRevision).ConfigureAwait(false);
        }
    }

    private async Task RestoreRetainedWatchAsync(DevelopmentOwnership owner, long generation, bool succeeded, long sourceRevision)
    {
        if (_stopping || _tasks.CancellationToken.IsCancellationRequested) return;
        var row = owner.Row;
        using var recovery = CancellationTokenSource.CreateLinkedTokenSource(_tasks.CancellationToken);
        recovery.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var restore = await ReadUiAsync(() => !_stopping && IsCurrent(row, generation) && row.Watch
                && !owner.ReleaseStarted && owner.WatchStopConfirmed && ReferenceEquals(_sessions.GetValueOrDefault(row.Id), owner)
                && row.EntryPoint == owner.SourceEntryPoint && row.PackageId == owner.SourcePackageId
                && !_application.HasUnconfirmedJob(row.ProjectFolder)).ConfigureAwait(false);
            if (!restore) return;
            await owner.Handle.SetWatchAsync(true, recovery.Token).ConfigureAwait(false);
            if (!_stopping) StartWatcher(owner, waitForSourceChange: !succeeded, sourceRevision);
        }
        catch (OperationCanceledException error) when (PresentationCancellation.IsExpected(error, _tasks.CancellationToken)
            || _tasks.CancellationToken.IsCancellationRequested && PresentationCancellation.IsExpected(error, recovery.Token)) { }
        catch (Exception error) when ((error is not OperationCanceledException || PresentationCancellation.IsExpected(error, recovery.Token))
            && BuilderProjectApplicationService.IsRecoverable(error))
        {
            await UpdateUiAsync(() => row.SetWatchError("Automatic rebuilding could not restart: " + error.Message)).ConfigureAwait(false);
        }
    }

    public Task SetSelectedWatchAsync(bool watch)
    {
        var row = SelectedProject;
        return RunOwnedAsync(async lifetime =>
        {
            if (row is null) return;
            await RunProjectActionAsync(row, async (_, token) =>
            {
                await UpdateUiAsync(() => { row.Watch = watch; SaveBookmarks(); }).ConfigureAwait(false);
                if (!_sessions.TryGetValue(row.Id, out var owner)) return;
                if (owner.ReleaseStarted)
                    throw new InvalidOperationException("This development session is awaiting release. Reconnect and retry Unload before restarting watch.");
                await StopWatcherAsync(owner).ConfigureAwait(false);
                await owner.Handle.SetWatchAsync(watch, token).ConfigureAwait(false);
                var status = await owner.Handle.GetStatusAsync(token).ConfigureAwait(false);
                await UpdateUiAsync(() => row.SetDevelopmentStatus(status)).ConfigureAwait(false);
                if (watch) StartWatcher(owner);
            }, lifetime).ConfigureAwait(false);
        });
    }

    public Task RefreshSelectedProjectAsync()
    {
        var row = SelectedProject;
        return RunOwnedAsync(async token =>
        {
            if (row?.EntryPoint is null || row.IsBusy) return;
            var generation = await ReadUiAsync(() => row.OperationGeneration).ConfigureAwait(false);
            try { await RefreshSourceAsync(row, generation, token).ConfigureAwait(false); }
            catch (OperationCanceledException error) when (PresentationCancellation.IsExpected(error, token)) { }
            catch (Exception error) when (error is not OperationCanceledException && BuilderProjectApplicationService.IsRecoverable(error))
            {
                await UpdateUiAsync(() => { if (IsCurrent(row, generation)) row.SetMissing(error.Message); }).ConfigureAwait(false);
            }
        });
    }

    private async Task RefreshSourceAsync(BuilderProjectViewModel row, long generation, CancellationToken token)
    {
        var source = await _application.InspectAsync(row.EntryPoint!, token).ConfigureAwait(false);
        await UpdateUiAsync(() =>
        {
            if (!IsCurrent(row, generation)) return;
            var before = row.ToRecord();
            row.ApplyInspection(source);
            if (before != row.ToRecord()) SaveBookmarks();
        }).ConfigureAwait(false);
    }

    private async Task RunProjectActionAsync(BuilderProjectViewModel row, Func<long, CancellationToken, Task> action, CancellationToken lifetime, string operation = "operation")
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var generation = await ReadUiAsync(() =>
        {
            if (_stopping || row.IsBusy || !Projects.Contains(row)) return -1L;
            row.BeginOperation(operation);
            row.IsBusy = true;
            _operations[row.Id] = cancellation;
            return ++row.OperationGeneration;
        }).ConfigureAwait(false);
        if (generation < 0) return;
        try { await action(generation, cancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException error) when (PresentationCancellation.IsExpected(error, cancellation.Token))
        {
            await UpdateUiAsync(() => { if (IsCurrent(row, generation)) row.SetStatus("Operation cancelled."); }).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException && BuilderProjectApplicationService.IsRecoverable(error))
        {
            await UpdateUiAsync(() => { if (IsCurrent(row, generation)) row.SetError(error.Message); }).ConfigureAwait(false);
        }
        finally
        {
            _operations.TryRemove(new KeyValuePair<string, CancellationTokenSource>(row.Id, cancellation));
            await UpdateUiAsync(() =>
            {
                if (!IsCurrent(row, generation)) return;
                if (row.HasSession && !_sessions.ContainsKey(row.Id)) row.SetSession(false);
                row.IsBusy = false;
            }).ConfigureAwait(false);
        }
    }

    private Task<BuilderJobSnapshot> RunRowJobAsync(BuilderProjectViewModel row, long generation, BuilderJobRequest request, CancellationToken token)
        => _application.RunJobAsync(request with { DisplayName = request.DisplayName ?? row.DisplayName, PackageId = request.PackageId ?? row.PackageId }, snapshot => UpdateUiAsync(() =>
        {
            if (!IsCurrent(row, generation)) return;
            row.ApplySnapshot(snapshot);
            if (snapshot.State != "running") SaveBookmarks();
        }), token, projectId: row.Id);

    private static BuilderJobRequest Request(BuilderProjectViewModel row, string operation) => new()
    {
        Operation = operation, Kind = row.Kind!.Value, ProjectFolder = row.ProjectFolder, EntryPoint = row.EntryPoint,
    };

    private void StartWatcher(DevelopmentOwnership owner, bool waitForSourceChange = false, long sourceRevision = 0)
    {
        if (_stopping || _tasks.CancellationToken.IsCancellationRequested) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_tasks.CancellationToken);
        owner.WatchCancellation = cancellation;
        owner.WatchTask = Task.Run(async () =>
        {
            try
            {
                // The worker's watch job builds before starting its producer. If that build fails,
                // retry only after an authored source change rather than spinning on the same error.
                if (owner.SourceChanges is null && Directory.Exists(owner.Row.ProjectFolder))
                    owner.SourceChanges = new(owner.Row.ProjectFolder);
                while (true)
                {
                    if (waitForSourceChange)
                    {
                        if (owner.SourceChanges is null) throw new IOException("The source folder cannot be monitored. Retry rebuilding when it is accessible.");
                        await UpdateUiAsync(() => owner.Row.SetWatchWaitingForChanges(true)).ConfigureAwait(false);
                        await owner.SourceChanges.WaitForChangeAsync(sourceRevision, cancellation.Token).ConfigureAwait(false);
                    }
                    cancellation.Token.ThrowIfCancellationRequested();
                    sourceRevision = owner.SourceChanges?.Revision ?? 0;
                    var request = Request(owner.Row, "watch") with { Id = Guid.NewGuid().ToString("N") };
                    owner.WatchJobId = request.Id;
                    owner.WatchStopConfirmed = false;
                    await UpdateUiAsync(() => { owner.Row.SetWatchWaitingForChanges(false); owner.Row.SetWatchRunning(true); }).ConfigureAwait(false);
                    var result = await _application.RunJobAsync(request, snapshot => UpdateUiAsync(() =>
                    {
                        if (!_disposed && ReferenceEquals(owner.WatchCancellation, cancellation) && Projects.Contains(owner.Row)
                            && !cancellation.IsCancellationRequested)
                        {
                            owner.Row.ApplySnapshot(snapshot);
                        }
                    }), cancellation.Token).ConfigureAwait(false);
                    owner.WatchStopConfirmed = true;
                    await UpdateUiAsync(() => owner.Row.SetWatchRunning(false)).ConfigureAwait(false);
                    cancellation.Token.ThrowIfCancellationRequested();
                    await UpdateUiAsync(() => owner.Row.SetWatchError("Source watch stopped: " + (result.Error ?? result.Message))).ConfigureAwait(false);
                    waitForSourceChange = true;
                }
            }
            catch (OperationCanceledException error) when (PresentationCancellation.IsExpected(error, cancellation.Token))
            {
                owner.WatchStopConfirmed = owner.WatchStopConfirmed || _application.IsJobStopConfirmed(owner.WatchJobId);
            }
            catch (Exception error) when (error is not OperationCanceledException && BuilderProjectApplicationService.IsRecoverable(error))
            {
                // Cancellation/exception classification is not stop proof. Only a
                // no-Start admission, an exact terminal result or the captured Host
                // execution's cleanup receipt may release this retained owner.
                owner.WatchStopConfirmed = owner.WatchStopConfirmed || _application.IsJobStopConfirmed(owner.WatchJobId);
                await UpdateUiAsync(() =>
                {
                    if (!owner.WatchStopConfirmed) owner.Row.SetWatchConnectionState(BuilderJobConnectionState.StopUnconfirmed);
                    owner.Row.SetWatchError("Automatic rebuilding could not restart: " + error.Message);
                }).ConfigureAwait(false);
            }
            finally { await UpdateUiAsync(() => { owner.Row.SetWatchRunning(false); owner.Row.SetWatchWaitingForChanges(false); }).ConfigureAwait(false); }
        });
    }

    private async Task StopWatcherAsync(DevelopmentOwnership owner)
    {
        if (owner.WatchCancellation is not { } cancellation) return;
        List<Exception> errors = [];
        await CaptureCleanupFailureAsync(errors, cancellation.CancelAsync).ConfigureAwait(false);
        if (owner.WatchTask is { } watching)
            await CaptureCleanupFailureAsync(errors, () => watching).ConfigureAwait(false);
        if (!owner.WatchStopConfirmed)
        {
            await CaptureCleanupFailureAsync(errors, async () =>
            {
                var stopped = await _application.StopJobAsync(owner.WatchJobId!).ConfigureAwait(false);
                if (stopped.JobId != owner.WatchJobId) throw new InvalidDataException("Builder reconciled a different watch job.");
                owner.WatchStopConfirmed = true;
            }).ConfigureAwait(false);
        }
        if (owner.WatchStopConfirmed)
        {
            await UpdateUiAsync(() => owner.Row.SetWatchConnectionState(BuilderJobConnectionState.Connected)).ConfigureAwait(false);
            cancellation.Dispose();
            owner.WatchCancellation = null;
            owner.WatchTask = null;
            owner.WatchJobId = null;
        }
        ThrowCleanupFailures(errors);
    }

    private async Task MonitorSessionAsync(DevelopmentOwnership owner)
    {
        var token = owner.MonitorCancellation.Token;
        PackageDevelopmentStatus? last = null;
        var observations = 0;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    last = await owner.Handle.GetStatusAsync(token).ConfigureAwait(false);
                    await _ui.InvokeAsync(() =>
                    {
                        if (!_disposed && !token.IsCancellationRequested && Projects.Contains(owner.Row))
                        {
                            owner.Row.SetDevelopmentStatus(last);
                            if (owner.SourceChanges?.LastChangeAtUtc is { } changed) owner.Row.ObserveSourceChange(changed);
                        }
                    }, token).ConfigureAwait(false);
                    if (++observations % 3 == 0 && !owner.Row.IsBusy)
                    {
                        var generation = await _ui.InvokeAsync(() => owner.Row.OperationGeneration, token).ConfigureAwait(false);
                        await RefreshSourceAsync(owner.Row, generation, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException error) when (PresentationCancellation.IsExpected(error, token)) { throw; }
                catch (Exception error) when (error is not OperationCanceledException && BuilderProjectApplicationService.IsRecoverable(error))
                {
                    var unavailable = (last ?? new(owner.Handle.PackageId, false, false, "Unknown", null, false))
                        with { Readiness = "Unavailable", ErrorMessage = error.Message };
                    await _ui.InvokeAsync(() => { if (!token.IsCancellationRequested) owner.Row.SetDevelopmentStatus(unavailable); }, token).ConfigureAwait(false);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException error) when (PresentationCancellation.IsExpected(error, token)) { }
    }

    private async Task ReleaseSessionAsync(DevelopmentOwnership owner)
    {
        owner.ReleaseStarted = true;
        List<Exception> errors = [];
        await CaptureCleanupFailureAsync(errors, owner.MonitorCancellation.CancelAsync).ConfigureAwait(false);
        await CaptureCleanupFailureAsync(errors, () => StopWatcherAsync(owner)).ConfigureAwait(false);
        await CaptureCleanupFailureAsync(errors, () => owner.MonitorTask).ConfigureAwait(false);
        if (!owner.WatchStopConfirmed)
        {
            // Keep the exact job, cancellation source and lease available for a later Unload
            // reconciliation, or for host quarantine if this activation is being disposed.
            // Cancelling a request (or losing its response) is not proof that its worker stopped.
            throw new BuilderJobCleanupException(
                $"Builder watch job '{owner.WatchJobId}' has not confirmed shutdown. The development session for '{owner.SourcePackageId}' remains owned. Reconnect and retry Unload before changing this project or SDK.",
                new AggregateException("Builder watch cleanup could not be proven.", errors));
        }
        var released = false;
        await CaptureCleanupFailureAsync(errors, async () =>
        {
            await owner.Handle.DisposeAsync().ConfigureAwait(false);
            released = true;
        }).ConfigureAwait(false);
        if (released)
        {
            owner.MonitorCancellation.Dispose();
            owner.WatchCancellation?.Dispose();
            try { owner.SourceChanges?.Dispose(); }
            catch (Exception error) { errors.Add(error); }
            // Monitor/callback failure still releases a proven-stopped owner. Failed handle
            // release instead retains the exact owner and cleanup resources for Unload retry.
            _sessions.TryRemove(new KeyValuePair<string, DevelopmentOwnership>(owner.Row.Id, owner));
        }
        ThrowCleanupFailures(errors);
    }
}
