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
        var sourceRevision = 0L;
        try
        {
            if (owner is not null)
            {
                sourceRevision = row.Watch ? owner.ObservePause() : 0;
                await owner.StopWatchAsync().ConfigureAwait(false);
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

            if (operation == "save-metadata")
            {
                var saved = await RunRowJobAsync(row, generation, new()
                {
                    Operation = operation, Kind = inspection.Kind, ProjectFolder = inspection.ProjectFolder, EntryPoint = inspection.EntryPoint,
                    Expected = inspection, DisplayName = name, PackageId = packageId,
                }, token).ConfigureAwait(false);
                if (saved.State != "succeeded")
                {
                    await RefreshSourceAsync(row, generation, token).ConfigureAwait(false);
                    return;
                }
                await UpdateUiAsync(() =>
                {
                    if (IsCurrent(row, generation)) { row.ApplySnapshot(saved, acceptEdits: true); SaveBookmarks(); }
                }).ConfigureAwait(false);
                if (!wasLoaded) { succeeded = true; return; }
            }

            var loadOutput = operation == "load" || wasLoaded && (operation is "build" or "setup" or "save-metadata");
            if (loadOutput && inspection.Kind == BuilderProjectKind.Dotnet && row.Watch)
            {
                // Initial Load and manual reload use the same Watch-owned build. A finite
                // build followed by Watch would compile/analyze the project twice.
                paused = false;
                try { await BuildAndLoadWatchedDotnetAsync(row, generation, owner, token).ConfigureAwait(false); }
                catch (OperationCanceledException error) when (PresentationCancellation.IsExpected(error, token))
                {
                    if (owner is not null) await RestoreRetainedWatchAsync(owner, generation, succeeded: false, sourceRevision: 0).ConfigureAwait(false);
                    throw;
                }
                return;
            }

            var buildOperation = operation == "load" ? "setup" : operation == "save-metadata" ? "build" : operation;
            var result = await RunRowJobAsync(row, generation, Request(row, buildOperation), token).ConfigureAwait(false);
            if (result.State != "succeeded") return;
            // Source remains authoritative, including edits made by generated tooling during a build.
            await RefreshSourceAsync(row, generation, token).ConfigureAwait(false);
            if (loadOutput) owner = await LoadOutputAsync(row, owner, result.DevelopmentFolder, token).ConfigureAwait(false);
            succeeded = true;
            if (owner is not null && !ReferenceEquals(owner, original) && row.Watch) owner.StartWatch();
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

    private async Task RestoreRetainedWatchAsync(BuilderProjectSession owner, long generation, bool succeeded, long sourceRevision)
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
            if (!_stopping) owner.StartWatch(waitForSourceChange: !succeeded, sourceRevision);
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
                await owner.StopWatchAsync().ConfigureAwait(false);
                await owner.Handle.SetWatchAsync(watch, token).ConfigureAwait(false);
                var status = await owner.Handle.GetStatusAsync(token).ConfigureAwait(false);
                await UpdateUiAsync(() => row.SetDevelopmentStatus(status)).ConfigureAwait(false);
                if (watch) owner.StartWatch();
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
            await UpdateUiAsync(() => { if (IsCurrent(row, generation)) row.SetError(error); }).ConfigureAwait(false);
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

    private BuilderProjectSession CreateProjectSession(BuilderProjectViewModel row)
    {
        BuilderProjectSession? owner = null;
        owner = new(row, _application, _development, _tasks.CancellationToken,
            action => UpdateUiAsync(() =>
            {
                if (!_disposed && Projects.Contains(row) && ReferenceEquals(_sessions.GetValueOrDefault(row.Id), owner)) action();
            }), async () =>
            {
                var generation = await ReadUiAsync(() => row.IsBusy ? -1 : row.OperationGeneration).ConfigureAwait(false);
                if (generation >= 0) await RefreshSourceAsync(row, generation, _tasks.CancellationToken).ConfigureAwait(false);
            });
        return owner;
    }

    private async Task BuildAndLoadWatchedDotnetAsync(BuilderProjectViewModel row, long generation,
        BuilderProjectSession? owner, CancellationToken token)
    {
        owner ??= CreateProjectSession(row);
        _sessions[row.Id] = owner; // Own the producer before a development handle exists.
        try
        {
            var built = await owner.StartAndWaitForBuildAsync(token, waitForRecovery: !owner.HasHandle).ConfigureAwait(false);
            if (built.State is "failed" or "cancelled") return;
            if (built.Phase == BuilderJobPhase.BuildFailed)
            {
                // A manual compiler failure returns control without retiring last-good output.
                // The same Watch job recovers on edits; no new job or second observer is needed.
                await owner.Handle.SetWatchAsync(true, token).ConfigureAwait(false);
                return;
            }
            await RefreshSourceAsync(row, generation, token).ConfigureAwait(false);
            await UpdateUiAsync(() => { if (IsCurrent(row, generation)) SaveBookmarks(); }).ConfigureAwait(false);
            await LoadOutputAsync(row, owner, built.DevelopmentFolder, token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            try
            {
                if (error is OperationCanceledException cancellation && PresentationCancellation.IsExpected(cancellation, token)
                    && owner.HasHandle && !owner.ReleaseStarted) owner.ObservePause();
            }
            finally { await owner.StopWatchAsync().ConfigureAwait(false); }
            throw;
        }
        finally
        {
            if (!owner.HasHandle)
            {
                // A cancelled/failed load may already own a Worker. Drain that exact
                // producer before dropping the pending session, independent of the caller.
                try { await ReleaseSessionAsync(owner).ConfigureAwait(false); }
                finally
                {
                    await UpdateUiAsync(() => row.SetSession(!owner.Released)).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<BuilderProjectSession> LoadOutputAsync(BuilderProjectViewModel row, BuilderProjectSession? owner,
        string? folder, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new InvalidDataException("The build did not return a validated development folder.");
        owner ??= CreateProjectSession(row);
        _sessions[row.Id] = owner;
        try
        {
            try { await owner.LoadAsync(folder, row.Watch, token).ConfigureAwait(false); }
            catch (Exception error) when (!owner.HasHandle && error is not OperationCanceledException && BuilderProjectApplicationService.IsRecoverable(error))
            {
                throw new InvalidOperationException("The development output could not be loaded. Retry Load into Sunder. " + error.Message, error);
            }
            return owner;
        }
        finally
        {
            if (!owner.HasHandle) await ReleaseSessionAsync(owner).ConfigureAwait(false);
        }
    }

    private async Task ReleaseSessionAsync(BuilderProjectSession owner)
    {
        try { await owner.ReleaseAsync().ConfigureAwait(false); }
        finally
        {
            if (owner.Released) _sessions.TryRemove(new KeyValuePair<string, BuilderProjectSession>(owner.Row.Id, owner));
        }
    }
}
