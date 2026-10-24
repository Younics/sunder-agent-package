using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderViewModel
{
    public Task LoadSelectedProjectAsync() => ProjectOperationAsync("load");
    public Task UnloadSelectedProjectAsync() => ProjectOperationAsync("unload");
    public Task BuildSelectedProjectAsync() => ProjectOperationAsync("build");
    public Task HotreloadSelectedProjectAsync() => ProjectOperationAsync("hotreload");
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
            if (_packageOperations.GetSession(row.EntryPoint) is { } released)
            {
                await ReleaseSessionAsync(released).ConfigureAwait(false);
            }
            await UpdateUiAsync(() =>
            {
                row.SetSession(false);
                row.SetStatus("This Builder session stopped. Other owners may still have the package loaded.");
            }).ConfigureAwait(false);
            return;
        }

        var owner = _packageOperations.GetSession(row.EntryPoint);
        var inspection = await ReadUiAsync(() => row.Inspection).ConfigureAwait(false);
        if (operation == "hotreload" && owner?.PendingHotreload is not null) inspection = owner.Project;
        else if (inspection is null || row.IsMissing) throw new InvalidOperationException("Locate this project's source first.");
        if (owner?.ReleaseStarted == true)
            throw new InvalidOperationException("This development session is awaiting release. Reconnect and retry Unload before changing this project.");
        if (operation == "load" && owner is not null)
        {
            await UpdateUiAsync(() => { row.SetSession(owner.HasHandle); if (owner.Status is { } status) row.SetDevelopmentStatus(status); }).ConfigureAwait(false);
            return;
        }
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

        if (operation == "save-metadata")
        {
            // Identity edits must release the exact old owner first. Editing metadata
            // does not silently rebuild or acquire a different package identity.
            if (owner is not null && packageId != inspection.PackageId)
            {
                await ReleaseSessionAsync(owner).ConfigureAwait(false);
                await UpdateUiAsync(() => row.SetSession(false)).ConfigureAwait(false);
            }
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
            return;
        }

        if (operation == "hotreload")
        {
            var applied = await _packageOperations.HotreloadAsync(inspection!, snapshot => ApplyRowSnapshotAsync(row, generation, snapshot), token, row.Id).ConfigureAwait(false);
            await RefreshSourceAsync(row, generation, token).ConfigureAwait(false);
            await UpdateUiAsync(() => { if (IsCurrent(row, generation)) row.SetStatus("Hotreload applied revision " + applied.SourceContentIdentity[..12] + "."); }).ConfigureAwait(false);
            return;
        }

        var result = await RunRowJobAsync(row, generation, Request(row, operation == "load" ? "setup" : operation), token).ConfigureAwait(false);
        if (result.State != "succeeded") return;
        if (operation is "load" or "build" or "setup" && string.IsNullOrWhiteSpace(result.DevelopmentFolder))
            throw new InvalidDataException("The build did not return a validated development folder.");
        // A build result is not application evidence. Only explicit Load acquires a
        // development owner; Build/setup/archive never retire or reload a loaded view.
        await RefreshSourceAsync(row, generation, token).ConfigureAwait(false);
        if (operation == "load") await LoadOutputAsync(row, owner, result.DevelopmentFolder, token).ConfigureAwait(false);
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
                if (row.HasSession && _packageOperations.GetSession(row.EntryPoint) is null) row.SetSession(false);
                row.IsBusy = false;
            }).ConfigureAwait(false);
        }
    }

    private Task<BuilderJobSnapshot> RunRowJobAsync(BuilderProjectViewModel row, long generation, BuilderJobRequest request, CancellationToken token)
        => _packageOperations.RunJobAsync(request with { DisplayName = request.DisplayName ?? row.DisplayName, PackageId = request.PackageId ?? row.PackageId },
            snapshot => ApplyRowSnapshotAsync(row, generation, snapshot), token, projectId: row.Id);

    private Task ApplyRowSnapshotAsync(BuilderProjectViewModel row, long generation, BuilderJobSnapshot snapshot) => UpdateUiAsync(() =>
        {
            if (!IsCurrent(row, generation)) return;
            row.ApplySnapshot(snapshot);
            if (snapshot.State != "running") SaveBookmarks();
        });

    private static BuilderJobRequest Request(BuilderProjectViewModel row, string operation) => new()
    {
        Operation = operation, Kind = row.Kind!.Value, ProjectFolder = row.ProjectFolder, EntryPoint = row.EntryPoint,
    };

    private void OnDevelopmentChanged(BuilderDevelopmentSession session)
    {
        if (_stopping || _developmentPresentationFailures.ContainsKey(session.SourceEntryPoint)) return;
        _ = _tasks.RunAsync(async token =>
        {
            try
            {
            BuilderProjectViewModel? row = null;
            await UpdateUiAsync(() =>
            {
                row = Projects.SingleOrDefault(item => item.EntryPoint == session.SourceEntryPoint);
                if (row is null) return;
                row.SetSession(!session.Released && session.HasHandle);
                row.SetHotreloadPending(session.PendingHotreload is not null && !session.Released);
                if (session.ReleaseStarted && !session.Released) row.SetReleasePending();
                else if (session.Status is { } status) row.SetDevelopmentStatus(status);
            }).ConfigureAwait(false);
            if (row is null || session.ReleaseStarted || session.Released || session.Status is null) return;
            var now = DateTimeOffset.UtcNow;
            if (_sourceObservations.TryGetValue(row.Id, out var last) && now - last < TimeSpan.FromSeconds(3)) return;
            _sourceObservations[row.Id] = now;
            var generation = await ReadUiAsync(() => row.IsBusy ? -1 : row.OperationGeneration).ConfigureAwait(false);
            if (generation >= 0) await RefreshSourceAsync(row, generation, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (PresentationCancellation.IsExpected(error, token)) { }
            catch (Exception error)
            {
                // The activation, not an event handler or the current page, owns
                // observation failures. Preserve the exact failure for its drain;
                // stop this row's presentation without abandoning its lease.
                _developmentPresentationFailures.TryAdd(session.SourceEntryPoint, error);
            }
        });
    }

    private async Task<BuilderDevelopmentSession> LoadOutputAsync(BuilderProjectViewModel row, BuilderDevelopmentSession? owner,
        string? folder, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new InvalidDataException("The build did not return a validated development folder.");
        try
        {
            var session = await _packageOperations.LoadAsync(row.Inspection!, folder, token).ConfigureAwait(false);
            await UpdateUiAsync(() =>
            {
                row.SetSession(true);
                row.SetStatus("Development session acquired. Checking runtime and App readiness…");
            }).ConfigureAwait(false);
            return session;
        }
        catch (Exception error) when (error is not OperationCanceledException && BuilderProjectApplicationService.IsRecoverable(error))
        {
            throw new InvalidOperationException("The development output could not be loaded. Retry Load into Sunder. " + error.Message, error);
        }
    }

    private async Task ReleaseSessionAsync(BuilderDevelopmentSession owner)
    {
        List<Exception> failures = [];
        await CaptureCleanupFailureAsync(failures, () => UpdateUiAsync(() =>
        {
            foreach (var row in Projects.Where(item => item.EntryPoint == owner.SourceEntryPoint)) row.SetReleasePending();
        })).ConfigureAwait(false);
        await CaptureCleanupFailureAsync(failures, () => _packageOperations.ReleaseAsync(owner)).ConfigureAwait(false);
        if (owner.Released)
        {
            await CaptureCleanupFailureAsync(failures, () => UpdateUiAsync(() =>
            {
                foreach (var row in Projects.Where(item => item.EntryPoint == owner.SourceEntryPoint)) row.SetSession(false);
            })).ConfigureAwait(false);
        }
        if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Builder development release failed.", failures);
    }
}
