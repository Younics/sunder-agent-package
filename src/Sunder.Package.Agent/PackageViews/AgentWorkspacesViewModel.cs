using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentWorkspacesViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SuccessStatusDisplayDuration = TimeSpan.FromSeconds(3);

    private readonly AgentWorkspaceService _workspaceService;
    private readonly AgentExecutionTargetService _targetService;
    private readonly IPackageExtensionCatalog _extensionCatalog;
    private readonly IPackageExtensionCatalogMonitor? _extensionCatalogMonitor;
    private readonly IPackageExtensionCatalogChangeNotifier? _extensionCatalogChangeNotifier;
    private readonly AgentExecutionTargetWarmupService? _warmupService;
    private readonly IPackageSettingsNavigationService? _settingsNavigationService;
    private CancellationTokenSource? _successStatusClearCancellation;
    private bool _suppressSelectionHandlers;
    private bool _suppressWorkspaceChangeNotifications;
    private bool _disposed;

    public AgentWorkspacesViewModel(
        AgentWorkspaceService workspaceService,
        AgentExecutionTargetService targetService,
        IPackageExtensionCatalog extensionCatalog,
        AgentExecutionTargetWarmupService? warmupService = null,
        IPackageSettingsNavigationService? settingsNavigationService = null)
    {
        _workspaceService = workspaceService;
        _targetService = targetService;
        _extensionCatalog = extensionCatalog;
        _settingsNavigationService = settingsNavigationService;
        _workspaceService.WorkspacesChanged += OnWorkspacesChanged;
        _extensionCatalogMonitor = extensionCatalog as IPackageExtensionCatalogMonitor;
        if (_extensionCatalogMonitor is not null)
        {
            _extensionCatalogMonitor.Changed += OnExtensionCatalogChanged;
        }
        else if (extensionCatalog is IPackageExtensionCatalogChangeNotifier changeNotifier)
        {
            _extensionCatalogChangeNotifier = changeNotifier;
            changeNotifier.ExtensionsChanged += OnExtensionCatalogChanged;
        }

        _warmupService = warmupService;
        ReloadTargets();
        ReloadWorkspaces(selectWorkspaceId: null);
    }

    public ObservableCollection<AgentWorkspaceRecord> Workspaces { get; } = [];

    public ObservableCollection<ExecutionTargetOption> ExecutionTargets { get; } = [];

    public ObservableCollection<AgentWorkspacePathItemViewModel> WorkspacePaths { get; } = [];

    public ObservableCollection<AgentWorkspaceDocumentItemViewModel> WorkspaceDocuments { get; } = [];

    public bool HasExecutionTargetChoices => ExecutionTargets.Any(target => !target.IsUnconfigured);

    public bool HasNoExecutionTargetChoices => !HasExecutionTargetChoices;

    public bool HasSelectedWorkspace => SelectedWorkspace is not null;

    public bool HasWorkspacePaths => WorkspacePaths.Count > 0;

    public bool HasWorkspaceDocuments => WorkspaceDocuments.Count > 0;

    public bool IsListActive => !IsEditorActive;

    public bool ShowWideLayout => !IsCompactLayout;

    public bool ShowCompactList => IsCompactLayout && IsListActive;

    public bool ShowCompactEditor => IsCompactLayout && IsEditorActive;

    public bool ShowListPane => ShowWideLayout || ShowCompactList;

    public bool ShowEditorPane => ShowWideLayout || ShowCompactEditor;

    [ObservableProperty]
    private AgentWorkspaceRecord? _selectedWorkspace;

    [ObservableProperty]
    private bool _isCompactLayout;

    [ObservableProperty]
    private bool _isEditorActive;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private ExecutionTargetOption? _selectedExecutionTarget;

    [ObservableProperty]
    private AgentWorkspacePathItemViewModel? _selectedWorkspacePath;

    [ObservableProperty]
    private AgentWorkspaceDocumentItemViewModel? _selectedWorkspaceDocument;

    public ObservableCollection<AgentEditorSectionViewModel> EditorSections { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusText))]
    private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusSuccess))]
    [NotifyPropertyChangedFor(nameof(IsStatusWarning))]
    [NotifyPropertyChangedFor(nameof(IsStatusError))]
    private AgentWorkspaceStatusKind _statusKind = AgentWorkspaceStatusKind.None;

    public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

    public bool IsStatusSuccess => StatusKind == AgentWorkspaceStatusKind.Success;

    public bool IsStatusWarning => StatusKind == AgentWorkspaceStatusKind.Warning;

    public bool IsStatusError => StatusKind == AgentWorkspaceStatusKind.Error;

    partial void OnSelectedWorkspaceChanged(AgentWorkspaceRecord? value)
    {
        DeleteWorkspaceCommand.NotifyCanExecuteChanged();
        SaveWorkspaceCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasSelectedWorkspace));

        if (_suppressSelectionHandlers)
        {
            return;
        }

        LoadWorkspace(value);
        if (IsCompactLayout && value is not null)
        {
            IsEditorActive = true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelSuccessStatusClear();
        _workspaceService.WorkspacesChanged -= OnWorkspacesChanged;
        if (_extensionCatalogMonitor is not null)
        {
            _extensionCatalogMonitor.Changed -= OnExtensionCatalogChanged;
        }

        if (_extensionCatalogChangeNotifier is not null)
        {
            _extensionCatalogChangeNotifier.ExtensionsChanged -= OnExtensionCatalogChanged;
        }
    }

    partial void OnIsCompactLayoutChanged(bool value)
    {
        if (value && !IsEditorActive)
        {
            SelectedWorkspace = null;
        }
        else if (!value && SelectedWorkspace is null)
        {
            SelectedWorkspace = Workspaces.FirstOrDefault();
        }

        OnPropertyChanged(nameof(ShowWideLayout));
        OnPropertyChanged(nameof(ShowCompactList));
        OnPropertyChanged(nameof(ShowCompactEditor));
        OnPropertyChanged(nameof(ShowListPane));
        OnPropertyChanged(nameof(ShowEditorPane));
    }

    partial void OnIsEditorActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(IsListActive));
        OnPropertyChanged(nameof(ShowCompactList));
        OnPropertyChanged(nameof(ShowCompactEditor));
        OnPropertyChanged(nameof(ShowListPane));
        OnPropertyChanged(nameof(ShowEditorPane));
    }

    partial void OnSelectedExecutionTargetChanged(ExecutionTargetOption? value)
    {
        if (_suppressSelectionHandlers)
        {
            return;
        }

        _ = RefreshEditorSectionsAsync();
    }

    partial void OnSelectedWorkspacePathChanged(AgentWorkspacePathItemViewModel? value)
    {
        SetSelectedWorkspacePathAsDefaultCommand.NotifyCanExecuteChanged();
        DeleteSelectedWorkspacePathCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedWorkspaceDocumentChanged(AgentWorkspaceDocumentItemViewModel? value)
        => DeleteSelectedWorkspaceDocumentCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void CreateWorkspace()
    {
        try
        {
            AgentWorkspaceRecord workspace;
            _suppressWorkspaceChangeNotifications = true;
            try
            {
                workspace = _workspaceService.CreateWorkspace("New Workspace");
            }
            finally
            {
                _suppressWorkspaceChangeNotifications = false;
            }

            ReloadWorkspaces(workspace.WorkspaceId);
            IsEditorActive = true;
            ClearStatus();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, AgentWorkspaceStatusKind.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveWorkspace))]
    private async Task SaveWorkspace()
    {
        if (SelectedWorkspace is null)
        {
            return;
        }

        try
        {
            var savedWorkspaceId = SelectedWorkspace.WorkspaceId;
            if (!ValidateWorkspacePathsAndDocuments(out var validationMessage))
            {
                SetStatus(validationMessage, AgentWorkspaceStatusKind.Error);
                return;
            }

            var editorSaveResult = await SaveEditorSectionsAsync();
            if (!editorSaveResult.Success)
            {
                SetStatus(editorSaveResult.Message, AgentWorkspaceStatusKind.Error);
                return;
            }

            AgentExecutionTargetWarmupResult? warmupResult = null;
            _suppressWorkspaceChangeNotifications = true;
            try
            {
                _workspaceService.SaveWorkspace(SelectedWorkspace.WorkspaceId, DisplayName, Description);
                _workspaceService.SaveWorkspacePaths(
                    SelectedWorkspace.WorkspaceId,
                    WorkspacePaths.Select((path, index) => path.ToRecord(SelectedWorkspace.WorkspaceId, index)).ToArray());
                _workspaceService.SaveWorkspaceDocuments(
                    SelectedWorkspace.WorkspaceId,
                    WorkspaceDocuments.Select((document, index) => document.ToRecord(SelectedWorkspace.WorkspaceId, index)).ToArray());
                if (SelectedExecutionTarget is null || SelectedExecutionTarget.IsUnconfigured)
                {
                    _workspaceService.RemovePrimaryExecutionBinding(SelectedWorkspace.WorkspaceId);
                }
                else
                {
                    _workspaceService.SavePrimaryExecutionBinding(SelectedWorkspace.WorkspaceId, SelectedExecutionTarget.TargetId!);
                    if (_warmupService is not null)
                    {
                        var warmupWorkspace = _workspaceService.GetWorkspace(savedWorkspaceId) ?? SelectedWorkspace;
                        SetStatus("Workspace saved. Preparing execution target...", AgentWorkspaceStatusKind.Warning);
                        warmupResult = await _warmupService.WarmWorkspaceAsync(warmupWorkspace);
                    }
                }
            }
            finally
            {
                _suppressWorkspaceChangeNotifications = false;
            }

            var shouldClearSelection = IsCompactLayout;
            ReloadWorkspaceList(savedWorkspaceId);
            if (shouldClearSelection)
            {
                SelectedWorkspace = null;
                ClearStatus();
            }
            else
            {
                var statusText = warmupResult?.Status == AgentExecutionTargetWarmupStatus.Failed
                    ? $"Workspace saved, but execution target is not ready: {warmupResult.Message}"
                    : warmupResult?.Status == AgentExecutionTargetWarmupStatus.Ready
                        ? "Workspace saved. Execution target is ready."
                        : "Workspace saved.";
                var statusKind = warmupResult?.Status == AgentExecutionTargetWarmupStatus.Failed
                    ? AgentWorkspaceStatusKind.Warning
                    : AgentWorkspaceStatusKind.Success;

                SetStatus(statusText, statusKind, autoClear: statusKind == AgentWorkspaceStatusKind.Success);
            }

            IsEditorActive = false;
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, AgentWorkspaceStatusKind.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteWorkspace))]
    private void DeleteWorkspace()
    {
        if (SelectedWorkspace is null)
        {
            return;
        }

        try
        {
            var shouldClearSelection = IsCompactLayout;
            _suppressWorkspaceChangeNotifications = true;
            try
            {
                _workspaceService.DeleteWorkspace(SelectedWorkspace.WorkspaceId);
            }
            finally
            {
                _suppressWorkspaceChangeNotifications = false;
            }

            if (shouldClearSelection)
            {
                ReloadWorkspaceList(selectWorkspaceId: null);
                SelectedWorkspace = null;
            }
            else
            {
                ReloadWorkspaces(selectWorkspaceId: null);
            }

            IsEditorActive = false;
            if (shouldClearSelection)
            {
                ClearStatus();
            }
            else
            {
                SetStatus("Workspace deleted.", AgentWorkspaceStatusKind.Success, autoClear: true);
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, AgentWorkspaceStatusKind.Error);
        }
    }

    private bool CanSaveWorkspace() => SelectedWorkspace is not null;

    private bool CanDeleteWorkspace() => SelectedWorkspace is not null;

    public void AddWorkspacePath(string hostPath)
    {
        if (SelectedWorkspace is null || string.IsNullOrWhiteSpace(hostPath))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var fullPath = AgentWorkspacePathFormatter.GetFullPath(hostPath);
        if (WorkspacePaths.Any(path => string.Equals(AgentWorkspacePathFormatter.GetFullPath(path.HostPath), fullPath, GetPathStringComparison())))
        {
            return;
        }

        var item = new AgentWorkspacePathItemViewModel(new AgentWorkspacePathRecord(
            Guid.NewGuid().ToString("N"),
            SelectedWorkspace.WorkspaceId,
            fullPath,
            WorkspacePaths.Count == 0,
            WorkspacePaths.Count,
            now,
            now));
        WorkspacePaths.Add(item);
        SelectedWorkspacePath = item;
        NotifyWorkspacePathCollectionChanged();
    }

    public void AddWorkspaceDocument(string filePath)
    {
        if (SelectedWorkspace is null || string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var fullPath = AgentWorkspacePathFormatter.GetFullPath(filePath);
        if (WorkspaceDocuments.Any(document => string.Equals(AgentWorkspacePathFormatter.GetFullPath(document.FilePath), fullPath, GetPathStringComparison())))
        {
            return;
        }

        var item = new AgentWorkspaceDocumentItemViewModel(new AgentWorkspaceDocumentRecord(
            Guid.NewGuid().ToString("N"),
            SelectedWorkspace.WorkspaceId,
            fullPath,
            WorkspaceDocuments.Count,
            now,
            now));
        WorkspaceDocuments.Add(item);
        SelectedWorkspaceDocument = item;
        NotifyWorkspaceDocumentCollectionChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSetSelectedWorkspacePathAsDefault))]
    private void SetSelectedWorkspacePathAsDefault()
    {
        SetWorkspacePathAsDefault(SelectedWorkspacePath);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedWorkspacePath))]
    private void DeleteSelectedWorkspacePath()
    {
        DeleteWorkspacePath(SelectedWorkspacePath);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedWorkspaceDocument))]
    private void DeleteSelectedWorkspaceDocument()
    {
        DeleteWorkspaceDocument(SelectedWorkspaceDocument);
    }

    [RelayCommand]
    private void BeginEditWorkspacePath(AgentWorkspacePathItemViewModel? path)
    {
        if (path is null)
        {
            return;
        }

        foreach (var item in WorkspacePaths)
        {
            if (!ReferenceEquals(item, path) && item.IsEditActive)
            {
                item.CancelEdit();
            }
        }

        path.BeginEdit();
    }

    [RelayCommand]
    private static void SaveWorkspacePathEdit(AgentWorkspacePathItemViewModel? path)
        => path?.SaveEdit();

    [RelayCommand]
    private static void CancelWorkspacePathEdit(AgentWorkspacePathItemViewModel? path)
        => path?.CancelEdit();

    [RelayCommand]
    private void SetWorkspacePathAsDefault(AgentWorkspacePathItemViewModel? path)
    {
        if (path is null)
        {
            return;
        }

        foreach (var item in WorkspacePaths)
        {
            item.IsDefault = ReferenceEquals(item, path);
        }

        SelectedWorkspacePath = path;
        SetSelectedWorkspacePathAsDefaultCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void DeleteWorkspacePath(AgentWorkspacePathItemViewModel? path)
    {
        if (path is null)
        {
            return;
        }

        var wasDefault = path.IsDefault;
        WorkspacePaths.Remove(path);
        if (wasDefault && WorkspacePaths.Count > 0)
        {
            WorkspacePaths[0].IsDefault = true;
        }

        if (ReferenceEquals(SelectedWorkspacePath, path))
        {
            SelectedWorkspacePath = WorkspacePaths.FirstOrDefault(item => item.IsDefault) ?? WorkspacePaths.FirstOrDefault();
        }

        NotifyWorkspacePathCollectionChanged();
    }

    [RelayCommand]
    private void BeginEditWorkspaceDocument(AgentWorkspaceDocumentItemViewModel? document)
    {
        if (document is null)
        {
            return;
        }

        foreach (var item in WorkspaceDocuments)
        {
            if (!ReferenceEquals(item, document) && item.IsEditActive)
            {
                item.CancelEdit();
            }
        }

        document.BeginEdit();
    }

    [RelayCommand]
    private static void SaveWorkspaceDocumentEdit(AgentWorkspaceDocumentItemViewModel? document)
        => document?.SaveEdit();

    [RelayCommand]
    private static void CancelWorkspaceDocumentEdit(AgentWorkspaceDocumentItemViewModel? document)
        => document?.CancelEdit();

    [RelayCommand]
    private void DeleteWorkspaceDocument(AgentWorkspaceDocumentItemViewModel? document)
    {
        if (document is null)
        {
            return;
        }

        WorkspaceDocuments.Remove(document);
        if (ReferenceEquals(SelectedWorkspaceDocument, document))
        {
            SelectedWorkspaceDocument = WorkspaceDocuments.FirstOrDefault();
        }

        NotifyWorkspaceDocumentCollectionChanged();
    }

    private bool CanSetSelectedWorkspacePathAsDefault() => SelectedWorkspacePath is not null;

    private bool CanDeleteSelectedWorkspacePath() => SelectedWorkspacePath is not null;

    private bool CanDeleteSelectedWorkspaceDocument() => SelectedWorkspaceDocument is not null;

    [RelayCommand]
    private void BackToWorkspaceList()
    {
        if (IsCompactLayout)
        {
            SelectedWorkspace = null;
        }

        IsEditorActive = false;
    }

    public async Task ExecuteEditorActionAsync(AgentEditorActionViewModel action)
    {
        try
        {
            switch (action.Kind)
            {
                case AgentEditorActionKind.OpenPackageSettings:
                    if (_settingsNavigationService is null || string.IsNullOrWhiteSpace(action.PackageId))
                    {
                        SetStatus("Package settings cannot be opened from this host.", AgentWorkspaceStatusKind.Warning);
                        return;
                    }

                    if (await _settingsNavigationService.OpenPackageSettingsAsync(action.PackageId, action.Parameters))
                    {
                        SetStatus("Opened package settings.", AgentWorkspaceStatusKind.Success, autoClear: true);
                    }
                    else
                    {
                        SetStatus("Package settings could not be opened.", AgentWorkspaceStatusKind.Warning);
                    }

                    break;
                case AgentEditorActionKind.RefreshEditor:
                    await RefreshEditorSectionsAsync();
                    SetStatus("Workspace editor refreshed.", AgentWorkspaceStatusKind.Success, autoClear: true);
                    break;
                case AgentEditorActionKind.RefreshField:
                    await RefreshEditorFieldAsync(action.Field);
                    SetStatus("Workspace editor field refreshed.", AgentWorkspaceStatusKind.Success, autoClear: true);
                    break;
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, AgentWorkspaceStatusKind.Error);
        }
    }

    [RelayCommand]
    private void OpenWorkspaceEditor(AgentWorkspaceRecord workspace)
    {
        ActivateWorkspace(workspace);
        IsEditorActive = true;
    }

    public void ActivateWorkspace(AgentWorkspaceRecord workspace)
    {
        if (!string.Equals(SelectedWorkspace?.WorkspaceId, workspace.WorkspaceId, StringComparison.OrdinalIgnoreCase))
        {
            SelectedWorkspace = workspace;
        }

        if (IsCompactLayout)
        {
            IsEditorActive = true;
        }
    }

    private void ReloadTargets(string? preferredTargetId = null)
    {
        ExecutionTargets.Clear();
        ExecutionTargets.Add(ExecutionTargetOption.Unconfigured);
        foreach (var target in _targetService.ListTargets())
        {
            ExecutionTargets.Add(new ExecutionTargetOption(
                target.TargetId,
                target.DisplayName,
                target.Description ?? target.TargetId));
        }

        if (preferredTargetId is not null)
        {
            SetSelectionSilently(() => SelectedExecutionTarget = ResolveTargetOption(preferredTargetId));
        }

        OnPropertyChanged(nameof(HasExecutionTargetChoices));
        OnPropertyChanged(nameof(HasNoExecutionTargetChoices));
    }

    private void OnExtensionCatalogChanged(object? sender, EventArgs e)
        => RunOnUiThread(ApplyExtensionCatalogChanges);

    private void OnExtensionCatalogChanged(object? sender, PackageExtensionCatalogChangedEventArgs e)
    {
        if (!e.IncludesExtensionPoint(PackageExtensionPoints.ExecutionTargets.Id)
            && !e.IncludesExtensionPoint(PackageExtensionPoints.WorkspaceEditorContributors.Id))
        {
            return;
        }

        RunOnUiThread(ApplyExtensionCatalogChanges);
    }

    private void OnWorkspacesChanged()
        => RunOnUiThread(() =>
        {
            if (!_disposed && !_suppressWorkspaceChangeNotifications)
            {
                ReloadWorkspaces(SelectedWorkspace?.WorkspaceId);
            }
        });

    private void ApplyExtensionCatalogChanges()
    {
        if (_disposed)
        {
            return;
        }

        var preferredTargetId = SelectedExecutionTarget?.TargetId;
        if (string.IsNullOrWhiteSpace(preferredTargetId) && SelectedWorkspace is not null)
        {
            preferredTargetId = ResolveWorkspaceTargetId(SelectedWorkspace.WorkspaceId);
        }

        ReloadTargets(preferredTargetId);
        _ = RefreshEditorSectionsAsync();
    }

    private void ReloadWorkspaces(string? selectWorkspaceId)
    {
        ReloadWorkspaceList(selectWorkspaceId);
        LoadWorkspace(SelectedWorkspace);
    }

    private void ReloadWorkspaceList(string? selectWorkspaceId)
    {
        var workspaces = _workspaceService.ListWorkspaces();
        _suppressSelectionHandlers = true;
        try
        {
            Workspaces.Clear();
            foreach (var workspace in workspaces)
            {
                Workspaces.Add(workspace);
            }

            SelectedWorkspace = Workspaces.FirstOrDefault(workspace => string.Equals(workspace.WorkspaceId, selectWorkspaceId, StringComparison.OrdinalIgnoreCase))
                ?? Workspaces.FirstOrDefault();
        }
        finally
        {
            _suppressSelectionHandlers = false;
        }
    }

    private void LoadWorkspace(AgentWorkspaceRecord? workspace)
    {
        DisplayName = workspace?.DisplayName ?? string.Empty;
        Description = workspace?.Description ?? string.Empty;
        LoadWorkspacePaths(workspace);
        LoadWorkspaceDocuments(workspace);
        var binding = workspace is null
            ? null
            : _workspaceService.ListBindings(workspace.WorkspaceId)
                .FirstOrDefault(item => string.Equals(item.Role, AgentWorkspaceBindingRoles.PrimaryExecutionTarget, StringComparison.OrdinalIgnoreCase));
        SetSelectionSilently(() => SelectedExecutionTarget = ResolveTargetOption(binding?.ContributionId));
        _ = RefreshEditorSectionsAsync();
    }

    private void LoadWorkspacePaths(AgentWorkspaceRecord? workspace)
    {
        WorkspacePaths.Clear();
        if (workspace is not null)
        {
            foreach (var path in workspace.Paths.OrderBy(path => path.SortOrder))
            {
                WorkspacePaths.Add(new AgentWorkspacePathItemViewModel(path));
            }
        }

        SelectedWorkspacePath = WorkspacePaths.FirstOrDefault(path => path.IsDefault) ?? WorkspacePaths.FirstOrDefault();
        NotifyWorkspacePathCollectionChanged();
    }

    private void LoadWorkspaceDocuments(AgentWorkspaceRecord? workspace)
    {
        WorkspaceDocuments.Clear();
        if (workspace is not null)
        {
            foreach (var document in workspace.Documents.OrderBy(document => document.SortOrder))
            {
                WorkspaceDocuments.Add(new AgentWorkspaceDocumentItemViewModel(document));
            }
        }

        SelectedWorkspaceDocument = WorkspaceDocuments.FirstOrDefault();
        NotifyWorkspaceDocumentCollectionChanged();
    }

    private bool ValidateWorkspacePathsAndDocuments(out string message)
    {
        foreach (var path in WorkspacePaths)
        {
            if (string.IsNullOrWhiteSpace(path.HostPath))
            {
                message = "Workspace path cannot be empty.";
                return false;
            }

            var fullPath = AgentWorkspacePathFormatter.GetFullPath(path.HostPath);
            if (!Directory.Exists(fullPath))
            {
                message = $"Workspace path does not exist: {fullPath}";
                return false;
            }
        }

        foreach (var document in WorkspaceDocuments)
        {
            if (string.IsNullOrWhiteSpace(document.FilePath))
            {
                message = "Workspace document path cannot be empty.";
                return false;
            }

            var fullPath = AgentWorkspacePathFormatter.GetFullPath(document.FilePath);
            if (!File.Exists(fullPath))
            {
                message = $"Workspace document does not exist: {fullPath}";
                return false;
            }
        }

        message = string.Empty;
        return true;
    }

    private void NotifyWorkspacePathCollectionChanged()
    {
        OnPropertyChanged(nameof(HasWorkspacePaths));
        SetSelectedWorkspacePathAsDefaultCommand.NotifyCanExecuteChanged();
        DeleteSelectedWorkspacePathCommand.NotifyCanExecuteChanged();
    }

    private void NotifyWorkspaceDocumentCollectionChanged()
    {
        OnPropertyChanged(nameof(HasWorkspaceDocuments));
        DeleteSelectedWorkspaceDocumentCommand.NotifyCanExecuteChanged();
    }

    private string? ResolveWorkspaceTargetId(string workspaceId)
        => _workspaceService.ListBindings(workspaceId)
            .FirstOrDefault(item => string.Equals(item.Role, AgentWorkspaceBindingRoles.PrimaryExecutionTarget, StringComparison.OrdinalIgnoreCase))
            ?.ContributionId;

    private async Task RefreshEditorSectionsAsync()
    {
        EditorSections.Clear();
        var context = BuildEditorContext();
        if (context is null)
        {
            return;
        }

        try
        {
            var contributors = _extensionCatalog.GetExtensions(PackageExtensionPoints.WorkspaceEditorContributors)
                .Where(contributor => contributor.CanEdit(context))
                .ToArray();
            foreach (var contributor in contributors)
            {
                var sections = await contributor.GetSectionsAsync(context);
                foreach (var section in sections)
                {
                    EditorSections.Add(new AgentEditorSectionViewModel(contributor, context, section));
                }
            }

            ClearStatus();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, AgentWorkspaceStatusKind.Error);
        }
    }

    private async Task RefreshEditorFieldAsync(AgentEditorFieldViewModel field)
    {
        var context = BuildEditorContext();
        if (context is null || !field.Section.Contributor.CanEdit(context))
        {
            return;
        }

        var sections = await field.Section.Contributor.GetSectionsAsync(context);
        var refreshedSection = sections.FirstOrDefault(section => string.Equals(section.SectionId, field.Section.SectionId, StringComparison.OrdinalIgnoreCase));
        var refreshedField = refreshedSection?.Fields.FirstOrDefault(candidate => string.Equals(candidate.FieldId, field.FieldId, StringComparison.OrdinalIgnoreCase));
        if (refreshedField is null)
        {
            await RefreshEditorSectionsAsync();
            return;
        }

        field.ApplyField(refreshedField);
    }

    private AgentWorkspaceEditorContext? BuildEditorContext()
    {
        if (SelectedWorkspace is null || SelectedExecutionTarget is null || SelectedExecutionTarget.IsUnconfigured)
        {
            return null;
        }

        return new AgentWorkspaceEditorContext(
            SelectedWorkspace,
            SelectedExecutionTarget.TargetId!,
            AgentWorkspaceService.BuildPrimaryBindingId(SelectedWorkspace.WorkspaceId));
    }

    private async Task<AgentEditorSaveResult> SaveEditorSectionsAsync()
    {
        foreach (var section in EditorSections)
        {
            var result = await section.SaveAsync();
            if (!result.Success)
            {
                return result;
            }
        }

        return AgentEditorSaveResult.Ok("Workspace editor sections saved.");
    }

    private ExecutionTargetOption ResolveTargetOption(string? contributionId)
        => ExecutionTargets.FirstOrDefault(target => string.Equals(target.TargetId, contributionId, StringComparison.OrdinalIgnoreCase))
           ?? ExecutionTargetOption.Unconfigured;

    private static void RunOnUiThread(Action action)
    {
        if (Avalonia.Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
    }

    private void ClearStatus()
        => SetStatus(string.Empty, AgentWorkspaceStatusKind.None);

    private void SetStatus(string message, AgentWorkspaceStatusKind kind, bool autoClear = false)
    {
        CancelSuccessStatusClear();
        StatusKind = string.IsNullOrWhiteSpace(message) ? AgentWorkspaceStatusKind.None : kind;
        StatusText = message;
        if (autoClear && StatusKind == AgentWorkspaceStatusKind.Success)
        {
            ScheduleSuccessStatusClear(message);
        }
    }

    private void ScheduleSuccessStatusClear(string message)
    {
        var cancellation = new CancellationTokenSource();
        _successStatusClearCancellation = cancellation;
        _ = ClearSuccessStatusAfterDelayAsync(message, cancellation);
    }

    private async Task ClearSuccessStatusAfterDelayAsync(string message, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(SuccessStatusDisplayDuration, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            if (_successStatusClearCancellation == cancellation
                && StatusKind == AgentWorkspaceStatusKind.Success
                && string.Equals(StatusText, message, StringComparison.Ordinal))
            {
                ClearStatus();
            }
        });
    }

    private void CancelSuccessStatusClear()
    {
        var cancellation = _successStatusClearCancellation;
        if (cancellation is null)
        {
            return;
        }

        _successStatusClearCancellation = null;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void SetSelectionSilently(Action action)
    {
        _suppressSelectionHandlers = true;
        try
        {
            action();
        }
        finally
        {
            _suppressSelectionHandlers = false;
        }
    }

    private static StringComparison GetPathStringComparison()
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
