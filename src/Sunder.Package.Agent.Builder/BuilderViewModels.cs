using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private static readonly TimeSpan StatusMessageVisibleDuration = TimeSpan.FromSeconds(3);

    private readonly BuilderProjectApplicationService _applicationService;
    private readonly BuilderOperationQueue _operationQueue;
    private readonly BuilderProjectPersistence _persistence;
    private readonly BuilderPathService _pathService;
    private readonly IBuilderUiDispatcher _uiDispatcher;
    private readonly PresentationTaskScope _tasks = new();
    private readonly TimedStatusController _statusVisibility = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _initializationSync = new();
    private BuilderProjectViewModel? _selectedProject;
    private Task? _initializationTask;
    private Task? _statusVisibilityTask;
    private string _statusText = string.Empty;
    private string _runtimeLogText = string.Empty;
    private bool _isBusy;
    private bool _isSetupComplete;
    private bool _projectsLoaded;
    private bool _isCompactLayout;
    private bool _isEditorActive;
    private bool _isSelectedProjectInitialized;
    private bool _isSelectedProjectInitializing;
    private bool _showStatusMessage;
    private bool _suppressSelectedProjectChanges;
    private bool _disposed;

    public BuilderViewModel(
        BuilderProjectApplicationService applicationService,
        BuilderOperationQueue operationQueue,
        BuilderProjectPersistence persistence,
        BuilderPathService pathService,
        IBuilderUiDispatcher uiDispatcher)
    {
        _applicationService = applicationService;
        _operationQueue = operationQueue;
        _persistence = persistence;
        _pathService = pathService;
        _uiDispatcher = uiDispatcher;
        _persistence.SaveFailed += OnPersistenceSaveFailed;
    }

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

    public bool CanRunSelectedProjectOperations => CanEditSelectedProject;

    public bool ShowBuildSection => HasSelectedProject && IsSelectedProjectInitialized;

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

    public bool IsCompactLayout
    {
        get => _isCompactLayout;
        set
        {
            if (!SetField(ref _isCompactLayout, value))
            {
                return;
            }

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
            var previous = _selectedProject;
            if (!SetField(ref _selectedProject, value))
            {
                return;
            }

            if (previous is not null)
            {
                previous.PropertyChanged -= OnSelectedProjectPropertyChanged;
            }

            if (value is null)
            {
                WorkspacePathOptions.Clear();
            }
            else
            {
                if (string.IsNullOrWhiteSpace(value.WorkspaceId))
                {
                    value.WorkspaceId = Workspaces.FirstOrDefault()?.WorkspaceId ?? string.Empty;
                }

                ApplyProjectRecord(value, value.ToRecord());
                RefreshWorkspacePathOptions(preserveSelection: true);
                value.PropertyChanged += OnSelectedProjectPropertyChanged;
                if (IsCompactLayout)
                {
                    IsEditorActive = true;
                }
            }

            UpdateSelectedProjectInitialized();
            RuntimeLogText = string.Empty;
            NotifyProjectStatePropertiesChanged();
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

    public Task EnsureSelectedProjectSetupAsync()
    {
        if (!TryValidateSelectedProject(requireExistingFolder: false, requireInitializedPaths: false, out _, out var record))
        {
            return Task.CompletedTask;
        }

        RuntimeLogText = string.Empty;
        _operationQueue.Enqueue(
            $"Check setup for {record.DisplayName}",
            context => RunEnsureSetupOperationAsync(record, context));
        StatusText = "Setup check queued.";
        return Task.CompletedTask;
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

        ApplyProjectRecord(SelectedProject, SelectedProject.ToRecord() with
        {
            ProjectFolder = folder,
        });
    }

    public async ValueTask DisposeAsync()
    {
        Task? initializationTask;
        lock (_initializationSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            initializationTask = _initializationTask;
        }

        _persistence.SaveFailed -= OnPersistenceSaveFailed;
        _statusVisibility.Dispose();
        _tasks.Dispose();
        _lifetime.Cancel();
        if (initializationTask is not null)
        {
            try
            {
                await initializationTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
        }
        var projects = await _uiDispatcher.InvokeAsync(() =>
        {
            if (SelectedProject is not null)
            {
                SelectedProject.PropertyChanged -= OnSelectedProjectPropertyChanged;
            }

            return _projectsLoaded ? CaptureProjects() : null;
        }).ConfigureAwait(false);
        if (projects is not null)
        {
            await _persistence.SaveNowAsync(projects, CancellationToken.None).ConfigureAwait(false);
        }
        await _persistence.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private void ApplyLoadedProjects(IReadOnlyList<BuilderProjectRecord> projects)
    {
        var selectedProjectId = SelectedProject?.Id;
        Projects.Clear();
        foreach (var project in projects)
        {
            Projects.Add(new BuilderProjectViewModel(project));
        }

        SelectedProject = Projects.FirstOrDefault(project => project.Id == selectedProjectId)
            ?? (IsCompactLayout ? null : Projects.FirstOrDefault());
    }

    private void ApplyLoadedWorkspaces(IReadOnlyList<AgentWorkspaceRecord> workspaces)
    {
        var selectedWorkspaceId = SelectedProject?.WorkspaceId;
        Workspaces.Clear();
        foreach (var workspace in workspaces.OrderBy(workspace => workspace.DisplayName, StringComparer.OrdinalIgnoreCase))
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
        var workspace = project is null ? null : FindWorkspace(project.WorkspaceId);
        if (workspace is null)
        {
            NotifyProjectStatePropertiesChanged();
            return;
        }

        foreach (var path in workspace.Paths.OrderBy(path => path.SortOrder))
        {
            WorkspacePathOptions.Add(new BuilderWorkspacePathOptionViewModel(path, _pathService));
        }

        _suppressSelectedProjectChanges = true;
        try
        {
            if (WorkspacePathOptions.Count == 0)
            {
                project!.WorkspacePathId = string.Empty;
            }
            else
            {
                var selectedPathId = preserveSelection ? project!.WorkspacePathId : null;
                project!.WorkspacePathId = (WorkspacePathOptions.FirstOrDefault(path => string.Equals(path.PathId, selectedPathId, StringComparison.OrdinalIgnoreCase))
                    ?? WorkspacePathOptions.FirstOrDefault(path => path.IsDefault)
                    ?? WorkspacePathOptions[0]).PathId;
            }
        }
        finally
        {
            _suppressSelectedProjectChanges = false;
        }

        NotifyProjectStatePropertiesChanged();
    }

    private bool TryValidateSelectedProject(
        bool requireExistingFolder,
        bool requireInitializedPaths,
        out BuilderProjectViewModel project,
        out BuilderProjectRecord record)
    {
        project = SelectedProject!;
        record = null!;
        if (project is null)
        {
            StatusText = "Create or select a package project first.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(project.PackageId) && !string.IsNullOrWhiteSpace(project.DisplayName))
        {
            project.PackageId = _pathService.ToPackageId(project.DisplayName);
        }

        var validation = _applicationService.ValidateProject(
            project.ToRecord(),
            Workspaces,
            requireExistingFolder,
            requireInitializedPaths);
        if (!validation.IsValid)
        {
            StatusText = validation.Error ?? "Package project is invalid.";
            return false;
        }

        record = validation.Project!;
        ApplyProjectRecord(project, record);
        return true;
    }

    private void OnSelectedProjectPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressSelectedProjectChanges || !ReferenceEquals(sender, SelectedProject) || SelectedProject is null)
        {
            return;
        }

        var project = SelectedProject;
        if (e.PropertyName == nameof(BuilderProjectViewModel.WorkspaceId))
        {
            RefreshWorkspacePathOptions(preserveSelection: false);
        }

        if (e.PropertyName == nameof(BuilderProjectViewModel.DisplayName) && string.IsNullOrWhiteSpace(project.PackageId))
        {
            project.PackageId = _pathService.ToPackageId(project.DisplayName);
        }

        UpdateSelectedProjectInitialized();
        NotifyProjectStatePropertiesChanged();
    }

    private void ApplyProjectRecord(BuilderProjectViewModel project, BuilderProjectRecord record)
    {
        _suppressSelectedProjectChanges = true;
        try
        {
            project.Apply(record);
        }
        finally
        {
            _suppressSelectedProjectChanges = false;
        }
    }

    private void ApplySetupStatuses(IReadOnlyList<BuilderPrerequisiteStatus> statuses)
    {
        SetupItems.Clear();
        foreach (var status in statuses)
        {
            SetupItems.Add(new BuilderPrerequisiteViewModel(status));
        }

        IsSetupComplete = statuses.All(status => status.IsInstalled);
        StatusText = IsSetupComplete
            ? "Package builder setup is ready for this workspace."
            : "Install the missing prerequisites for this workspace.";
        NotifySetupStatePropertiesChanged();
        NotifyProjectStatePropertiesChanged();
    }

    private (IReadOnlyList<BuilderProjectRecord> Projects, string Name, bool ClearSelection)? RemoveSelectedProject()
    {
        if (SelectedProject is null)
        {
            return null;
        }

        var project = SelectedProject;
        var clearSelection = IsCompactLayout;
        Projects.Remove(project);
        SelectedProject = clearSelection ? null : Projects.FirstOrDefault();
        return (CaptureProjects(), project.DisplayName, clearSelection);
    }

    private void CompleteProjectDeletion((IReadOnlyList<BuilderProjectRecord> Projects, string Name, bool ClearSelection) deletion)
    {
        if (deletion.ClearSelection)
        {
            StatusText = string.Empty;
        }
        else
        {
            StatusText = $"Deleted package project '{deletion.Name}'.";
        }

        IsEditorActive = false;
    }

    private IReadOnlyList<BuilderProjectRecord> CaptureProjects()
        => Projects.Select(project => project.ToRecord()).ToArray();

    private AgentWorkspaceRecord? FindWorkspace(string workspaceId)
        => Workspaces.FirstOrDefault(workspace => string.Equals(workspace.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase));

    private AgentWorkspacePathRecord? FindWorkspacePath(string workspaceId, string workspacePathId)
        => FindWorkspace(workspaceId)?.Paths.FirstOrDefault(path => string.Equals(path.PathId, workspacePathId, StringComparison.OrdinalIgnoreCase));

    private void UpdateSelectedProjectInitialized()
        => IsSelectedProjectInitialized = _pathService.IsProjectInitialized(SelectedProject?.ToRecord());

    private void OnPersistenceSaveFailed(object? sender, BuilderPersistenceFailedEventArgs e)
        => _tasks.Run(_ => _uiDispatcher.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                StatusText = $"Failed to save runtime settings: {e.Exception.Message}";
            }
        }));

    private void ShowStatusMessageForCurrentText()
    {
        ShowStatusMessage = !string.IsNullOrWhiteSpace(StatusText);
        if (!ShowStatusMessage)
        {
            _statusVisibility.Cancel();
            return;
        }

        _statusVisibilityTask = _statusVisibility.ScheduleAsync(
            StatusMessageVisibleDuration,
            () => _tasks.Run(_ => _uiDispatcher.InvokeAsync(() => ShowStatusMessage = false)));
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
        OnPropertyChanged(nameof(CanRunSelectedProjectOperations));
        OnPropertyChanged(nameof(ShowBuildSection));
        OnPropertyChanged(nameof(ShowInitializeSelectedProject));
        OnPropertyChanged(nameof(ShowSelectedProjectSetup));
        OnPropertyChanged(nameof(CanInitializeSelectedProject));
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

    private static string BuildMissingPrerequisitesMessage(IReadOnlyList<BuilderPrerequisiteStatus> statuses)
    {
        var missing = statuses.Where(status => !status.IsInstalled).Select(status => $"{status.Name}: {status.Detail}").ToArray();
        return missing.Length == 0
            ? "Package builder setup is incomplete."
            : "Package builder setup is incomplete." + Environment.NewLine + string.Join(Environment.NewLine, missing);
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
