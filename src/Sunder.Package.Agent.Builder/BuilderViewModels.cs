using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Builder;

public sealed class BuilderViewModel(
    BuilderSetupService setupService,
    BuilderWorkspaceExecutionService executionService,
    BuilderProjectStore projectStore,
    IPackageSessionService packageSessionService,
    IBackgroundProcessQueue backgroundProcesses) : INotifyPropertyChanged
{
    private static readonly TimeSpan StatusMessageVisibleDuration = TimeSpan.FromSeconds(3);
    private const string DefaultDevPackageRelativePath = "/bin/Debug/net10.0/sunder-dev";

    private BuilderProjectViewModel? _selectedProject;
    private string _statusText = string.Empty;
    private string _runtimeLogText = string.Empty;
    private bool _isBusy;
    private bool _isSetupComplete;
    private bool _initialized;
    private bool _processedStartupAutoLoad;
    private bool _isCompactLayout;
    private bool _isEditorActive;
    private bool _isSelectedProjectInitialized;
    private bool _isSelectedProjectLoaded;
    private bool _isSelectedProjectInitializing;
    private bool _showStatusMessage;
    private int _selectedProjectStatusVersion;
    private long _statusMessageVersion;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<BuilderPrerequisiteViewModel> SetupItems { get; } = [];

    public ObservableCollection<BuilderProjectViewModel> Projects { get; } = [];

    public ObservableCollection<AgentWorkspaceRecord> Workspaces { get; } = [];

    public ObservableCollection<BuilderWorkspacePathOptionViewModel> WorkspacePathOptions { get; } = [];

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                NotifySetupStatePropertiesChanged();
                NotifyProjectStatePropertiesChanged();
            }
        }
    }

    public bool IsSetupComplete
    {
        get => _isSetupComplete;
        private set
        {
            if (SetField(ref _isSetupComplete, value))
            {
                OnPropertyChanged(nameof(ShowSetup));
                OnPropertyChanged(nameof(ShowProjects));
                NotifySetupStatePropertiesChanged();
            }
        }
    }

    public bool ShowSetup => false;

    public bool ShowProjects => true;

    public bool HasSelectedProject => SelectedProject is not null;

    public bool CanEditSelectedProject => HasSelectedProject && !IsBusy && !IsSelectedProjectInitializing;

    public bool CanEditProjectIdentity => CanEditSelectedProject && !IsSelectedProjectInitialized;

    public bool IsProjectIdentityReadOnly => !CanEditProjectIdentity;

    public bool CanUseSelectedProjectRuntimeActions => CanEditSelectedProject;

    public bool ShowRuntimeSection => HasSelectedProject && IsSelectedProjectInitialized;

    public bool IsSelectedProjectInitialized
    {
        get => _isSelectedProjectInitialized;
        private set
        {
            if (SetField(ref _isSelectedProjectInitialized, value))
            {
                NotifyProjectStatePropertiesChanged();
            }
        }
    }

    public bool IsSelectedProjectLoaded
    {
        get => _isSelectedProjectLoaded;
        private set
        {
            if (SetField(ref _isSelectedProjectLoaded, value))
            {
                NotifyProjectStatePropertiesChanged();
            }
        }
    }

    public bool IsSelectedProjectInitializing
    {
        get => _isSelectedProjectInitializing;
        private set
        {
            if (SetField(ref _isSelectedProjectInitializing, value))
            {
                NotifyProjectStatePropertiesChanged();
                NotifySetupStatePropertiesChanged();
            }
        }
    }

    public bool ShowInitializeSelectedProject => HasSelectedProject && !IsSelectedProjectInitialized;

    public bool ShowSelectedProjectSetup => HasSelectedProject && !IsSelectedProjectInitialized && SetupItems.Count > 0;

    public bool CanInitializeSelectedProject => HasSelectedProject
                                                && !IsBusy
                                                && !IsSelectedProjectInitializing
                                                && !IsSelectedProjectInitialized
                                               && !string.IsNullOrWhiteSpace(SelectedProject?.DisplayName)
                                               && !string.IsNullOrWhiteSpace(SelectedProject?.PackageId)
                                               && !string.IsNullOrWhiteSpace(SelectedProject?.WorkspaceId)
                                               && !string.IsNullOrWhiteSpace(SelectedProject?.WorkspacePathId);

    public bool ShowLoadSelectedProject => HasSelectedProject && IsSelectedProjectInitialized && !IsSelectedProjectLoaded;

    public bool ShowUnloadSelectedProject => HasSelectedProject && IsSelectedProjectLoaded;

    public bool IsCompactLayout
    {
        get => _isCompactLayout;
        set
        {
            if (SetField(ref _isCompactLayout, value))
            {
                if (value && SelectedProject is not null)
                {
                    IsEditorActive = true;
                }
                else if (!value && SelectedProject is null)
                {
                    SelectedProject = Projects.FirstOrDefault();
                }

                NotifyLayoutPropertiesChanged();
            }
        }
    }

    public bool IsEditorActive
    {
        get => _isEditorActive;
        private set
        {
            if (SetField(ref _isEditorActive, value))
            {
                NotifyLayoutPropertiesChanged();
            }
        }
    }

    public bool IsListActive => !IsEditorActive;

    public bool ShowWideLayout => !IsCompactLayout;

    public bool ShowCompactList => IsCompactLayout && IsListActive;

    public bool ShowCompactEditor => IsCompactLayout && IsEditorActive;

    public bool ShowListPane => ShowWideLayout || ShowCompactList;

    public bool ShowEditorPane => ShowWideLayout || ShowCompactEditor;

    public BuilderProjectViewModel? SelectedProject
    {
        get => _selectedProject;
        set
        {
            var oldProject = _selectedProject;
            if (SetField(ref _selectedProject, value))
            {
                if (oldProject is not null)
                {
                    oldProject.PropertyChanged -= OnSelectedProjectPropertyChanged;
                }

                if (value is not null)
                {
                    if (string.IsNullOrWhiteSpace(value.WorkspaceId))
                    {
                        value.WorkspaceId = Workspaces.FirstOrDefault()?.WorkspaceId ?? string.Empty;
                    }

                    NormalizeLoadedProject(value);
                    RefreshWorkspacePathOptions(preserveSelection: true);

                    value.PropertyChanged += OnSelectedProjectPropertyChanged;
                    if (IsCompactLayout)
                    {
                        IsEditorActive = true;
                    }
                }
                else
                {
                    WorkspacePathOptions.Clear();
                }

                UpdateSelectedProjectInitialized();
                IsSelectedProjectLoaded = false;
                RuntimeLogText = string.Empty;
                NotifyProjectStatePropertiesChanged();
                if (value is not null)
                {
                    _ = RefreshSelectedStatusAsync(++_selectedProjectStatusVersion, updateStatusText: false);
                }
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (!EqualityComparer<string>.Default.Equals(_statusText, value))
            {
                _statusText = value;
                OnPropertyChanged();
            }

            ShowStatusMessageForCurrentText();
        }
    }

    public bool ShowStatusMessage
    {
        get => _showStatusMessage;
        private set => SetField(ref _showStatusMessage, value);
    }

    public string RuntimeLogText
    {
        get => _runtimeLogText;
        private set
        {
            if (SetField(ref _runtimeLogText, value))
            {
                OnPropertyChanged(nameof(HasRuntimeLog));
            }
        }
    }

    public bool HasRuntimeLog => !string.IsNullOrWhiteSpace(RuntimeLogText);

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        ReloadWorkspaces();
        await LoadProjectsAsync();
    }

    public async Task RefreshSetupAsync()
    {
        await RunBusyAsync(async () =>
        {
            if (SelectedProject is not null && !string.IsNullOrWhiteSpace(SelectedProject.WorkspaceId))
            {
                var execution = await executionService.ResolveAsync(SelectedProject.WorkspaceId);
                await CheckAndApplySetupAsync(execution);
            }
        });
    }

    public async Task InstallDotnetSdkAsync()
    {
        await RunBusyAsync(async () =>
        {
            StatusText = "Downloading .NET SDK installer...";
            var execution = await ResolveSelectedExecutionAsync();
            StatusText = await setupService.InstallDotnetSdkAsync(execution);
            await CheckAndApplySetupAsync(execution);
        });
    }

    public async Task InstallTemplateAsync()
    {
        await RunBusyAsync(async () =>
        {
            StatusText = "Installing Sunder package template...";
            var execution = await ResolveSelectedExecutionAsync();
            StatusText = await setupService.InstallTemplateAsync(execution);
            await CheckAndApplySetupAsync(execution);
        });
    }

    public async Task CreateProjectAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var project = new BuilderProjectViewModel(new BuilderProjectRecord(
            Guid.NewGuid().ToString("N"),
            "New Sunder Package",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            Watch: true,
            now,
            now));
        project.WorkspaceId = Workspaces.FirstOrDefault()?.WorkspaceId ?? string.Empty;
        Projects.Add(project);
        SelectedProject = project;
        IsEditorActive = true;
        await Task.CompletedTask;
    }

    public async Task DeleteSelectedProjectAsync()
    {
        if (SelectedProject is null)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            var project = SelectedProject ?? throw new InvalidOperationException("No package project is selected.");
            var deletedName = project.DisplayName;
            var shouldClearSelection = IsCompactLayout;
            Projects.Remove(project);
            SelectedProject = shouldClearSelection ? null : Projects.FirstOrDefault();
            await SaveProjectsAsync();
            if (shouldClearSelection)
            {
                ClearStatus();
            }
            else
            {
                StatusText = $"Deleted package project '{deletedName}'.";
            }

            IsEditorActive = false;
        });
    }

    public async Task InitializeSelectedProjectAsync()
    {
        var project = SelectedProject;
        if (!ValidateSelectedProject(project, requireExistingFolder: false, requireInitializedPaths: false))
        {
            return;
        }

        var draft = BuilderProjectInitializationDraft.From(project!);
        project!.Touch();
        await SaveProjectsAsync();
        StatusText = "Package initialization queued.";
        RuntimeLogText = string.Empty;
        IsSelectedProjectInitializing = true;
        try
        {
            backgroundProcesses.Enqueue(new BackgroundProcessRequest(
                $"Initialize {draft.DisplayName}",
                "sunder-package-builder",
                BackgroundProcessIndicator.Main,
                BackgroundProcessConcurrencyMode.SequentialWithinGroup,
                CanCancel: true,
                async context =>
                {
                    try
                    {
                        context.ReportIndeterminate("Resolving workspace execution target...");
                        var execution = await executionService.ResolveAsync(draft.WorkspaceId, context.CancellationToken);
                        var statuses = await EnsurePrerequisitesInstalledAsync(execution, context);

                        if (!statuses.All(status => status.IsInstalled))
                        {
                            throw new InvalidOperationException(BuildMissingPrerequisitesMessage(statuses));
                        }

                        context.ReportIndeterminate("Creating Sunder package project...");
                        await RunOnUiThreadAsync(() => StatusText = "Creating Sunder package project...");
                        await InitializeProjectInFolderAsync(project, draft, execution, context);

                        project.Touch();
                        await SaveProjectsAsync(context.CancellationToken);
                        await RunOnUiThreadAsync(() =>
                        {
                            if (ReferenceEquals(SelectedProject, project))
                            {
                                UpdateSelectedProjectInitialized();
                            }
                        });
                        if (ReferenceEquals(SelectedProject, project))
                        {
                            await RefreshSelectedStatusAsync(updateStatusText: false);
                        }

                        context.ReportProgress(100, "Sunder package project initialized.");
                        await RunOnUiThreadAsync(() => StatusText = $"Initialized {draft.DisplayName}.");
                    }
                    catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                    {
                        await RunOnUiThreadAsync(() => StatusText = "Package initialization cancelled.");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        context.ReportProgress(100, "Package initialization failed.");
                        await RunOnUiThreadAsync(() =>
                        {
                            RuntimeLogText = ex.Message;
                            StatusText = "Initialization failed. See runtime log.";
                        });
                        throw;
                    }
                    finally
                    {
                        await RunOnUiThreadAsync(() => IsSelectedProjectInitializing = false);
                    }
                }));
        }
        catch (Exception ex)
        {
            IsSelectedProjectInitializing = false;
            RuntimeLogText = ex.Message;
            StatusText = "Initialization failed. See runtime log.";
        }
    }

    public async Task BuildSelectedProjectAsync()
    {
        var project = SelectedProject;
        if (!ValidateSelectedProject(project, requireExistingFolder: true, requireInitializedPaths: true))
        {
            return;
        }

        backgroundProcesses.Enqueue(new BackgroundProcessRequest(
            $"Build {project!.DisplayName}",
            "sunder-package-builder",
            BackgroundProcessIndicator.Main,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup,
            CanCancel: true,
            async context =>
            {
                context.ReportIndeterminate("Running dotnet build...");
                var execution = await executionService.ResolveAsync(project.WorkspaceId, context.CancellationToken);
                var projectFolder = ResolveExecutionProjectFolder(project);
                var result = await execution.RunProcessAsync("dotnet", ["build", projectFolder], projectFolder, cancellationToken: context.CancellationToken);
                if (result.ExitCode != 0)
                {
                    RuntimeLogText = string.IsNullOrWhiteSpace(result.CombinedOutput) ? "dotnet build failed." : result.CombinedOutput;
                    StatusText = "Build failed. See runtime log.";
                    context.ReportProgress(100, "Build failed. See runtime log.");
                    throw new InvalidOperationException("dotnet build failed. See Builder runtime log for details.");
                }

                RuntimeLogText = string.Empty;
                EnsureDevPackageRelativePath(project);
                project.DevPackageFolder = ResolveDevPackageFolder(project);
                project.Touch();
                await SaveProjectsAsync(context.CancellationToken);
                UpdateSelectedProjectInitialized();
                await RefreshSelectedStatusAsync(updateStatusText: false);
                context.ReportProgress(100, "Sunder package build completed.");
                StatusText = $"Build completed. Dev output: {project.DevPackageFolder}";
            }));
        StatusText = "Build queued.";
    }

    public async Task EnsureSelectedProjectSetupAsync()
    {
        var project = SelectedProject;
        if (!ValidateSelectedProject(project, requireExistingFolder: false, requireInitializedPaths: false))
        {
            return;
        }

        RuntimeLogText = string.Empty;
        backgroundProcesses.Enqueue(new BackgroundProcessRequest(
            $"Check setup for {project!.DisplayName}",
            "sunder-package-builder",
            BackgroundProcessIndicator.Main,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup,
            CanCancel: true,
            async context =>
            {
                try
                {
                    context.ReportIndeterminate("Resolving workspace execution target...");
                    var execution = await executionService.ResolveAsync(project.WorkspaceId, context.CancellationToken);
                    var statuses = await EnsurePrerequisitesInstalledAsync(execution, context);
                    if (!statuses.All(status => status.IsInstalled))
                    {
                        var message = BuildMissingPrerequisitesMessage(statuses);
                        await RunOnUiThreadAsync(() =>
                        {
                            RuntimeLogText = message;
                            StatusText = "Setup incomplete. See runtime log.";
                        });
                        context.ReportProgress(100, "Setup incomplete. See runtime log.");
                        throw new InvalidOperationException(message);
                    }

                    await RunOnUiThreadAsync(() =>
                    {
                        RuntimeLogText = string.Empty;
                        StatusText = "Package builder setup is ready.";
                    });
                    context.ReportProgress(100, "Package builder setup is ready.");
                }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                {
                    await RunOnUiThreadAsync(() => StatusText = "Setup check cancelled.");
                    throw;
                }
                catch (Exception ex)
                {
                    await RunOnUiThreadAsync(() =>
                    {
                        RuntimeLogText = ex.Message;
                        StatusText = ex.Message.StartsWith("Package builder setup is incomplete.", StringComparison.Ordinal)
                            ? "Setup incomplete. See runtime log."
                            : "Setup check failed. See runtime log.";
                    });
                    throw;
                }
            }));
        StatusText = "Setup check queued.";
        await Task.CompletedTask;
    }

    public async Task PublishSelectedProjectAsync()
    {
        var project = SelectedProject;
        if (!ValidateSelectedProject(project, requireExistingFolder: true, requireInitializedPaths: true))
        {
            return;
        }

        backgroundProcesses.Enqueue(new BackgroundProcessRequest(
            $"Publish {project!.DisplayName}",
            "sunder-package-builder",
            BackgroundProcessIndicator.Main,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup,
            CanCancel: true,
            async context =>
            {
                context.ReportIndeterminate("Running dotnet publish...");
                var execution = await executionService.ResolveAsync(project.WorkspaceId, context.CancellationToken);
                var projectFolder = ResolveExecutionProjectFolder(project);
                var result = await execution.RunProcessAsync("dotnet", ["publish", projectFolder], projectFolder, timeoutSeconds: 900, cancellationToken: context.CancellationToken);
                if (result.ExitCode != 0)
                {
                    RuntimeLogText = string.IsNullOrWhiteSpace(result.CombinedOutput) ? "dotnet publish failed." : result.CombinedOutput;
                    StatusText = "Publish failed. See runtime log.";
                    context.ReportProgress(100, "Publish failed. See runtime log.");
                    throw new InvalidOperationException("dotnet publish failed. See Builder runtime log for details.");
                }

                RuntimeLogText = string.IsNullOrWhiteSpace(result.CombinedOutput) ? "Publish completed." : result.CombinedOutput;
                project.Touch();
                await SaveProjectsAsync(context.CancellationToken);
                context.ReportProgress(100, "Sunder package publish completed.");
                StatusText = "Publish completed.";
            }));
        StatusText = "Publish queued.";
        await Task.CompletedTask;
    }

    public async Task LoadSelectedProjectAsync()
    {
        var project = SelectedProject;
        if (!ValidateSelectedProject(project, requireExistingFolder: true, requireInitializedPaths: true))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(project!.DevPackageRelativePath))
        {
            project.DevPackageRelativePath = DefaultDevPackageRelativePath;
        }

        project.DevPackageFolder = ResolveDevPackageFolder(project);
        if (!Directory.Exists(project.DevPackageFolder))
        {
            StatusText = "Build the project before loading; the sunder-dev folder does not exist.";
            return;
        }

        await RunBusyAsync(async () =>
        {
            RuntimeLogText = string.Empty;
            await LoadProjectDevPackageAsync(project, updateStatusText: true, CancellationToken.None);
        });
    }

    public async Task UnloadSelectedProjectAsync()
    {
        var project = SelectedProject;
        if (project is null || string.IsNullOrWhiteSpace(project.PackageId))
        {
            StatusText = "Select a package with a package id first.";
            return;
        }

        await RunBusyAsync(async () =>
        {
            var unloaded = await packageSessionService.UnloadPackageAsync(project.PackageId, PackageSessionSourceKind.Dev);
            IsSelectedProjectLoaded = false;
            StatusText = unloaded
                ? $"Unloaded dev package '{project.PackageId}'."
                : $"Dev package '{project.PackageId}' was not loaded.";
            await RefreshSelectedStatusAsync(updateStatusText: false);
        });
    }

    public async Task RefreshSelectedStatusAsync()
        => await RefreshSelectedStatusAsync(++_selectedProjectStatusVersion, updateStatusText: true);

    private async Task RefreshSelectedStatusAsync(bool updateStatusText)
        => await RefreshSelectedStatusAsync(++_selectedProjectStatusVersion, updateStatusText);

    private async Task RefreshSelectedStatusAsync(int version, bool updateStatusText)
    {
        var project = SelectedProject;
        if (project is null || string.IsNullOrWhiteSpace(project.PackageId))
        {
            IsSelectedProjectLoaded = false;
            return;
        }

        try
        {
            var status = await packageSessionService.GetPackageStatusAsync(project.PackageId);
            if (version != _selectedProjectStatusVersion || !ReferenceEquals(project, SelectedProject))
            {
                return;
            }

            IsSelectedProjectLoaded = status?.ActiveSourceKind == PackageSessionSourceKind.Dev;
            if (updateStatusText)
            {
                StatusText = status is null ? $"Package '{project.PackageId}' is not active." : FormatStatus(status);
            }
        }
        catch (Exception ex)
        {
            if (version == _selectedProjectStatusVersion && ReferenceEquals(project, SelectedProject))
            {
                IsSelectedProjectLoaded = false;
                if (updateStatusText)
                {
                    StatusText = $"Failed to read package status: {ex.Message}";
                }
            }
        }
    }

    public void ActivateProject(BuilderProjectViewModel? project)
    {
        if (project is null)
        {
            return;
        }

        SelectedProject = project;
        IsEditorActive = true;
    }

    public void BackToProjectList()
    {
        if (IsCompactLayout)
        {
            SelectedProject = null;
        }

        IsEditorActive = false;
    }

    public void ApplySelectedFolder(string folder)
    {
        if (!CanEditProjectIdentity || SelectedProject is null)
        {
            return;
        }

        SelectedProject.ProjectFolder = folder;
        EnsureDevPackageRelativePath(SelectedProject);
        SelectedProject.DevPackageFolder = ResolveDevPackageFolder(SelectedProject);
    }

    private async Task LoadProjectsAsync(CancellationToken cancellationToken = default)
    {
        var projects = await projectStore.LoadAsync(cancellationToken);
        await RunOnUiThreadAsync(() =>
        {
            var selectedProjectId = SelectedProject?.Id;
            Projects.Clear();
            foreach (var project in projects)
            {
                var projectViewModel = new BuilderProjectViewModel(project);
                NormalizeLoadedProject(projectViewModel);
                Projects.Add(projectViewModel);
            }

            SelectedProject = Projects.FirstOrDefault(project => project.Id == selectedProjectId)
                ?? (IsCompactLayout ? null : Projects.FirstOrDefault());
        });

        if (!_processedStartupAutoLoad)
        {
            _processedStartupAutoLoad = true;
            await LoadStartupAutoLoadProjectsAsync(cancellationToken);
        }
    }

    private void ReloadWorkspaces()
    {
        var selectedWorkspaceId = SelectedProject?.WorkspaceId;
        Workspaces.Clear();
        foreach (var workspace in executionService.ListWorkspaces().OrderBy(workspace => workspace.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            Workspaces.Add(workspace);
        }

        if (SelectedProject is not null && string.IsNullOrWhiteSpace(SelectedProject.WorkspaceId))
        {
            SelectedProject.WorkspaceId = Workspaces.FirstOrDefault()?.WorkspaceId ?? string.Empty;
        }

        if (SelectedProject is not null
            && !string.IsNullOrWhiteSpace(selectedWorkspaceId)
            && Workspaces.Any(workspace => string.Equals(workspace.WorkspaceId, selectedWorkspaceId, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedProject.WorkspaceId = selectedWorkspaceId;
        }

        RefreshWorkspacePathOptions(preserveSelection: true);
        OnPropertyChanged(nameof(Workspaces));
        NotifyProjectStatePropertiesChanged();
    }

    private void RefreshWorkspacePathOptions(bool preserveSelection)
    {
        var project = SelectedProject;
        WorkspacePathOptions.Clear();
        if (project is null || string.IsNullOrWhiteSpace(project.WorkspaceId))
        {
            NotifyProjectStatePropertiesChanged();
            return;
        }

        var workspace = FindWorkspace(project.WorkspaceId);
        if (workspace is null)
        {
            NotifyProjectStatePropertiesChanged();
            return;
        }

        foreach (var path in workspace.Paths.OrderBy(path => path.SortOrder))
        {
            WorkspacePathOptions.Add(new BuilderWorkspacePathOptionViewModel(path));
        }

        if (WorkspacePathOptions.Count == 0)
        {
            project.WorkspacePathId = string.Empty;
            NotifyProjectStatePropertiesChanged();
            return;
        }

        var selectedPathId = preserveSelection ? project.WorkspacePathId : null;
        var selectedPath = WorkspacePathOptions.FirstOrDefault(path => string.Equals(path.PathId, selectedPathId, StringComparison.OrdinalIgnoreCase))
            ?? WorkspacePathOptions.FirstOrDefault(path => path.IsDefault)
            ?? WorkspacePathOptions.First();
        project.WorkspacePathId = selectedPath.PathId;
        NotifyProjectStatePropertiesChanged();
    }

    private AgentWorkspaceRecord? FindWorkspace(string workspaceId)
        => Workspaces.FirstOrDefault(workspace => string.Equals(workspace.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase));

    private AgentWorkspacePathRecord? FindWorkspacePath(string workspaceId, string workspacePathId)
        => FindWorkspace(workspaceId)?.Paths.FirstOrDefault(path => string.Equals(path.PathId, workspacePathId, StringComparison.OrdinalIgnoreCase));

    private void NormalizeLoadedProject(BuilderProjectViewModel project)
    {
        if (string.IsNullOrWhiteSpace(project.DevPackageRelativePath))
        {
            project.DevPackageRelativePath = TryResolveRelativeDevPackagePath(project.ProjectFolder, project.DevPackageFolder)
                ?? DefaultDevPackageRelativePath;
        }

        project.DevPackageRelativePath = NormalizeDevPackageRelativePath(project.DevPackageRelativePath);
        if (!string.IsNullOrWhiteSpace(project.ProjectFolder))
        {
            project.DevPackageFolder = ResolveDevPackageFolder(project);
        }
    }

    private async Task<BuilderWorkspaceExecution> ResolveSelectedExecutionAsync(CancellationToken cancellationToken = default)
    {
        var workspaceId = SelectedProject?.WorkspaceId;
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            throw new InvalidOperationException("Select a workspace before continuing.");
        }

        return await executionService.ResolveAsync(workspaceId, cancellationToken);
    }

    private async Task SaveProjectsAsync(CancellationToken cancellationToken = default)
        => await projectStore.SaveAsync(Projects.Select(project => project.ToRecord()).ToArray(), cancellationToken);

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<IReadOnlyList<BuilderPrerequisiteStatus>> CheckAndApplySetupAsync(
        BuilderWorkspaceExecution execution,
        CancellationToken cancellationToken = default)
    {
        var statuses = await setupService.CheckAsync(execution, cancellationToken);
        var isSetupComplete = statuses.All(status => status.IsInstalled);
        await RunOnUiThreadAsync(() =>
        {
            SetupItems.Clear();
            foreach (var status in statuses)
            {
                SetupItems.Add(new BuilderPrerequisiteViewModel(status));
            }

            StatusText = isSetupComplete
                ? "Package builder setup is ready for this workspace."
                : "Install the missing prerequisites for this workspace.";
            NotifySetupStatePropertiesChanged();
            NotifyProjectStatePropertiesChanged();
        });

        return statuses;
    }

    private async Task<IReadOnlyList<BuilderPrerequisiteStatus>> EnsurePrerequisitesInstalledAsync(
        BuilderWorkspaceExecution execution,
        BackgroundProcessContext context)
    {
        context.ReportIndeterminate("Checking package builder prerequisites...");
        var statuses = await CheckAndApplySetupAsync(execution, context.CancellationToken);
        if (statuses.All(status => status.IsInstalled))
        {
            context.ReportProgress(100, "Package builder setup is already ready.");
            return statuses;
        }

        if (IsMissing(statuses, BuilderPrerequisiteKind.DotnetSdk))
        {
            context.ReportIndeterminate("Downloading .NET SDK installer...");
            var message = await setupService.InstallDotnetSdkAsync(execution, context.CancellationToken);
            await RunOnUiThreadAsync(() => StatusText = message);
            context.ReportIndeterminate(message);
            statuses = await CheckAndApplySetupAsync(execution, context.CancellationToken);
        }

        if (IsMissing(statuses, BuilderPrerequisiteKind.DotnetSdk))
        {
            context.ReportProgress(100, "Complete the .NET SDK installer, then recheck setup.");
            return statuses;
        }

        if (IsMissing(statuses, BuilderPrerequisiteKind.SunderTemplate))
        {
            context.ReportIndeterminate("Installing Sunder package template...");
            var message = await setupService.InstallTemplateAsync(execution, context.CancellationToken);
            await RunOnUiThreadAsync(() => StatusText = message);
            context.ReportIndeterminate(message);
            statuses = await CheckAndApplySetupAsync(execution, context.CancellationToken);
        }

        context.ReportProgress(
            100,
            statuses.All(status => status.IsInstalled)
                ? "Package builder setup is ready."
                : "Some prerequisites are still missing.");
        return statuses;
    }

    private static string BuildMissingPrerequisitesMessage(IReadOnlyList<BuilderPrerequisiteStatus> statuses)
    {
        var missing = statuses
            .Where(status => !status.IsInstalled)
            .Select(status => $"{status.Name}: {status.Detail}")
            .ToArray();
        return missing.Length == 0
            ? "Package builder setup is incomplete."
            : "Package builder setup is incomplete." + Environment.NewLine + string.Join(Environment.NewLine, missing);
    }

    private static bool IsMissing(IReadOnlyList<BuilderPrerequisiteStatus> statuses, BuilderPrerequisiteKind kind)
        => statuses.Any(status => status.Kind == kind && !status.IsInstalled);

    private void OnSelectedProjectPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, SelectedProject))
        {
            return;
        }

        if (e.PropertyName is nameof(BuilderProjectViewModel.ProjectFolder)
            or nameof(BuilderProjectViewModel.ExecutionProjectFolder)
            or nameof(BuilderProjectViewModel.WorkspaceId)
            or nameof(BuilderProjectViewModel.WorkspacePathId)
            or nameof(BuilderProjectViewModel.DisplayName)
            or nameof(BuilderProjectViewModel.PackageId)
            or nameof(BuilderProjectViewModel.DevPackageFolder)
            or nameof(BuilderProjectViewModel.DevPackageRelativePath))
        {
            var version = ++_selectedProjectStatusVersion;
            if (e.PropertyName is nameof(BuilderProjectViewModel.WorkspaceId))
            {
                RefreshWorkspacePathOptions(preserveSelection: false);
            }

            if (e.PropertyName is nameof(BuilderProjectViewModel.DevPackageRelativePath)
                && SelectedProject is not null
                && !string.IsNullOrWhiteSpace(SelectedProject.ProjectFolder))
            {
                try
                {
                    EnsureDevPackageRelativePath(SelectedProject);
                    SelectedProject.DevPackageFolder = ResolveDevPackageFolder(SelectedProject);
                }
                catch (Exception ex)
                {
                    StatusText = ex.Message;
                    return;
                }
            }

            UpdateSelectedProjectInitialized();
            IsSelectedProjectLoaded = false;
            if (e.PropertyName is nameof(BuilderProjectViewModel.DisplayName) && string.IsNullOrWhiteSpace(SelectedProject?.PackageId))
            {
                SelectedProject!.PackageId = ToPackageId(SelectedProject.DisplayName);
            }

            NotifyProjectStatePropertiesChanged();
            if (!string.IsNullOrWhiteSpace(SelectedProject?.PackageId))
            {
                _ = RefreshSelectedStatusAsync(version, updateStatusText: false);
            }
        }

        if (IsSelectedProjectInitialized
            && e.PropertyName is nameof(BuilderProjectViewModel.DevPackageFolder)
                or nameof(BuilderProjectViewModel.DevPackageRelativePath)
                or nameof(BuilderProjectViewModel.Watch)
                or nameof(BuilderProjectViewModel.AutoLoadOnStartup))
        {
            _ = SaveRuntimeProjectUpdateAsync(SelectedProject);
        }
    }

    private async Task SaveRuntimeProjectUpdateAsync(BuilderProjectViewModel? project)
    {
        if (project is null)
        {
            return;
        }

        try
        {
            project.Touch();
            await SaveProjectsAsync();
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(project, SelectedProject))
            {
                StatusText = $"Failed to save runtime settings: {ex.Message}";
            }
        }
    }

    private async Task LoadStartupAutoLoadProjectsAsync(CancellationToken cancellationToken)
    {
        foreach (var project in Projects.Where(static project => project.AutoLoadOnStartup).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await LoadProjectDevPackageAsync(project, updateStatusText: ReferenceEquals(project, SelectedProject), cancellationToken);
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(project, SelectedProject))
                {
                    StatusText = $"Auto load failed: {ex.Message}";
                }
            }
        }
    }

    private async Task<PackageSessionStatus?> LoadProjectDevPackageAsync(
        BuilderProjectViewModel project,
        bool updateStatusText,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(project.DevPackageRelativePath))
        {
            project.DevPackageRelativePath = DefaultDevPackageRelativePath;
        }

        project.DevPackageFolder = ResolveDevPackageFolder(project);
        if (string.IsNullOrWhiteSpace(project.DevPackageFolder) || !Directory.Exists(project.DevPackageFolder))
        {
            if (updateStatusText)
            {
                StatusText = "Build the project before loading; the sunder-dev folder does not exist.";
            }

            return null;
        }

        var status = await packageSessionService.LoadPackageAsync(new PackageSessionLoadRequest(
            PackageSessionSourceKind.Dev,
            project.DevPackageFolder,
            project.Watch), cancellationToken);
        project.PackageId = status.PackageId;
        project.Touch();
        await SaveProjectsAsync(cancellationToken);
        if (ReferenceEquals(project, SelectedProject))
        {
            IsSelectedProjectLoaded = status.ActiveSourceKind == PackageSessionSourceKind.Dev;
        }

        if (updateStatusText)
        {
            StatusText = FormatStatus(status);
        }

        return status;
    }

    private bool ValidateSelectedProject(
        BuilderProjectViewModel? project,
        bool requireExistingFolder,
        bool requireInitializedPaths)
    {
        if (project is null)
        {
            StatusText = "Create or select a package project first.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(project.DisplayName))
        {
            StatusText = "Package name is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(project.PackageId))
        {
            project.PackageId = ToPackageId(project.DisplayName);
        }

        if (string.IsNullOrWhiteSpace(project.WorkspaceId))
        {
            StatusText = "Workspace is required.";
            return false;
        }

        if (!Workspaces.Any(workspace => string.Equals(workspace.WorkspaceId, project.WorkspaceId, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = "Selected workspace was not found.";
            return false;
        }

        if (FindWorkspace(project.WorkspaceId)?.Paths.Count == 0)
        {
            StatusText = "Selected workspace has no workspace paths.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(project.WorkspacePathId))
        {
            StatusText = "Workspace path is required.";
            return false;
        }

        if (FindWorkspacePath(project.WorkspaceId, project.WorkspacePathId) is null)
        {
            StatusText = "Selected workspace path was not found.";
            return false;
        }

        try
        {
            project.DevPackageRelativePath = NormalizeDevPackageRelativePath(project.DevPackageRelativePath);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return false;
        }

        if (!requireInitializedPaths)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(project.ExecutionProjectFolder))
        {
            StatusText = "Initialize the package project first.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(project.ProjectFolder))
        {
            StatusText = "Host project folder is not available. Reinitialize the package project.";
            return false;
        }

        project.ProjectFolder = Path.GetFullPath(project.ProjectFolder);
        EnsureDevPackageRelativePath(project);
        project.DevPackageFolder = ResolveDevPackageFolder(project);

        if (requireExistingFolder && !Directory.Exists(project.ProjectFolder))
        {
            StatusText = "Project folder does not exist.";
            return false;
        }

        return true;
    }

    private async Task InitializeProjectInFolderAsync(
        BuilderProjectViewModel project,
        BuilderProjectInitializationDraft draft,
        BuilderWorkspaceExecution execution,
        BackgroundProcessContext context)
    {
        var workspacePath = FindWorkspacePath(draft.WorkspaceId, draft.WorkspacePathId)
            ?? throw new InvalidOperationException("Selected workspace path was not found.");
        var executionRoot = execution.ResolveExecutionWorkspacePath(workspacePath);
        var executionProjectFolder = execution.CombinePath(executionRoot, ToProjectName(draft.DisplayName));
        var hostMapping = await execution.MapToHostPathAsync(executionProjectFolder, context.CancellationToken);
        if (!hostMapping.IsInsideAllowedRoot)
        {
            throw new InvalidOperationException("Generated project path is outside the selected workspace paths.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(hostMapping.HostPath) ?? hostMapping.HostPath);
        if (Directory.Exists(hostMapping.HostPath))
        {
            EnsureProjectFolderCanBeInitialized(hostMapping.HostPath);
        }

        var executionWorkingDirectory = GetExecutionParentFolder(executionProjectFolder) ?? execution.DefaultExecutionRoot;

        var result = await execution.RunProcessAsync(
            "dotnet",
            BuildTemplateArguments(draft, executionProjectFolder, createInPlace: true),
            executionWorkingDirectory,
            cancellationToken: context.CancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.CombinedOutput) ? "Package initialization failed." : result.CombinedOutput);
        }

        project.DisplayName = draft.DisplayName;
        project.PackageId = draft.PackageId;
        project.WorkspaceId = draft.WorkspaceId;
        project.WorkspacePathId = draft.WorkspacePathId;
        project.ExecutionProjectFolder = executionProjectFolder;
        project.ProjectFolder = hostMapping.HostPath;
        project.DevPackageRelativePath = NormalizeDevPackageRelativePath(draft.DevPackageRelativePath);
        project.DevPackageFolder = ResolveDevPackageFolder(project);
    }

    private static string[] BuildTemplateArguments(BuilderProjectInitializationDraft project, string outputFolder, bool createInPlace)
    {
        List<string> arguments =
        [
            "new",
            "sunder-package",
            "--name",
            ToProjectName(project.DisplayName),
            "--packageId",
            project.PackageId,
            "--packageName",
            project.DisplayName,
            "--output",
            outputFolder,
        ];

        if (createInPlace)
        {
            arguments.Add("--createInPlace");
        }

        return [.. arguments];
    }

    private static void EnsureProjectFolderCanBeInitialized(string projectFolder)
    {
        var existingEntries = Directory
            .EnumerateFileSystemEntries(projectFolder)
            .Where(entry => !IsIgnorableProjectFolderEntry(entry))
            .ToArray();
        if (existingEntries.Length > 0)
        {
            throw new InvalidOperationException("Choose an empty folder before initializing a package project.");
        }
    }

    private static bool IsIgnorableProjectFolderEntry(string path)
        => string.Equals(Path.GetFileName(path), ".DS_Store", StringComparison.OrdinalIgnoreCase);

    private static string? GetExecutionParentFolder(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var separatorIndex = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        if (separatorIndex <= 0)
        {
            return null;
        }

        if (trimmed.Length > 2 && trimmed[1] == ':' && separatorIndex == 2)
        {
            return trimmed[..3];
        }

        return trimmed[..separatorIndex];
    }

    private static bool ShouldFallbackToStaging(BuilderProcessResult result)
    {
        var output = result.CombinedOutput;
        return output.Contains("createInPlace", StringComparison.OrdinalIgnoreCase)
            && (output.Contains("invalid option", StringComparison.OrdinalIgnoreCase)
                || output.Contains("not a valid", StringComparison.OrdinalIgnoreCase)
                || output.Contains("not supported", StringComparison.OrdinalIgnoreCase)
                || output.Contains("unrecognized", StringComparison.OrdinalIgnoreCase)
                || output.Contains("unknown", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveGeneratedProjectFolder(string stagingRoot, string projectName)
    {
        var expectedProjectFile = Directory
            .EnumerateFiles(stagingRoot, projectName + ".csproj", SearchOption.AllDirectories)
            .FirstOrDefault(path => !IsContractsProjectFile(path));
        if (expectedProjectFile is not null)
        {
            return Path.GetDirectoryName(expectedProjectFile)!;
        }

        var projectFiles = Directory
            .EnumerateFiles(stagingRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsContractsProjectFile(path))
            .ToArray();
        return projectFiles.Length == 1
            ? Path.GetDirectoryName(projectFiles[0])!
            : throw new InvalidOperationException("Generated package project could not be located.");
    }

    private static bool IsContractsProjectFile(string path)
        => Path.GetFileNameWithoutExtension(path).EndsWith(".Contracts", StringComparison.OrdinalIgnoreCase);

    private static void MoveGeneratedProjectContents(string stagingRoot, string generatedProjectFolder, string destinationFolder)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(generatedProjectFolder).ToArray())
        {
            MoveGeneratedEntry(entry, destinationFolder);
        }

        var stagingRootFullPath = Path.GetFullPath(stagingRoot);
        var generatedProjectFolderFullPath = Path.GetFullPath(generatedProjectFolder);
        if (PathsEqual(stagingRootFullPath, generatedProjectFolderFullPath))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(stagingRoot).ToArray())
        {
            if (PathsEqual(Path.GetFullPath(entry), generatedProjectFolderFullPath))
            {
                continue;
            }

            MoveGeneratedEntry(entry, destinationFolder);
        }
    }

    private static void MoveGeneratedEntry(string sourcePath, string destinationFolder)
    {
        var destinationPath = Path.Combine(destinationFolder, Path.GetFileName(sourcePath));
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            throw new InvalidOperationException($"Generated package content conflicts with existing path: {destinationPath}");
        }

        if (Directory.Exists(sourcePath))
        {
            Directory.Move(sourcePath, destinationPath);
        }
        else
        {
            File.Move(sourcePath, destinationPath);
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // A stale temp staging folder should not hide the initialization result.
        }
    }

    private void UpdateSelectedProjectInitialized()
    {
        IsSelectedProjectInitialized = IsProjectInitialized(SelectedProject);
    }

    private static bool IsProjectInitialized(BuilderProjectViewModel? project)
    {
        if (project is null || string.IsNullOrWhiteSpace(project.ProjectFolder) || !Directory.Exists(project.ProjectFolder))
        {
            return false;
        }

        try
        {
            return Directory
                .EnumerateFiles(project.ProjectFolder, "*.csproj", SearchOption.TopDirectoryOnly)
                .Any(path => !IsContractsProjectFile(path));
        }
        catch
        {
            return false;
        }
    }

    private void NotifySetupStatePropertiesChanged()
    {
        OnPropertyChanged(nameof(ShowSelectedProjectSetup));
        OnPropertyChanged(nameof(CanInitializeSelectedProject));
    }

    private void NotifyProjectStatePropertiesChanged()
    {
        OnPropertyChanged(nameof(HasSelectedProject));
        OnPropertyChanged(nameof(CanEditSelectedProject));
        OnPropertyChanged(nameof(CanEditProjectIdentity));
        OnPropertyChanged(nameof(IsProjectIdentityReadOnly));
        OnPropertyChanged(nameof(CanUseSelectedProjectRuntimeActions));
        OnPropertyChanged(nameof(ShowRuntimeSection));
        OnPropertyChanged(nameof(ShowInitializeSelectedProject));
        OnPropertyChanged(nameof(ShowSelectedProjectSetup));
        OnPropertyChanged(nameof(CanInitializeSelectedProject));
        OnPropertyChanged(nameof(ShowLoadSelectedProject));
        OnPropertyChanged(nameof(ShowUnloadSelectedProject));
    }

    private void NotifyLayoutPropertiesChanged()
    {
        OnPropertyChanged(nameof(IsListActive));
        OnPropertyChanged(nameof(ShowWideLayout));
        OnPropertyChanged(nameof(ShowCompactList));
        OnPropertyChanged(nameof(ShowCompactEditor));
        OnPropertyChanged(nameof(ShowListPane));
        OnPropertyChanged(nameof(ShowEditorPane));
    }

    private static Task RunOnUiThreadAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                action();
                completion.SetResult(null);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }

    private static void EnsureDevPackageRelativePath(BuilderProjectViewModel project)
    {
        if (string.IsNullOrWhiteSpace(project.DevPackageRelativePath))
        {
            project.DevPackageRelativePath = DefaultDevPackageRelativePath;
        }

        project.DevPackageRelativePath = NormalizeDevPackageRelativePath(project.DevPackageRelativePath);
    }

    private static string ResolveDevPackageFolder(BuilderProjectViewModel project)
        => ResolveDevPackageFolder(project.ProjectFolder, project.DevPackageRelativePath);

    private static string ResolveDevPackageFolder(string projectFolder, string devPackageRelativePath)
    {
        var normalized = NormalizeDevPackageRelativePath(devPackageRelativePath);
        var parts = normalized.TrimStart('/', '\\')
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Path.GetFullPath(Path.Combine([Path.GetFullPath(projectFolder), .. parts]));
    }

    private static string NormalizeDevPackageRelativePath(string? relativePath)
    {
        var value = string.IsNullOrWhiteSpace(relativePath)
            ? DefaultDevPackageRelativePath
            : relativePath.Trim().Replace('\\', '/');
        value = "/" + value.TrimStart('/');
        if (value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(part => part == ".."))
        {
            throw new InvalidOperationException("sunder-dev folder must stay inside the package project folder.");
        }

        return value;
    }

    private static string? TryResolveRelativeDevPackagePath(string projectFolder, string devPackageFolder)
    {
        if (string.IsNullOrWhiteSpace(projectFolder) || string.IsNullOrWhiteSpace(devPackageFolder))
        {
            return null;
        }

        var projectRoot = Path.GetFullPath(projectFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var devFolder = Path.GetFullPath(devPackageFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(projectRoot, devFolder, comparison)
            && !devFolder.StartsWith(projectRoot + Path.DirectorySeparatorChar, comparison)
            && !devFolder.StartsWith(projectRoot + Path.AltDirectorySeparatorChar, comparison))
        {
            return null;
        }

        var relative = Path.GetRelativePath(projectRoot, devFolder).Replace(Path.DirectorySeparatorChar, '/');
        return string.IsNullOrWhiteSpace(relative) || relative == "."
            ? "/"
            : NormalizeDevPackageRelativePath(relative);
    }

    private static string ResolveExecutionProjectFolder(BuilderProjectViewModel project)
        => string.IsNullOrWhiteSpace(project.ExecutionProjectFolder)
            ? project.ProjectFolder
            : project.ExecutionProjectFolder;

    private sealed record BuilderProjectInitializationDraft(
        string DisplayName,
        string PackageId,
        string WorkspaceId,
        string WorkspacePathId,
        string ExecutionProjectFolder,
        string DevPackageRelativePath)
    {
        public static BuilderProjectInitializationDraft From(BuilderProjectViewModel project)
            => new(
                project.DisplayName.Trim(),
                project.PackageId.Trim(),
                project.WorkspaceId.Trim(),
                project.WorkspacePathId.Trim(),
                project.ExecutionProjectFolder.Trim(),
                NormalizeDevPackageRelativePath(project.DevPackageRelativePath));
    }

    private static string ToProjectName(string displayName)
    {
        var characters = displayName
            .Where(character => char.IsLetterOrDigit(character))
            .ToArray();
        return characters.Length == 0 ? "SunderPackage" : new string(characters);
    }

    private static string ToPackageId(string displayName)
    {
        var parts = displayName
            .ToLowerInvariant()
            .Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => new string(part.Where(char.IsLetterOrDigit).ToArray()))
            .Where(part => part.Length > 0)
            .ToArray();
        return parts.Length == 0 ? "local.sunder.package" : "local." + string.Join('.', parts);
    }

    private static string FormatStatus(PackageSessionStatus status)
    {
        var source = status.ActiveSourceKind == PackageSessionSourceKind.Dev ? "dev" : "installed";
        var overlay = status.OverridesInstalledPackage ? " overriding installed package" : string.Empty;
        var watch = status.WatchEnabled ? " Watch is enabled." : string.Empty;
        var error = string.IsNullOrWhiteSpace(status.ErrorMessage) ? string.Empty : $" Last error: {status.ErrorMessage}";
        return $"{status.PackageId} {status.Version} loaded from {source}{overlay}.{watch}{error}";
    }

    private void ClearStatus() => StatusText = string.Empty;

    private void ShowStatusMessageForCurrentText()
    {
        var version = Interlocked.Increment(ref _statusMessageVersion);
        ShowStatusMessage = !string.IsNullOrWhiteSpace(StatusText);
        if (ShowStatusMessage)
        {
            _ = HideStatusMessageAfterDelayAsync(version);
        }
    }

    private async Task HideStatusMessageAfterDelayAsync(long version)
    {
        await Task.Delay(StatusMessageVisibleDuration);
        if (Interlocked.Read(ref _statusMessageVersion) == version)
        {
            ShowStatusMessage = false;
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class BuilderPrerequisiteViewModel(BuilderPrerequisiteStatus status)
{
    public BuilderPrerequisiteKind Kind { get; } = status.Kind;

    public string Name { get; } = status.Name;

    public bool IsInstalled { get; } = status.IsInstalled;

    public string Detail { get; } = status.Detail;

    public string StateText => IsInstalled ? "Installed" : "Missing";
}

public sealed class BuilderWorkspacePathOptionViewModel(AgentWorkspacePathRecord path)
{
    public string PathId { get; } = path.PathId;

    public string HostPath { get; } = path.HostPath;

    public bool IsDefault { get; } = path.IsDefault;

    public string DisplayPath { get; } = FormatPath(path.HostPath);

    private static string FormatPath(string hostPath)
    {
        if (string.IsNullOrWhiteSpace(hostPath))
        {
            return string.Empty;
        }

        var fullPath = Path.GetFullPath(hostPath.Trim());
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            var normalizedHome = Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), normalizedHome, comparison)
                || fullPath.StartsWith(normalizedHome + Path.DirectorySeparatorChar, comparison)
                || fullPath.StartsWith(normalizedHome + Path.AltDirectorySeparatorChar, comparison))
            {
                var relative = fullPath[normalizedHome.Length..]
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace(Path.DirectorySeparatorChar, '/');
                return string.IsNullOrWhiteSpace(relative) ? "~" : $"~/{relative}";
            }
        }

        return fullPath.Replace(Path.DirectorySeparatorChar, '/');
    }
}

public sealed class BuilderProjectViewModel(BuilderProjectRecord record) : INotifyPropertyChanged
{
    private string _displayName = record.DisplayName ?? string.Empty;
    private string _packageId = record.PackageId ?? string.Empty;
    private string _workspaceId = record.WorkspaceId ?? string.Empty;
    private string _workspacePathId = record.WorkspacePathId ?? string.Empty;
    private string _executionProjectFolder = record.ExecutionProjectFolder ?? string.Empty;
    private string _projectFolder = record.ProjectFolder ?? string.Empty;
    private string _devPackageFolder = record.DevPackageFolder ?? string.Empty;
    private string _devPackageRelativePath = record.DevPackageRelativePath ?? string.Empty;
    private bool _watch = record.Watch;
    private bool _autoLoadOnStartup = record.AutoLoadOnStartup;
    private DateTimeOffset _updatedAtUtc = record.UpdatedAtUtc;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; } = record.Id;

    public DateTimeOffset CreatedAtUtc { get; } = record.CreatedAtUtc;

    public string DisplayName
    {
        get => _displayName;
        set => SetField(ref _displayName, value);
    }

    public string PackageId
    {
        get => _packageId;
        set => SetField(ref _packageId, value);
    }

    public string WorkspaceId
    {
        get => _workspaceId;
        set => SetField(ref _workspaceId, value);
    }

    public string WorkspacePathId
    {
        get => _workspacePathId;
        set => SetField(ref _workspacePathId, value);
    }

    public string ExecutionProjectFolder
    {
        get => _executionProjectFolder;
        set => SetField(ref _executionProjectFolder, value);
    }

    public string ProjectFolder
    {
        get => _projectFolder;
        set => SetField(ref _projectFolder, value);
    }

    public string DevPackageFolder
    {
        get => _devPackageFolder;
        set => SetField(ref _devPackageFolder, value);
    }

    public string DevPackageRelativePath
    {
        get => _devPackageRelativePath;
        set => SetField(ref _devPackageRelativePath, value);
    }

    public bool Watch
    {
        get => _watch;
        set => SetField(ref _watch, value);
    }

    public bool AutoLoadOnStartup
    {
        get => _autoLoadOnStartup;
        set => SetField(ref _autoLoadOnStartup, value);
    }

    public DateTimeOffset UpdatedAtUtc
    {
        get => _updatedAtUtc;
        private set => SetField(ref _updatedAtUtc, value);
    }

    public void Touch() => UpdatedAtUtc = DateTimeOffset.UtcNow;

    public BuilderProjectRecord ToRecord()
        => new(
            Id,
            DisplayName.Trim(),
            PackageId.Trim(),
            WorkspaceId.Trim(),
            ExecutionProjectFolder.Trim(),
            ProjectFolder.Trim(),
            DevPackageFolder.Trim(),
            Watch,
            CreatedAtUtc,
            UpdatedAtUtc)
        {
            AutoLoadOnStartup = AutoLoadOnStartup,
            WorkspacePathId = WorkspacePathId.Trim(),
            DevPackageRelativePath = DevPackageRelativePath.Trim(),
        };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
