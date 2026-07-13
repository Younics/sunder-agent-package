using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderViewModel
{
    public Task InitializeAsync()
    {
        lock (_initializationSync)
        {
            if (_initialized || _disposed)
            {
                return Task.CompletedTask;
            }

            return _initializationTask ??= InitializeCoreAsync();
        }
    }

    private async Task InitializeCoreAsync()
    {
        try
        {
            await _uiDispatcher.InvokeAsync(ReloadWorkspaces);
            var projects = await _applicationService.LoadProjectsAsync(_lifetime.Token);
            await _uiDispatcher.InvokeAsync(() =>
            {
                ApplyLoadedProjects(projects);
                _projectsLoaded = true;
                _initialized = true;
            });
            if (!_processedStartupAutoLoad)
            {
                _processedStartupAutoLoad = true;
                await LoadStartupAutoLoadProjectsAsync(_lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                RuntimeLogText = ex.ToString();
                StatusText = $"Package builder initialization failed: {ex.Message}";
            });
        }
    }

    public Task RefreshSetupAsync()
        => RunBusyAsync(async cancellationToken =>
        {
            var workspaceId = await _uiDispatcher.InvokeAsync(() => SelectedProject?.WorkspaceId ?? string.Empty);
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                return;
            }

            var statuses = await _operationQueue.RunAsync(
                () => _applicationService.CheckSetupAsync(workspaceId, cancellationToken),
                cancellationToken);
            await _uiDispatcher.InvokeAsync(() => ApplySetupStatuses(statuses));
        });

    public Task InstallDotnetSdkAsync()
        => InstallPrerequisiteAsync(
            "Downloading .NET SDK installer...",
            (workspaceId, cancellationToken) => _applicationService.InstallDotnetSdkAsync(workspaceId, cancellationToken));

    public Task InstallTemplateAsync()
        => InstallPrerequisiteAsync(
            "Installing Sunder package template...",
            (workspaceId, cancellationToken) => _applicationService.InstallTemplateAsync(workspaceId, cancellationToken));

    public Task CreateProjectAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var project = new BuilderProjectViewModel(new BuilderProjectRecord(
            Guid.NewGuid().ToString("N"),
            "New Sunder Package",
            string.Empty,
            Workspaces.FirstOrDefault()?.WorkspaceId ?? string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            Watch: true,
            now,
            now)
        {
            DevPackageRelativePath = BuilderPathService.DefaultDevPackageRelativePath,
        });
        Projects.Add(project);
        SelectedProject = project;
        IsEditorActive = true;
        return Task.CompletedTask;
    }

    public Task DeleteSelectedProjectAsync()
        => RunBusyAsync(async cancellationToken =>
        {
            var deletion = await _uiDispatcher.InvokeAsync(RemoveSelectedProject);
            if (deletion is null)
            {
                return;
            }

            await _persistence.SaveNowAsync(deletion.Value.Projects, cancellationToken);
            await _uiDispatcher.InvokeAsync(() => CompleteProjectDeletion(deletion.Value));
        });

    public async Task InitializeSelectedProjectAsync()
    {
        if (!TryValidateSelectedProject(requireExistingFolder: false, requireInitializedPaths: false, out var project, out var record))
        {
            return;
        }

        var workspacePath = FindWorkspacePath(record.WorkspaceId, record.WorkspacePathId!);
        if (workspacePath is null)
        {
            StatusText = "Selected workspace path was not found.";
            return;
        }

        record = record with { UpdatedAtUtc = DateTimeOffset.UtcNow };
        ApplyProjectRecord(project, record);
        await _persistence.SaveNowAsync(CaptureProjects(), _lifetime.Token);
        StatusText = "Package initialization queued.";
        RuntimeLogText = string.Empty;
        IsSelectedProjectInitializing = true;
        try
        {
            _operationQueue.Enqueue(
                $"Initialize {record.DisplayName}",
                context => RunInitializeOperationAsync(project, record, workspacePath, context));
        }
        catch (Exception ex)
        {
            IsSelectedProjectInitializing = false;
            RuntimeLogText = ex.Message;
            StatusText = "Initialization failed. See runtime log.";
        }
    }

    public Task BuildSelectedProjectAsync()
        => QueueBuildOrPublishAsync(isPublish: false);

    public Task PublishSelectedProjectAsync()
        => QueueBuildOrPublishAsync(isPublish: true);

    private async Task InstallPrerequisiteAsync(
        string initialStatus,
        Func<string, CancellationToken, Task<string>> install)
    {
        await RunBusyAsync(async cancellationToken =>
        {
            var workspaceId = await _uiDispatcher.InvokeAsync(() => SelectedProject?.WorkspaceId ?? string.Empty);
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                throw new InvalidOperationException("Select a workspace before continuing.");
            }

            await _uiDispatcher.InvokeAsync(() => StatusText = initialStatus);
            var message = await _operationQueue.RunAsync(() => install(workspaceId, cancellationToken), cancellationToken);
            var statuses = await _applicationService.CheckSetupAsync(workspaceId, cancellationToken);
            await _uiDispatcher.InvokeAsync(() =>
            {
                ApplySetupStatuses(statuses);
                StatusText = message;
            });
        });
    }

    private async Task RunInitializeOperationAsync(
        BuilderProjectViewModel project,
        BuilderProjectRecord record,
        AgentWorkspacePathRecord workspacePath,
        BackgroundProcessContext context)
    {
        try
        {
            var result = await _applicationService.InitializeProjectAsync(
                record,
                workspacePath,
                context,
                statuses => _uiDispatcher.InvokeAsync(() => ApplySetupStatuses(statuses)));
            var projects = await _uiDispatcher.InvokeAsync(() =>
            {
                ApplyProjectRecord(project, result.Project);
                UpdateSelectedProjectInitialized();
                StatusText = $"Initialized {record.DisplayName}.";
                return CaptureProjects();
            });
            await _persistence.SaveNowAsync(projects, CancellationToken.None);
            await RefreshSelectedStatusCoreAsync(Interlocked.Increment(ref _selectedProjectStatusVersion), false, context.CancellationToken);
            context.ReportProgress(100, "Sunder package project initialized.");
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            await _uiDispatcher.InvokeAsync(() => StatusText = "Package initialization cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            context.ReportProgress(100, "Package initialization failed.");
            await _uiDispatcher.InvokeAsync(() =>
            {
                RuntimeLogText = ex.Message;
                StatusText = "Initialization failed. See runtime log.";
            });
            throw;
        }
        finally
        {
            await _uiDispatcher.InvokeAsync(() => IsSelectedProjectInitializing = false);
        }
    }

    private Task QueueBuildOrPublishAsync(bool isPublish)
    {
        if (!TryValidateSelectedProject(true, true, out var project, out var record))
        {
            return Task.CompletedTask;
        }

        var operationName = isPublish ? "Publish" : "Build";
        _operationQueue.Enqueue(
            $"{operationName} {record.DisplayName}",
            context => RunBuildOrPublishOperationAsync(project, record, isPublish, context));
        StatusText = $"{operationName} queued.";
        return Task.CompletedTask;
    }

    private async Task RunBuildOrPublishOperationAsync(
        BuilderProjectViewModel project,
        BuilderProjectRecord record,
        bool isPublish,
        BackgroundProcessContext context)
    {
        var operationName = isPublish ? "Publish" : "Build";
        try
        {
            var result = isPublish
                ? await _applicationService.PublishProjectAsync(record, context)
                : await _applicationService.BuildProjectAsync(record, context);
            var projects = await _uiDispatcher.InvokeAsync(() =>
            {
                ApplyProjectRecord(project, result.Project);
                RuntimeLogText = result.RuntimeLog;
                UpdateSelectedProjectInitialized();
                StatusText = isPublish
                    ? "Publish completed."
                    : $"Build completed. Dev output: {result.Project.DevPackageFolder}";
                return CaptureProjects();
            });
            await _persistence.SaveNowAsync(projects, CancellationToken.None);
            if (!isPublish)
            {
                await RefreshSelectedStatusCoreAsync(Interlocked.Increment(ref _selectedProjectStatusVersion), false, context.CancellationToken);
            }

            context.ReportProgress(100, $"Sunder package {operationName.ToLowerInvariant()} completed.");
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            await _uiDispatcher.InvokeAsync(() => StatusText = $"{operationName} cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            context.ReportProgress(100, $"{operationName} failed. See runtime log.");
            await _uiDispatcher.InvokeAsync(() =>
            {
                RuntimeLogText = ex is BuilderProjectOperationException operationException
                    ? operationException.RuntimeLog
                    : ex.Message;
                StatusText = $"{operationName} failed. See runtime log.";
            });
            throw;
        }
    }

    private async Task RunEnsureSetupOperationAsync(BuilderProjectRecord record, BackgroundProcessContext context)
    {
        try
        {
            var statuses = await _applicationService.EnsurePrerequisitesInstalledAsync(
                record.WorkspaceId,
                context,
                items => _uiDispatcher.InvokeAsync(() => ApplySetupStatuses(items)));
            if (!statuses.All(status => status.IsInstalled))
            {
                var message = BuildMissingPrerequisitesMessage(statuses);
                await _uiDispatcher.InvokeAsync(() =>
                {
                    RuntimeLogText = message;
                    StatusText = "Setup incomplete. See runtime log.";
                });
                throw new InvalidOperationException(message);
            }

            await _uiDispatcher.InvokeAsync(() =>
            {
                RuntimeLogText = string.Empty;
                StatusText = "Package builder setup is ready.";
            });
            context.ReportProgress(100, "Package builder setup is ready.");
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            await _uiDispatcher.InvokeAsync(() => StatusText = "Setup check cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                RuntimeLogText = ex.Message;
                StatusText = ex.Message.StartsWith("Package builder setup is incomplete.", StringComparison.Ordinal)
                    ? "Setup incomplete. See runtime log."
                    : "Setup check failed. See runtime log.";
            });
            throw;
        }
    }

    private async Task LoadStartupAutoLoadProjectsAsync(CancellationToken cancellationToken)
    {
        var projects = await _uiDispatcher.InvokeAsync(() => Projects.Where(project => project.AutoLoadOnStartup).ToArray());
        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await _applicationService.LoadProjectAsync(project.ToRecord(), cancellationToken);
                if (result.Status is null)
                {
                    continue;
                }

                var records = await _uiDispatcher.InvokeAsync(() =>
                {
                    ApplyProjectRecord(project, result.Project);
                    if (ReferenceEquals(project, SelectedProject))
                    {
                        IsSelectedProjectLoaded = result.Status.IsLoaded;
                        StatusText = FormatStatus(result.Status);
                    }

                    return CaptureProjects();
                });
                await _persistence.SaveNowAsync(records, CancellationToken.None);
            }
            catch (Exception ex)
            {
                await _uiDispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(project, SelectedProject))
                    {
                        StatusText = $"Auto load failed: {ex.Message}";
                    }
                });
            }
        }
    }

    private async Task RefreshSelectedStatusCoreAsync(int version, bool updateStatusText, CancellationToken cancellationToken)
    {
        var selection = await _uiDispatcher.InvokeAsync(() =>
            SelectedProject is null || string.IsNullOrWhiteSpace(SelectedProject.PackageId)
                ? default((BuilderProjectViewModel Project, string PackageId)?)
                : (SelectedProject, SelectedProject.PackageId));
        if (selection is null)
        {
            await _uiDispatcher.InvokeAsync(() => IsSelectedProjectLoaded = false);
            return;
        }

        try
        {
            var status = await _applicationService.GetProjectStatusAsync(selection.Value.PackageId, cancellationToken);
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (version != Volatile.Read(ref _selectedProjectStatusVersion)
                    || !ReferenceEquals(selection.Value.Project, SelectedProject))
                {
                    return;
                }

                IsSelectedProjectLoaded = status?.IsLoaded == true;
                if (updateStatusText)
                {
                    StatusText = status is null
                        ? $"Package '{selection.Value.PackageId}' is not active."
                        : FormatStatus(status);
                }
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (version == Volatile.Read(ref _selectedProjectStatusVersion)
                    && ReferenceEquals(selection.Value.Project, SelectedProject))
                {
                    IsSelectedProjectLoaded = false;
                    if (updateStatusText)
                    {
                        StatusText = $"Failed to read package status: {ex.Message}";
                    }
                }
            });
        }
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> action)
    {
        var started = await _uiDispatcher.InvokeAsync(() =>
        {
            if (IsBusy || _disposed)
            {
                return false;
            }

            IsBusy = true;
            return true;
        });
        if (!started)
        {
            return;
        }

        try
        {
            await action(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await _uiDispatcher.InvokeAsync(() => StatusText = ex.Message);
        }
        finally
        {
            await _uiDispatcher.InvokeAsync(() => IsBusy = false);
        }
    }
}
