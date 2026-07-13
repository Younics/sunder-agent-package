using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentWorkspacesViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SuccessStatusDisplayDuration = TimeSpan.FromSeconds(3);

    private readonly IAgentWorkspaceGateway _workspaceService;
    private readonly IAgentExecutionGateway _executionGateway;
    private readonly IPackageExtensionCatalog _extensionCatalog;
    private readonly IPackageExtensionCatalogMonitor? _extensionCatalogMonitor;
    private readonly IPackageExtensionCatalogChangeNotifier? _extensionCatalogChangeNotifier;
    private readonly IPackageSettingsNavigationService? _settingsNavigationService;
    private readonly IAgentRuntimeAvailability? _runtimeAvailability;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly IPresentationDispatcher _uiDispatcher;
    private readonly PresentationTaskScope _tasks;
    private readonly TimedStatusController _statusClear;
    private readonly OperationState<AgentWorkspaceOperation> _operation = new();
    private bool _suppressSelectionHandlers;
    private bool _suppressWorkspaceChangeNotifications;
    private bool _disposed;

    public AgentWorkspacesViewModel(
        IAgentWorkspaceGateway workspaceService,
        IAgentExecutionGateway executionGateway,
        IPackageExtensionCatalog extensionCatalog,
        IPackageSettingsNavigationService? settingsNavigationService = null)
    {
        _workspaceService = workspaceService;
        _executionGateway = executionGateway;
        _extensionCatalog = extensionCatalog;
        _settingsNavigationService = settingsNavigationService;
        _uiDispatcher = PresentationDispatcher.Capture();
        _tasks = new PresentationTaskScope(exception => ReportPresentationFailure(exception));
        _statusClear = new TimedStatusController(dispatcher: _uiDispatcher);
        _operation.PropertyChanged += OnOperationPropertyChanged;
        _runtimeAvailability = workspaceService as IAgentRuntimeAvailability;
        if (_runtimeAvailability is not null)
        {
            _runtimeAvailability.ConnectionStateChanged += OnRuntimeConnectionStateChanged;
        }
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

        try
        {
            ReloadTargets();
            ReloadWorkspaces(selectWorkspaceId: null);
        }
        catch (Exception ex)
        {
            SetStatus($"Agent Runtime is unavailable: {ex.Message}", AgentWorkspaceStatusKind.Warning);
        }
    }

    public AgentWorkspacesViewModel(
        AgentWorkspaceService workspaceService,
        AgentExecutionTargetService executionTargetService,
        IPackageExtensionCatalog extensionCatalog,
        AgentExecutionTargetWarmupService warmupService,
        IPackageSettingsNavigationService? settingsNavigationService = null)
        : this(workspaceService, warmupService, extensionCatalog, settingsNavigationService)
    {
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

    public bool IsBusy => _operation.IsBusy;

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
        _lifetimeCancellation.Cancel();
        if (_runtimeAvailability is not null)
        {
            _runtimeAvailability.ConnectionStateChanged -= OnRuntimeConnectionStateChanged;
        }
        _statusClear.Dispose();
        _tasks.Dispose();
        _operation.PropertyChanged -= OnOperationPropertyChanged;
        _operation.Dispose();
        _workspaceService.WorkspacesChanged -= OnWorkspacesChanged;
        if (_extensionCatalogMonitor is not null)
        {
            _extensionCatalogMonitor.Changed -= OnExtensionCatalogChanged;
        }

        if (_extensionCatalogChangeNotifier is not null)
        {
            _extensionCatalogChangeNotifier.ExtensionsChanged -= OnExtensionCatalogChanged;
        }
        _lifetimeCancellation.Dispose();
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

        TrackOperation(RefreshEditorSectionsAsync());
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

    private bool CanSaveWorkspace() => SelectedWorkspace is not null && !IsBusy;

    private bool CanDeleteWorkspace() => SelectedWorkspace is not null && !IsBusy;

    [RelayCommand]
    private void BackToWorkspaceList()
    {
        if (IsCompactLayout)
        {
            SelectedWorkspace = null;
        }

        IsEditorActive = false;
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

    private void OnWorkspacesChanged()
        => RunOnUiThread(() =>
        {
            if (!_disposed && !_suppressWorkspaceChangeNotifications)
            {
                ReloadWorkspaces(SelectedWorkspace?.WorkspaceId);
            }
        });

    private void OnRuntimeConnectionStateChanged(AgentRuntimeConnectionState state)
        => RunOnUiThread(() =>
        {
            if (_disposed)
            {
                return;
            }
            if (state == AgentRuntimeConnectionState.Connected)
            {
                ReloadTargets(SelectedExecutionTarget?.TargetId);
                ReloadWorkspaces(SelectedWorkspace?.WorkspaceId);
                ClearStatus();
            }
            else if (state is AgentRuntimeConnectionState.Unavailable or AgentRuntimeConnectionState.Reconnecting)
            {
                SetStatus("Agent Runtime is unavailable. Reconnecting...", AgentWorkspaceStatusKind.Warning);
            }
        });

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
        TrackOperation(RefreshEditorSectionsAsync());
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

    private void RunOnUiThread(Action action)
    {
        _tasks.Run(_uiDispatcher.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                action();
            }
        }));
    }

    private void ClearStatus()
        => SetStatus(string.Empty, AgentWorkspaceStatusKind.None);

    internal void ReportPresentationFailure(Exception exception)
        => SetStatus(exception.Message, AgentWorkspaceStatusKind.Error);

    private void SetStatus(string message, AgentWorkspaceStatusKind kind, bool autoClear = false)
    {
        _statusClear.Cancel();
        StatusKind = string.IsNullOrWhiteSpace(message) ? AgentWorkspaceStatusKind.None : kind;
        StatusText = message;
        if (autoClear && StatusKind == AgentWorkspaceStatusKind.Success)
        {
            _tasks.Run(_statusClear.ScheduleAsync(SuccessStatusDisplayDuration, () =>
            {
                if (StatusKind == AgentWorkspaceStatusKind.Success
                    && string.Equals(StatusText, message, StringComparison.Ordinal))
                {
                    ClearStatus();
                }
            }));
        }
    }

    private void TrackOperation(Task operation) => _tasks.Run(operation);

    private OperationGeneration BeginOperation(AgentWorkspaceOperation operation)
    {
        var generation = _operation.Begin(operation, canCancel: false);
        OnOperationPropertyChanged(this, new PropertyChangedEventArgs(nameof(OperationState.IsBusy)));
        return generation;
    }

    private void EndOperation(OperationGeneration generation)
        => _operation.TryComplete(generation);

    private void OnOperationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsBusy));
        SaveWorkspaceCommand.NotifyCanExecuteChanged();
        DeleteWorkspaceCommand.NotifyCanExecuteChanged();
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

internal enum AgentWorkspaceOperation
{
    Save,
    DiscoverEditor,
}
