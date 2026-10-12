using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Sunder.Generated.Packages.Sunder_Package_Agent_Models;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderViewModel
{
    private static readonly TimeSpan InitializationCancellationDrainTimeout = TimeSpan.FromSeconds(2);

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Task initialization;
        lock (_initializationSync)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            initialization = _initializationTask ??= _tasks.Track(_ => InitializeAndReportAsync());
        }

        return cancellationToken.CanBeCanceled
            ? initialization.WaitAsync(cancellationToken)
            : initialization;
    }

    private async Task InitializeAndReportAsync()
    {
        await Task.Yield();
        var succeeded = false;
        try
        {
            await RunPresentationOperationAsync(
                BuilderInitializationOperation,
                async cancellationToken =>
                {
                    try
                    {
                        await InitializeCoreAsync(cancellationToken);
                        await _uiDispatcher.InvokeAsync(() =>
                        {
                            if (StatusText.StartsWith(
                                    "Package builder initialization failed:",
                                    StringComparison.Ordinal))
                            {
                                RuntimeLogText = string.Empty;
                                StatusText = string.Empty;
                            }
                        });
                        succeeded = true;
                    }
                    catch (OperationCanceledException exception) when (
                        _lifetime.IsCancellationRequested
                        && !PresentationExceptionPolicy.IsFatal(exception))
                    {
                        succeeded = true;
                    }
                },
                ReconcileBuilderInitializationAsync,
                _lifetime.Token);
        }
        finally
        {
            if (!succeeded)
            {
                lock (_initializationSync)
                {
                    _initializationTask = null;
                }
            }
        }
    }

    private Task ReconcileBuilderInitializationAsync(
        Exception exception,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var failure = exception.InnerException ?? exception;
        return _uiDispatcher.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                RuntimeLogText = failure.ToString();
                StatusText = $"Package builder initialization failed: {failure.Message}";
            }
        });
    }

    private static bool IsExpectedBuilderInitializationFailure(Exception exception)
    {
        if (PresentationExceptionPolicy.IsFatal(exception))
        {
            return false;
        }
        if (exception is AggregateException aggregate)
        {
            return aggregate.InnerExceptions.Count > 0
                && aggregate.InnerExceptions.All(IsExpectedBuilderInitializationFailure);
        }
        return PresentationExceptionPolicy.IsExpectedOperationFailure(exception)
            || exception is InvalidOperationException or InvalidDataException;
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var revision = _snapshotRevision;
            var snapshot = _requests.Begin(SnapshotChannel, cancellationToken);
            var applied = false;
            try
            {
                Task<IReadOnlyList<AgentWorkspaceRecord>> workspacesTask;
                Task<IReadOnlyList<BuilderProjectRecord>> projectsTask;
                try
                {
                    workspacesTask = StartInitializationLoad(
                        () => _applicationService.ListWorkspacesAsync(snapshot.CancellationToken));
                    projectsTask = StartInitializationLoad(
                        () => _applicationService.LoadProjectsAsync(snapshot.CancellationToken));
                    var loading = Task.WhenAll(workspacesTask, projectsTask);
                    try
                    {
                        await loading.WaitAsync(snapshot.CancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException exception) when (
                        snapshot.CancellationToken.IsCancellationRequested
                        && !PresentationExceptionPolicy.IsFatal(exception))
                    {
                        await DrainCanceledInitializationAsync(loading).ConfigureAwait(false);
                        throw;
                    }
                    catch (Exception exception)
                    {
                        var failure = GetInitializationFailure(loading, exception);
                        ExceptionDispatchInfo.Capture(failure).Throw();
                        throw;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (IsExpectedBuilderInitializationFailure(exception))
                {
                    throw new PresentationOperationException(
                        "Package Builder initialization failed.",
                        exception);
                }
                var workspaces = await workspacesTask.ConfigureAwait(false);
                var projects = await projectsTask.ConfigureAwait(false);
                snapshot.CancellationToken.ThrowIfCancellationRequested();
                await _uiDispatcher.InvokeAsync(() =>
                {
                    if (_disposed || !_requests.IsCurrent(snapshot))
                    {
                        return;
                    }
                    ApplyLoadedWorkspaces(workspaces);
                    ApplyLoadedProjects(projects);
                    if (revision == _snapshotRevision)
                    {
                        _snapshotRefreshPending = false;
                    }
                    applied = true;
                });
            }
            catch (OperationCanceledException exception) when (
                snapshot.CancellationToken.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested
                && !PresentationExceptionPolicy.IsFatal(exception))
            {
            }
            finally
            {
                _requests.Complete(snapshot);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (applied && !_snapshotRefreshPending)
            {
                return;
            }
        }
    }

    private static async Task DrainCanceledInitializationAsync(Task loading)
    {
        try
        {
            await loading.WaitAsync(InitializationCancellationDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!loading.IsCompleted)
        {
            _ = ObserveDetachedInitializationAsync(loading);
        }
        catch (Exception exception)
        {
            var failure = GetInitializationFailure(loading, exception);
            if (!IsExpectedBuilderInitializationFailure(failure))
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
    }

    private static async Task ObserveDetachedInitializationAsync(Task loading)
    {
        try
        {
            await loading.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var failure = GetInitializationFailure(loading, exception);
            if (PresentationExceptionPolicy.IsExpectedOperationFailure(failure))
            {
                return;
            }
            if (!PresentationExceptionPolicy.IsFatal(failure))
            {
                Trace.TraceError(
                    $"A detached Package Builder initialization load failed: {failure}");
                return;
            }

            ThreadPool.QueueUserWorkItem(
                static state => ExceptionDispatchInfo.Capture((Exception)state!).Throw(),
                failure);
        }
    }

    private static Exception GetInitializationFailure(Task loading, Exception fallback)
    {
        var aggregate = loading.Exception?.Flatten();
        return aggregate?.InnerExceptions.Count switch
        {
            1 => aggregate.InnerExceptions[0],
            > 1 => aggregate,
            _ => fallback,
        };
    }

    private static Task<T> StartInitializationLoad<T>(Func<Task<T>> load)
    {
        try
        {
            return load()
                ?? Task.FromException<T>(new InvalidOperationException(
                    "A Package Builder initialization load returned a null task."));
        }
        catch (Exception exception)
        {
            return Task.FromException<T>(exception);
        }
    }

    public Task RefreshSetupAsync()
        => RunPresentationOperationAsync(
            BuilderSetupOperation,
            RefreshSetupCoreAsync,
            ReconcileBuilderSnapshotAsync);

    private Task RefreshSetupCoreAsync(CancellationToken operationCancellation)
        => RunBusyAsync(async cancellationToken =>
        {
            operationCancellation.ThrowIfCancellationRequested();
            var workspaceId = await _uiDispatcher.InvokeAsync(() => SelectedProject?.WorkspaceId ?? string.Empty);
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                return;
            }

            var statuses = await _operationQueue.RunAsync(
                () => _applicationService.CheckSetupAsync(workspaceId, cancellationToken),
                cancellationToken);
            await _uiDispatcher.InvokeAsync(() => ApplySetupStatuses(statuses));
        }, operationCancellation);

    public Task InstallDotnetSdkAsync()
        => RunPresentationOperationAsync(
            BuilderSetupOperation,
            cancellationToken => InstallPrerequisiteAsync(
                "Downloading .NET SDK installer...",
                (workspaceId, token) => _applicationService.InstallDotnetSdkAsync(workspaceId, token),
                cancellationToken),
            ReconcileBuilderSnapshotAsync);

    public Task InstallTemplateAsync()
        => RunPresentationOperationAsync(
            BuilderSetupOperation,
            cancellationToken => InstallPrerequisiteAsync(
                "Installing Sunder package template...",
                (workspaceId, token) => _applicationService.InstallTemplateAsync(workspaceId, token),
                cancellationToken),
            ReconcileBuilderSnapshotAsync);

    public Task CreateProjectAsync()
        => RunPresentationOperationAsync(
            BuilderCreateOperation,
            CreateProjectCoreAsync);

    private Task CreateProjectCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var project = new BuilderProjectViewModel(new BuilderProjectRecord(
            Guid.NewGuid().ToString("N"),
            "New Sunder Package",
            string.Empty,
            Workspaces.FirstOrDefault()?.WorkspaceId ?? string.Empty,
            string.Empty,
            string.Empty,
            now,
            now));
        _snapshotRefreshPending = true;
        _snapshotRevision++;
        _requests.Invalidate(SnapshotChannel);
        _listDetail.Reconcile([.. Projects, project], selectFirstWhenUnrouted: false);
        _listDetail.ShowNewDetail(project);
        _persistence.RequestSave(CaptureProjects());
        return Task.CompletedTask;
    }

    public Task DeleteSelectedProjectAsync()
    {
        BuilderProjectDeletion? deletion = null;
        return RunPresentationOperationAsync(
            BuilderDeleteOperation,
            cancellationToken => DeleteSelectedProjectCoreAsync(
                cancellationToken,
                value => deletion = value),
            cancellationToken => ReconcileProjectDeletionAsync(deletion, cancellationToken));
    }

    private Task DeleteSelectedProjectCoreAsync(
        CancellationToken operationCancellation,
        Action<BuilderProjectDeletion> captureDeletion)
        => RunBusyAsync(async cancellationToken =>
        {
            var deletion = await _uiDispatcher.InvokeAsync(RemoveSelectedProject);
            if (deletion is null)
            {
                return;
            }

            captureDeletion(deletion);
            await _persistence.SaveNowAsync(deletion.Projects, cancellationToken);
            await _uiDispatcher.InvokeAsync(() => CompleteProjectDeletion(deletion));
        }, operationCancellation);

    private async Task ReconcileProjectDeletionAsync(
        BuilderProjectDeletion? deletion,
        CancellationToken cancellationToken)
    {
        if (deletion is null)
        {
            await ReconcileBuilderSnapshotAsync(cancellationToken);
            return;
        }

        var workspacesTask = _applicationService.ListWorkspacesAsync(cancellationToken);
        var projectsTask = _applicationService.LoadProjectsAsync(cancellationToken);
        await Task.WhenAll(workspacesTask, projectsTask).ConfigureAwait(false);
        var workspaces = await workspacesTask.ConfigureAwait(false);
        var projects = await projectsTask.ConfigureAwait(false);
        await _uiDispatcher.InvokeAsync(() =>
        {
            _locallyDeletedProjectIds.Remove(deletion.ProjectId);
            ApplyLoadedWorkspaces(workspaces);
            ApplyLoadedProjects(projects);
            StatusText = projects.Any(project => string.Equals(
                project.Id,
                deletion.ProjectId,
                StringComparison.OrdinalIgnoreCase))
                ? "Project deletion was not saved. The project was restored."
                : $"Deleted package project '{deletion.Name}'.";
            _persistence.RequestSave(CaptureProjects());
        });
    }

    public Task InitializeSelectedProjectAsync()
        => RunPresentationOperationAsync(
            BuilderInitializeProjectOperation,
            InitializeSelectedProjectCoreAsync,
            ReconcileBuilderSnapshotAsync);

    private async Task InitializeSelectedProjectCoreAsync(CancellationToken cancellationToken)
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
        await _persistence.SaveNowAsync(CaptureProjects(), cancellationToken);
        StatusText = "Package initialization queued.";
        RuntimeLogText = string.Empty;
        IsSelectedProjectInitializing = true;
        try
        {
            _operationQueue.Enqueue(
                $"Initialize {record.DisplayName}",
                context => RunInitializeOperationAsync(project, record, workspacePath, context));
        }
        catch
        {
            IsSelectedProjectInitializing = false;
            throw;
        }
    }

    public Task BuildSelectedProjectAsync()
        => RunPresentationOperationAsync(
            BuilderBuildOperation,
            cancellationToken => QueueBuildOrPublishAsync(isPublish: false, cancellationToken),
            ReconcileBuilderSnapshotAsync);

    public Task PublishSelectedProjectAsync()
        => RunPresentationOperationAsync(
            BuilderPublishOperation,
            cancellationToken => QueueBuildOrPublishAsync(isPublish: true, cancellationToken),
            ReconcileBuilderSnapshotAsync);

    private async Task InstallPrerequisiteAsync(
        string initialStatus,
        Func<string, CancellationToken, Task<string>> install,
        CancellationToken operationCancellation)
    {
        await RunBusyAsync(async cancellationToken =>
        {
            operationCancellation.ThrowIfCancellationRequested();
            var workspaceId = await _uiDispatcher.InvokeAsync(() => SelectedProject?.WorkspaceId ?? string.Empty);
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                await _uiDispatcher.InvokeAsync(() => StatusText = "Select a workspace before continuing.");
                return;
            }

            await _uiDispatcher.InvokeAsync(() => StatusText = initialStatus);
            var message = await _operationQueue.RunAsync(() => install(workspaceId, cancellationToken), cancellationToken);
            var statuses = await _applicationService.CheckSetupAsync(workspaceId, cancellationToken);
            await _uiDispatcher.InvokeAsync(() =>
            {
                ApplySetupStatuses(statuses);
                StatusText = message;
            });
        }, operationCancellation);
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

    private Task QueueBuildOrPublishAsync(
        bool isPublish,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
                    : "Build completed.";
                return CaptureProjects();
            });
            await _persistence.SaveNowAsync(projects, CancellationToken.None);

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

    private async Task RunBusyAsync(
        Func<CancellationToken, Task> action,
        CancellationToken operationCancellation = default)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            operationCancellation,
            _lifetime.Token);
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
            await action(linkedCancellation.Token);
        }
        finally
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    IsBusy = false;
                }
            });
        }
    }
}
