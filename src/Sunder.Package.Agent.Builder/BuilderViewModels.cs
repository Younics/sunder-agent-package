using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Threading;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Builder;

public sealed class BuilderViewModel(
    BuilderSetupService setupService,
    BuilderProjectStore projectStore,
    IPackageSessionService packageSessionService,
    IBackgroundProcessQueue backgroundProcesses) : INotifyPropertyChanged
{
    private static readonly TimeSpan StatusMessageVisibleDuration = TimeSpan.FromSeconds(3);

    private BuilderProjectViewModel? _selectedProject;
    private string _statusText = "Checking package builder setup...";
    private string _runtimeLogText = string.Empty;
    private bool _isBusy;
    private bool _isSetupComplete;
    private bool _isSetupInstallQueued;
    private bool _initialized;
    private bool _processedStartupAutoLoad;
    private bool _isCompactLayout;
    private bool _isEditorActive;
    private bool _isSelectedProjectInitialized;
    private bool _isSelectedProjectLoaded;
    private bool _showStatusMessage;
    private int _selectedProjectStatusVersion;
    private long _statusMessageVersion;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<BuilderPrerequisiteViewModel> SetupItems { get; } = [];

    public ObservableCollection<BuilderProjectViewModel> Projects { get; } = [];

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

    public bool ShowSetup => !IsSetupComplete;

    public bool ShowProjects => IsSetupComplete;

    public bool IsSetupInstallQueued
    {
        get => _isSetupInstallQueued;
        private set
        {
            if (SetField(ref _isSetupInstallQueued, value))
            {
                NotifySetupStatePropertiesChanged();
            }
        }
    }

    public bool CanInstallMissingPrerequisites => !IsSetupComplete && !IsBusy && !IsSetupInstallQueued && SetupItems.Any(item => !item.IsInstalled);

    public bool HasSelectedProject => SelectedProject is not null;

    public bool CanEditSelectedProject => HasSelectedProject && !IsBusy;

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

    public bool ShowInitializeSelectedProject => HasSelectedProject && !IsSelectedProjectInitialized;

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
                    value.PropertyChanged += OnSelectedProjectPropertyChanged;
                    if (IsCompactLayout)
                    {
                        IsEditorActive = true;
                    }
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
        await RefreshSetupAsync();
    }

    public async Task RefreshSetupAsync()
    {
        await RunBusyAsync(async () =>
        {
            await CheckAndApplySetupAsync();
        });
    }

    public Task InstallMissingPrerequisitesAsync()
    {
        if (!CanInstallMissingPrerequisites)
        {
            return Task.CompletedTask;
        }

        IsSetupInstallQueued = true;
        backgroundProcesses.Enqueue(new BackgroundProcessRequest(
            "Install package builder prerequisites",
            "sunder-package-builder-setup",
            BackgroundProcessIndicator.Packages,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup,
            CanCancel: true,
            async context =>
            {
                try
                {
                    await InstallMissingPrerequisitesCoreAsync(context);
                }
                finally
                {
                    IsSetupInstallQueued = false;
                }
            }));
        StatusText = "Prerequisite installation queued.";
        return Task.CompletedTask;
    }

    public async Task InstallDotnetSdkAsync()
    {
        await RunBusyAsync(async () =>
        {
            StatusText = "Downloading .NET SDK installer...";
            StatusText = await setupService.InstallDotnetSdkAsync();
            await CheckAndApplySetupAsync();
        });
    }

    public async Task InstallTemplateAsync()
    {
        await RunBusyAsync(async () =>
        {
            StatusText = "Installing Sunder package template...";
            StatusText = await setupService.InstallTemplateAsync();
            await CheckAndApplySetupAsync();
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
            Watch: true,
            now,
            now));
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

        var project = SelectedProject;
        Projects.Remove(project);
        SelectedProject = Projects.FirstOrDefault();
        IsEditorActive = SelectedProject is not null;
        await SaveProjectsAsync();
        StatusText = $"Removed {project.DisplayName} from Builder.";
    }

    public async Task InitializeSelectedProjectAsync()
    {
        var project = SelectedProject;
        if (!ValidateSelectedProject(project, requireExistingFolder: false))
        {
            return;
        }

        project!.Touch();
        await SaveProjectsAsync();
        backgroundProcesses.Enqueue(new BackgroundProcessRequest(
            $"Initialize {project.DisplayName}",
            "sunder-package-builder",
            BackgroundProcessIndicator.Packages,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup,
            CanCancel: true,
            async context =>
            {
                context.ReportIndeterminate("Creating Sunder package project...");
                await InitializeProjectInFolderAsync(project, context);

                project.DevPackageFolder = ResolveDefaultDevPackageFolder(project.ProjectFolder);
                project.Touch();
                await SaveProjectsAsync(context.CancellationToken);
                UpdateSelectedProjectInitialized();
                await RefreshSelectedStatusAsync(updateStatusText: false);
                context.ReportProgress(100, "Sunder package project initialized.");
                StatusText = $"Initialized {project.DisplayName}.";
            }));
        StatusText = "Package initialization queued.";
    }

    public async Task BuildSelectedProjectAsync()
    {
        var project = SelectedProject;
        if (!ValidateSelectedProject(project, requireExistingFolder: true))
        {
            return;
        }

        backgroundProcesses.Enqueue(new BackgroundProcessRequest(
            $"Build {project!.DisplayName}",
            "sunder-package-builder",
            BackgroundProcessIndicator.Packages,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup,
            CanCancel: true,
            async context =>
            {
                context.ReportIndeterminate("Running dotnet build...");
                var result = await BuilderDotnetTool.RunAsync(["build", project.ProjectFolder], project.ProjectFolder, context.CancellationToken);
                if (result.ExitCode != 0)
                {
                    RuntimeLogText = string.IsNullOrWhiteSpace(result.CombinedOutput) ? "dotnet build failed." : result.CombinedOutput;
                    StatusText = "Build failed. See runtime log.";
                    context.ReportProgress(100, "Build failed. See runtime log.");
                    throw new InvalidOperationException("dotnet build failed. See Builder runtime log for details.");
                }

                RuntimeLogText = string.Empty;
                project.DevPackageFolder = ResolveDefaultDevPackageFolder(project.ProjectFolder);
                project.Touch();
                await SaveProjectsAsync(context.CancellationToken);
                UpdateSelectedProjectInitialized();
                await RefreshSelectedStatusAsync(updateStatusText: false);
                context.ReportProgress(100, "Sunder package build completed.");
                StatusText = $"Build completed. Dev output: {project.DevPackageFolder}";
            }));
        StatusText = "Build queued.";
    }

    public async Task LoadSelectedProjectAsync()
    {
        var project = SelectedProject;
        if (!ValidateSelectedProject(project, requireExistingFolder: true))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(project!.DevPackageFolder))
        {
            project.DevPackageFolder = ResolveDefaultDevPackageFolder(project.ProjectFolder);
        }

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
        SelectedProject.DevPackageFolder = ResolveDefaultDevPackageFolder(folder);
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
                Projects.Add(new BuilderProjectViewModel(project));
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

    private async Task<IReadOnlyList<BuilderPrerequisiteStatus>> CheckAndApplySetupAsync(CancellationToken cancellationToken = default)
    {
        var statuses = await setupService.CheckAsync(cancellationToken);
        var isSetupComplete = statuses.All(status => status.IsInstalled);
        await RunOnUiThreadAsync(() =>
        {
            SetupItems.Clear();
            foreach (var status in statuses)
            {
                SetupItems.Add(new BuilderPrerequisiteViewModel(status));
            }

            IsSetupComplete = isSetupComplete;
            StatusText = isSetupComplete
                ? "Package builder setup is ready."
                : "Install the missing prerequisites to enable package project management.";
            NotifySetupStatePropertiesChanged();
        });

        if (isSetupComplete)
        {
            await LoadProjectsAsync(cancellationToken);
        }

        return statuses;
    }

    private async Task InstallMissingPrerequisitesCoreAsync(BackgroundProcessContext context)
    {
        context.ReportIndeterminate("Checking package builder prerequisites...");
        var statuses = await CheckAndApplySetupAsync(context.CancellationToken);
        if (statuses.All(status => status.IsInstalled))
        {
            context.ReportProgress(100, "Package builder setup is already ready.");
            return;
        }

        if (IsMissing(statuses, BuilderPrerequisiteKind.DotnetSdk))
        {
            context.ReportIndeterminate("Downloading .NET SDK installer...");
            var message = await setupService.InstallDotnetSdkAsync(context.CancellationToken);
            StatusText = message;
            context.ReportIndeterminate(message);
            statuses = await CheckAndApplySetupAsync(context.CancellationToken);
        }

        if (IsMissing(statuses, BuilderPrerequisiteKind.DotnetSdk))
        {
            context.ReportProgress(100, "Complete the .NET SDK installer, then recheck setup.");
            return;
        }

        if (IsMissing(statuses, BuilderPrerequisiteKind.SunderTemplate))
        {
            context.ReportIndeterminate("Installing Sunder package template...");
            var message = await setupService.InstallTemplateAsync(context.CancellationToken);
            StatusText = message;
            context.ReportIndeterminate(message);
            statuses = await CheckAndApplySetupAsync(context.CancellationToken);
        }

        context.ReportProgress(
            100,
            statuses.All(status => status.IsInstalled)
                ? "Package builder setup is ready."
                : "Some prerequisites are still missing. Recheck setup after completing external installers.");
    }

    private static bool IsMissing(IReadOnlyList<BuilderPrerequisiteStatus> statuses, BuilderPrerequisiteKind kind)
        => statuses.Any(status => status.Kind == kind && !status.IsInstalled);

    private void OnSelectedProjectPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, SelectedProject))
        {
            return;
        }

        if (e.PropertyName is nameof(BuilderProjectViewModel.ProjectFolder) or nameof(BuilderProjectViewModel.PackageId) or nameof(BuilderProjectViewModel.DevPackageFolder))
        {
            var version = ++_selectedProjectStatusVersion;
            UpdateSelectedProjectInitialized();
            IsSelectedProjectLoaded = false;
            if (!string.IsNullOrWhiteSpace(SelectedProject?.PackageId))
            {
                _ = RefreshSelectedStatusAsync(version, updateStatusText: false);
            }
        }

        if (IsSelectedProjectInitialized
            && e.PropertyName is nameof(BuilderProjectViewModel.DevPackageFolder)
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
        if (string.IsNullOrWhiteSpace(project.DevPackageFolder) && !string.IsNullOrWhiteSpace(project.ProjectFolder))
        {
            project.DevPackageFolder = ResolveDefaultDevPackageFolder(project.ProjectFolder);
        }

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

    private bool ValidateSelectedProject(BuilderProjectViewModel? project, bool requireExistingFolder)
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

        if (string.IsNullOrWhiteSpace(project.ProjectFolder))
        {
            StatusText = "Project folder is required.";
            return false;
        }

        project.ProjectFolder = Path.GetFullPath(project.ProjectFolder);
        if (string.IsNullOrWhiteSpace(project.DevPackageFolder))
        {
            project.DevPackageFolder = ResolveDefaultDevPackageFolder(project.ProjectFolder);
        }
        else
        {
            project.DevPackageFolder = Path.GetFullPath(project.DevPackageFolder);
        }

        if (requireExistingFolder && !Directory.Exists(project.ProjectFolder))
        {
            StatusText = "Project folder does not exist.";
            return false;
        }

        return true;
    }

    private async Task InitializeProjectInFolderAsync(BuilderProjectViewModel project, BackgroundProcessContext context)
    {
        Directory.CreateDirectory(project.ProjectFolder);
        EnsureProjectFolderCanBeInitialized(project.ProjectFolder);

        var result = await BuilderDotnetTool.RunAsync(
            BuildTemplateArguments(project, project.ProjectFolder, createInPlace: true),
            project.ProjectFolder,
            context.CancellationToken);
        if (result.ExitCode == 0)
        {
            return;
        }

        if (!ShouldFallbackToStaging(result))
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.CombinedOutput) ? "Package initialization failed." : result.CombinedOutput);
        }

        context.ReportIndeterminate("Installed template does not support --createInPlace; staging generated files...");
        await InitializeProjectViaStagingAsync(project, context);
    }

    private static async Task InitializeProjectViaStagingAsync(BuilderProjectViewModel project, BackgroundProcessContext context)
    {
        var stagingRoot = Path.Combine(Path.GetTempPath(), "sunder-builder", "template-staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);
        try
        {
            var result = await BuilderDotnetTool.RunAsync(
                BuildTemplateArguments(project, stagingRoot, createInPlace: false),
                stagingRoot,
                context.CancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.CombinedOutput) ? "Package initialization failed." : result.CombinedOutput);
            }

            var generatedProjectFolder = ResolveGeneratedProjectFolder(stagingRoot, ToProjectName(project.DisplayName));
            MoveGeneratedProjectContents(stagingRoot, generatedProjectFolder, project.ProjectFolder);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    private static string[] BuildTemplateArguments(BuilderProjectViewModel project, string outputFolder, bool createInPlace)
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
        OnPropertyChanged(nameof(CanInstallMissingPrerequisites));
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

    private static string ResolveDefaultDevPackageFolder(string projectFolder)
        => Path.Combine(Path.GetFullPath(projectFolder), "bin", "Debug", "net10.0", "sunder-dev");

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

public sealed class BuilderProjectViewModel(BuilderProjectRecord record) : INotifyPropertyChanged
{
    private string _displayName = record.DisplayName;
    private string _packageId = record.PackageId;
    private string _projectFolder = record.ProjectFolder;
    private string _devPackageFolder = record.DevPackageFolder;
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
            ProjectFolder.Trim(),
            DevPackageFolder.Trim(),
            Watch,
            CreatedAtUtc,
            UpdatedAtUtc)
        {
            AutoLoadOnStartup = AutoLoadOnStartup,
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
