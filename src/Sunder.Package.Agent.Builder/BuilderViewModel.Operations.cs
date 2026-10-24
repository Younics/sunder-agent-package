using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderViewModel
{
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Task task;
        lock (_lifecycleSync)
        {
            if (_stopping) return Task.CompletedTask;
            task = _initialization ??= RunOwnedAsync(async token =>
            {
                var loaded = false;
                try
                {
                    var records = await _application.LoadProjectsAsync(token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    var rows = await ReadUiAsync(() =>
                    {
                        foreach (var record in records)
                            if (Projects.All(row => row.Id != record.Id)) Projects.Add(new(record));
                        _projectsLoaded = true;
                        StatusText = string.Empty;
                        return Projects.ToArray();
                    }).ConfigureAwait(false);
                    loaded = true;
                    foreach (var row in rows)
                    {
                        token.ThrowIfCancellationRequested();
                        var generation = await ReadUiAsync(() => row.OperationGeneration).ConfigureAwait(false);
                        try
                        {
                            BuilderProjectInspection? inspection;
                            if (!string.IsNullOrEmpty(row.EntryPoint))
                                inspection = await _application.InspectAsync(row.EntryPoint, token).ConfigureAwait(false);
                            else if (Path.IsPathFullyQualified(row.ProjectFolder))
                            {
                                var candidates = await _application.DiscoverAsync(row.ProjectFolder, token).ConfigureAwait(false);
                                inspection = candidates.Count == 1 ? candidates[0] : null;
                            }
                            else inspection = null;
                            await UpdateUiAsync(() =>
                            {
                                if (!IsCurrent(row, generation)) return;
                                if (inspection is null) row.SetMissing("Locate a local package folder to resolve this bookmark.");
                                else
                                {
                                    var before = row.ToRecord();
                                    var duplicate = Projects.FirstOrDefault(other => other != row && other.EntryPoint is { } entry
                                        && _paths.SameEntryPoint(entry, inspection.EntryPoint));
                                    if (duplicate is not null)
                                    {
                                        if (ReferenceEquals(SelectedProject, row)) _listDetail.ShowExistingDetail(duplicate);
                                        Projects.Remove(row);
                                    }
                                    else row.ApplyInspection(inspection);
                                    if (before != row.ToRecord() || duplicate is not null) SaveBookmarks();
                                }
                            }).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException error) when (PresentationCancellation.IsExpected(error, token)) { throw; }
                        catch (Exception error) when (error is not OperationCanceledException && BuilderProjectApplicationService.IsRecoverable(error))
                        {
                            await UpdateUiAsync(() => { if (IsCurrent(row, generation)) row.SetMissing(error.Message); }).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException error) when (PresentationCancellation.IsExpected(error, token)) { }
                catch (Exception error) when (error is not OperationCanceledException && BuilderProjectApplicationService.IsRecoverable(error))
                {
                    await UpdateUiAsync(() => StatusText = "Sunder Package Maker initialization failed: " + error.Message).ConfigureAwait(false);
                }
                finally { if (!loaded) lock (_lifecycleSync) _initialization = null; }
            });
        }
        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }

    public Task OpenFolderAsync(string folder) => RunOwnedAsync(token => DiscoverFolderAsync(folder, null, token));
    public Task LocateSelectedProjectAsync(string folder) => SelectedProject is { } row ? LocateProjectAsync(row, folder) : Task.CompletedTask;

    public Task LocateProjectAsync(BuilderProjectViewModel row, string folder) => RunOwnedAsync(lifetime =>
        RunProjectActionAsync(row, async (generation, token) =>
        {
            if (_packageOperations.GetSession(row.EntryPoint) is { } owner)
            {
                await ReleaseSessionAsync(owner).ConfigureAwait(false);
                await UpdateUiAsync(() => row.SetSession(false)).ConfigureAwait(false);
            }
            await DiscoverFolderAsync(folder, row, token).ConfigureAwait(false);
        }, lifetime));

    private async Task DiscoverFolderAsync(string folder, BuilderProjectViewModel? locating, CancellationToken token)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        if (!_projectsLoaded) return;
        if (!Path.IsPathFullyQualified(folder)) throw new ArgumentException("Choose an absolute local folder.");
        var generation = await ReadUiAsync(() =>
        {
            _opening = true;
            OpenCandidates.Clear();
            _locating = locating;
            StatusText = string.Empty;
            return ++_openGeneration;
        }).ConfigureAwait(false);
        try
        {
            var candidates = await _application.DiscoverAsync(folder, token).ConfigureAwait(false);
            await UpdateUiAsync(() =>
            {
                if (generation != _openGeneration) return;
                if (candidates.Count == 0) StatusText = "No supported Sunder .NET or Node package project was found in this folder.";
                else if (candidates.Count == 1) RememberProject(candidates[0], locating, select: true);
                else
                {
                    foreach (var candidate in candidates) OpenCandidates.Add(candidate);
                    SelectedCandidate = candidates[0];
                    _showCreation = false;
                    StatusText = "Choose the package project to open.";
                    ChangedAll();
                }
            }).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException && BuilderProjectApplicationService.IsRecoverable(error))
        {
            await UpdateUiAsync(() => { if (generation == _openGeneration) StatusText = error.Message; }).ConfigureAwait(false);
        }
        finally { await UpdateUiAsync(() => { _opening = false; ChangedAll(); }).ConfigureAwait(false); }
    }

    public Task OpenSelectedCandidateAsync() => RunOwnedAsync(_ => UpdateUiAsync(() =>
    {
        if (SelectedCandidate is { } candidate && OpenCandidates.Contains(candidate))
            RememberProject(candidate, _locating, select: true);
    }));

    private BuilderProjectViewModel RememberProject(BuilderProjectInspection inspection, BuilderProjectViewModel? locating, bool select)
    {
        var existing = Projects.FirstOrDefault(row => row.EntryPoint is { } entry && _paths.SameEntryPoint(entry, inspection.EntryPoint));
        if (existing is not null && locating is not null && locating != existing) Projects.Remove(locating);
        var project = existing ?? (locating is not null && Projects.Contains(locating) ? locating : null);
        if (project is null)
        {
            var now = DateTimeOffset.UtcNow;
            project = new(new(Guid.NewGuid().ToString("N"), inspection.DisplayName, inspection.PackageId,
                inspection.ProjectFolder, inspection.EntryPoint, inspection.Kind, now, now));
            Projects.Add(project);
        }
        project.ApplyInspection(inspection);
        project.SetError(string.Empty);
        OpenCandidates.Clear();
        _locating = null;
        StatusText = string.Empty;
        SaveBookmarks();
        if (select) ActivateProject(project);
        ChangedAll();
        return project;
    }

    public Task SelectTechnologyAsync(BuilderProjectKind kind) => EnsureCreationSdkAsync(kind);
    public Task RetryCreationSdkAsync() => Creation.Kind is { } kind ? EnsureCreationSdkAsync(kind) : Task.CompletedTask;

    private Task EnsureCreationSdkAsync(BuilderProjectKind kind) => RunOwnedAsync(async lifetime =>
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var generation = await ReadUiAsync(() =>
        {
            if (Creation.IsBusy) return -1L;
            Creation.Kind = kind;
            Creation.Installation = null;
            Creation.Step = 1;
            Creation.IsBusy = true;
            _wizardCancellation = cancellation;
            return _wizardGeneration;
        }).ConfigureAwait(false);
        if (generation < 0) return;
        try
        {
            var result = await _application.RunJobAsync(new() { Operation = "ensure-sdk", Kind = kind },
                snapshot => UpdateUiAsync(() => { if (generation == _wizardGeneration) Creation.Apply(snapshot); }), cancellation.Token).ConfigureAwait(false);
            await UpdateUiAsync(() =>
            {
                if (generation == _wizardGeneration && result.State == "succeeded" && result.Installation is not null) Creation.Step = 2;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (PresentationCancellation.IsExpected(error, cancellation.Token))
        {
            await UpdateUiAsync(() => Creation.SetError("SDK setup cancelled. Retry when ready.")).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException && BuilderProjectApplicationService.IsRecoverable(error))
        {
            await UpdateUiAsync(() => Creation.SetError(error.Message)).ConfigureAwait(false);
        }
        finally
        {
            _wizardCancellation = null;
            await UpdateUiAsync(() => Creation.IsBusy = false).ConfigureAwait(false);
        }
    });

    public Task CompleteCreationAsync() => RunOwnedAsync(async lifetime =>
    {
        await InitializeAsync(lifetime).ConfigureAwait(false);
        if (!_projectsLoaded) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var request = await ReadUiAsync(() =>
        {
            if (!Creation.CanCreate) return null;
            Creation.IsBusy = true;
            _wizardCancellation = cancellation;
            return new BuilderJobRequest
            {
                Operation = "create", Kind = Creation.Kind!.Value, DisplayName = Creation.DisplayName.Trim(),
                PackageId = Creation.PackageId.Trim(), ProjectFolder = Creation.FullPath, Installation = Creation.Installation,
            };
        }).ConfigureAwait(false);
        if (request is null) return;
        var navigation = await ReadUiAsync(() => _navigationRevision).ConfigureAwait(false);
        BuilderJobSnapshot? last = null;
        try
        {
            last = await _application.RunJobAsync(request, snapshot => UpdateUiAsync(() =>
            {
                last = snapshot;
                Creation.Apply(snapshot);
            }), cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (PresentationCancellation.IsExpected(error, cancellation.Token))
        {
            if (last is not null) last = last with { State = "cancelled", Error = error.Message };
            await UpdateUiAsync(() => Creation.SetError("Creation cancelled. Your inputs are preserved.")).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException && BuilderProjectApplicationService.IsRecoverable(error))
        {
            if (last is not null) last = last with { State = "failed", Error = error.Message };
            await UpdateUiAsync(() => Creation.SetError(error.Message)).ConfigureAwait(false);
        }
        finally
        {
            _wizardCancellation = null;
            await UpdateUiAsync(() =>
            {
                Creation.IsBusy = false;
                if (last?.Project is { } source)
                {
                    var row = RememberProject(source, null,
                        select: !_stopping && (navigation == _navigationRevision || _showCreation));
                    row.ApplySnapshot(last);
                    if (last.State != "succeeded") row.SetStatus("Setup incomplete. Retry setup to restore and build the existing source.");
                    SaveBookmarks();
                }
            }).ConfigureAwait(false);
        }
    });

    public Task DeleteSelectedProjectAsync()
    {
        var row = SelectedProject;
        return RunOwnedAsync(async token =>
        {
            if (row is null) return;
            await RunProjectActionAsync(row, async (generation, operationToken) =>
            {
                if (_packageOperations.GetSession(row.EntryPoint) is { } owner)
                {
                    await ReleaseSessionAsync(owner).ConfigureAwait(false);
                }
                var records = await ReadUiAsync(() =>
                {
                    row.SetSession(false);
                    return Projects.Where(item => item != row).Select(item => item.ToRecord()).ToArray();
                }).ConfigureAwait(false);
                // Save first: a failed write retains the bookmark and the current route. Replace the
                // persistence worker's pending generation too, so a later flush cannot remove it.
                try { await _persistence.SaveNowAsync(records, operationToken).ConfigureAwait(false); }
                catch { await UpdateUiAsync(SaveBookmarks).ConfigureAwait(false); throw; }
                await UpdateUiAsync(() =>
                {
                    var wasSelected = ReferenceEquals(SelectedProject, row);
                    if (wasSelected) _listDetail.ShowList();
                    Projects.Remove(row);
                    SaveBookmarks();
                    ChangedAll();
                }).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        });
    }
}
