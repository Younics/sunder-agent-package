using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentWorkspacesViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SuccessStatusDisplayDuration = TimeSpan.FromSeconds(3);
    private const string ListRefreshChannel = "workspaces-list";
    private const string ExecutionTargetRefreshChannel = "execution-targets";

    private readonly IAgentWorkspaceGateway _workspaceService;
    private readonly IAgentExecutionGateway _executionGateway;
    private readonly IAgentDashboardLoader? _dashboardLoader;
    private readonly IAgentWorkspaceCommandGateway? _workspaceCommands;
    private readonly IServiceProviders<AgentServices.IWorkspaceEditorService> _editors;
    private readonly IAgentProfileGateway? _catalogNotifications;
    private int _editorObservationStarted;
    private readonly IPackageSettingsNavigationService? _settingsNavigationService;
    private readonly IAgentRuntimeAvailability? _runtimeAvailability;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly IPresentationDispatcher _uiDispatcher;
    private readonly PresentationTaskScope _tasks;
    private readonly TimedStatusController _statusClear;
    private readonly OperationState<AgentWorkspaceOperation> _operation = new();
    private readonly AsyncOnce _initialization = new();
    private readonly LatestRequestCoordinator _requests = new();
    private readonly KeyedAdaptiveListDetailState<string, AgentWorkspaceRecord> _listDetail;
    private readonly SerializedRefreshLoop _runtimeRefresh;
    private readonly SerializedRefreshLoop _extensionRefresh;
    private readonly Dictionary<string, AgentWorkspaceDraftState> _workspaceDrafts =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressDraftTracking;
    private bool _suppressWorkspaceRefresh;
    private bool _initializationRefreshPending;
    private bool _initializationCatalogRefreshPending;
    private bool _isInitialized;
    private bool _disposed;
    private string? _initializationFailureStatus;
    private Task _currentExecutionTargetRefresh = Task.CompletedTask;
    private Task _currentEditorSectionRefresh = Task.CompletedTask;
    private Task _currentEditorSectionRetry = Task.CompletedTask;
    private long _executionTargetSelectionRevision;
    private long _editorIntentRevision;
    private long _workspaceDraftRevision;

    public AgentWorkspacesViewModel(
        IAgentWorkspaceGateway workspaceService,
        IAgentExecutionGateway executionGateway,
        IServiceProviders<AgentServices.IWorkspaceEditorService> editors,
        IPackageSettingsNavigationService? settingsNavigationService = null)
        : this(
            workspaceService,
            executionGateway,
            editors,
            settingsNavigationService,
            PresentationDispatcher.Capture())
    {
    }

    internal AgentWorkspacesViewModel(
        IAgentWorkspaceGateway workspaceService,
        IAgentExecutionGateway executionGateway,
        IServiceProviders<AgentServices.IWorkspaceEditorService> editors,
        IPackageSettingsNavigationService? settingsNavigationService,
        IPresentationDispatcher uiDispatcher)
    {
        _workspaceService = workspaceService;
        _executionGateway = executionGateway;
        _dashboardLoader = workspaceService as IAgentDashboardLoader;
        _workspaceCommands = workspaceService as IAgentWorkspaceCommandGateway;
        _editors = editors;
        _catalogNotifications = workspaceService as IAgentProfileGateway;
        _settingsNavigationService = settingsNavigationService;
        _uiDispatcher = uiDispatcher;
        _tasks = new PresentationTaskScope((exception, token) => _uiDispatcher.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                ReportPresentationFailure(exception);
            }
        }, token), _uiDispatcher.ShutdownToken);
        _statusClear = new TimedStatusController(dispatcher: _uiDispatcher);
        _listDetail = new KeyedAdaptiveListDetailState<string, AgentWorkspaceRecord>(
            Workspaces,
            static workspace => workspace.WorkspaceId,
            keyComparer: StringComparer.OrdinalIgnoreCase,
            itemComparer: new AgentWorkspaceSnapshotComparer());
        _listDetail.SelectionChanging += OnWorkspaceSelectionChanging;
        _listDetail.SelectionChanged += OnWorkspaceSelectionChanged;
        _listDetail.PropertyChanged += OnListDetailPropertyChanged;
        _runtimeRefresh = new SerializedRefreshLoop(
            RefreshWorkspaceListAsync,
            exception => RunOnUiThread(() => ReportPresentationFailure(exception)));
        _extensionRefresh = new SerializedRefreshLoop(
            RefreshExtensionCatalogAsync,
            exception => RunOnUiThread(() => ReportPresentationFailure(exception)));
        _operation.PropertyChanged += OnOperationPropertyChanged;
        _runtimeAvailability = workspaceService as IAgentRuntimeAvailability;
        if (_runtimeAvailability is not null)
        {
            _runtimeAvailability.ConnectionStateChanged += OnRuntimeConnectionStateChanged;
        }
        _workspaceService.WorkspacesChanged += OnWorkspacesChanged;
        if (_dashboardLoader is not null)
        {
            _dashboardLoader.DashboardInvalidated += OnDashboardInvalidated;
        }
        if (_catalogNotifications is not null)
            _catalogNotifications.SelectableCapabilitiesChanged += OnEditorCatalogChanged;
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

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _initialization.RunAsync(InitializeCoreAsync, cancellationToken);
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (_initializationFailureStatus is not null
                    && (string.Equals(StatusText, _initializationFailureStatus, StringComparison.Ordinal)
                        || string.Equals(
                            StatusText,
                            "Agent Runtime is unavailable. Reconnecting...",
                            StringComparison.Ordinal)))
                {
                    ClearStatus();
                }
                _initializationFailureStatus = null;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    _initializationFailureStatus = $"Agent Runtime is unavailable: {ex.Message}";
                    SetStatus(
                        _initializationFailureStatus,
                        AgentWorkspaceStatusKind.Warning);
                }
            }).ConfigureAwait(false);
        }
    }

    public bool IsListActive => !IsEditorActive;

    public bool ShowWideLayout => _listDetail.Layout == AdaptiveListDetailLayout.Wide;

    public bool ShowCompactList => IsCompactLayout && IsListActive;

    public bool ShowCompactEditor => IsCompactLayout && IsEditorActive;

    public bool ShowListPane => ShowWideLayout || ShowCompactList;

    public bool ShowEditorPane => ShowWideLayout || ShowCompactEditor;

    public AgentWorkspaceRecord? SelectedWorkspace
    {
        get => _listDetail.SelectedItem;
        set
        {
            if (value is null)
            {
                _listDetail.ShowList();
            }
            else if (!(_listDetail.IsExistingDetail && ReferenceEquals(value, SelectedWorkspace)))
            {
                _listDetail.ShowExistingDetail(value);
            }
        }
    }

    public bool IsCompactLayout
    {
        get => _listDetail.Layout == AdaptiveListDetailLayout.Compact;
        set
        {
            var layout = value
                ? AdaptiveListDetailLayout.Compact
                : AdaptiveListDetailLayout.Wide;
            if (layout != _listDetail.Layout)
            {
                _editorIntentRevision++;
                _listDetail.SetLayout(layout);
            }
        }
    }

    public bool IsEditorActive => IsCompactLayout && !_listDetail.IsList;

    internal AdaptiveListDetailRoute Route => _listDetail.Route;

    internal AdaptiveListDetailLayout Layout => _listDetail.Layout;

    internal AdaptiveDetailPhase DetailPhase => _listDetail.DetailPhase;

    internal long IntentRevision => _listDetail.IntentRevision;

    internal long LayoutRevision => _listDetail.LayoutRevision;

    internal Task CurrentEditorSectionRefresh => _currentEditorSectionRefresh;

    internal Task CurrentEditorSectionRetry => _currentEditorSectionRetry;

    internal Task CurrentExecutionTargetRefresh => _currentExecutionTargetRefresh;

    internal Task CurrentRuntimeRefresh => _runtimeRefresh.WhenIdle;

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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCancellation.Cancel();
        _initialization.Dispose();
        if (_runtimeAvailability is not null)
        {
            _runtimeAvailability.ConnectionStateChanged -= OnRuntimeConnectionStateChanged;
        }
        _statusClear.Dispose();
        _tasks.Dispose();
        _operation.PropertyChanged -= OnOperationPropertyChanged;
        _operation.Dispose();
        _workspaceService.WorkspacesChanged -= OnWorkspacesChanged;
        if (_dashboardLoader is not null)
        {
            _dashboardLoader.DashboardInvalidated -= OnDashboardInvalidated;
        }
        if (_catalogNotifications is not null)
            _catalogNotifications.SelectableCapabilitiesChanged -= OnEditorCatalogChanged;

        _listDetail.PropertyChanged -= OnListDetailPropertyChanged;
        _listDetail.SelectionChanging -= OnWorkspaceSelectionChanging;
        _listDetail.SelectionChanged -= OnWorkspaceSelectionChanged;
        _runtimeRefresh.Dispose();
        _extensionRefresh.Dispose();
        _listDetail.Dispose();
        _requests.Dispose();
        _lifetimeCancellation.Dispose();
    }

    partial void OnSelectedExecutionTargetChanged(ExecutionTargetOption? value)
    {
        _executionTargetSelectionRevision++;
        if (_suppressDraftTracking)
        {
            return;
        }

        _editorIntentRevision++;
        OnWorkspaceEditorChanged();
        StartEditorSectionRefresh();
    }

    partial void OnDisplayNameChanged(string value) => OnWorkspaceEditorChanged();

    partial void OnDescriptionChanged(string value) => OnWorkspaceEditorChanged();

    partial void OnSelectedWorkspacePathChanged(AgentWorkspacePathItemViewModel? value)
    {
        SetSelectedWorkspacePathAsDefaultCommand.NotifyCanExecuteChanged();
        DeleteSelectedWorkspacePathCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedWorkspaceDocumentChanged(AgentWorkspaceDocumentItemViewModel? value)
        => DeleteSelectedWorkspaceDocumentCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private async Task CreateWorkspaceAsync()
    {
        try
        {
            AgentWorkspaceRecord workspace;
            _suppressWorkspaceRefresh = true;
            try
            {
                workspace = _workspaceCommands is null
                    ? _workspaceService.CreateWorkspace("New Workspace")
                    : await _workspaceCommands.CreateWorkspaceAsync(
                        "New Workspace",
                        _lifetimeCancellation.Token);
            }
            finally
            {
                _suppressWorkspaceRefresh = false;
            }
            var intentRevision = _listDetail.ShowNewDetail();
            _listDetail.Reconcile(await LoadWorkspacesAsync(_lifetimeCancellation.Token));
            _listDetail.TryShowCreatedDetail(workspace.WorkspaceId, intentRevision);
            DiscardPendingWorkspaceRefresh();
            ClearStatus();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, AgentWorkspaceStatusKind.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteWorkspace))]
    private async Task DeleteWorkspaceAsync()
    {
        if (SelectedWorkspace is null)
        {
            return;
        }

        try
        {
            var shouldClearSelection = IsCompactLayout;
            var workspaceId = SelectedWorkspace.WorkspaceId;
            _suppressWorkspaceRefresh = true;
            try
            {
                if (_workspaceCommands is null)
                {
                    await _workspaceService.DeleteWorkspaceAsync(workspaceId, _lifetimeCancellation.Token);
                }
                else
                {
                    await _workspaceCommands.DeleteWorkspaceAsync(
                        workspaceId,
                        _lifetimeCancellation.Token);
                }
            }
            finally
            {
                _suppressWorkspaceRefresh = false;
            }
            _workspaceDrafts.Remove(workspaceId);
            DiscardPendingWorkspaceRefresh();

            if (shouldClearSelection)
            {
                _listDetail.ShowList();
            }
            _listDetail.Reconcile(await LoadWorkspacesAsync(_lifetimeCancellation.Token));
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
        _operation.CancelCurrent();
        _listDetail.ShowList();
    }

    [RelayCommand]
    private void OpenWorkspaceEditor(AgentWorkspaceRecord workspace)
    {
        ActivateWorkspace(workspace);
    }

    public void ActivateWorkspace(AgentWorkspaceRecord workspace)
    {
        if (!(_listDetail.IsExistingDetail && ReferenceEquals(SelectedWorkspace, workspace)))
        {
            _operation.CancelCurrent();
            _listDetail.ShowExistingDetail(workspace);
        }
    }

    private void OnWorkspacesChanged()
    {
        if (_suppressWorkspaceRefresh)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            if (!_disposed && _isInitialized)
            {
                _tasks.Run(_ => _runtimeRefresh.MarkDirty());
            }
            else if (!_disposed)
            {
                _initializationRefreshPending = true;
            }
        });
    }

    private void OnDashboardInvalidated() => OnWorkspacesChanged();

    private void OnRuntimeConnectionStateChanged(AgentRuntimeConnectionState state)
        => RunOnUiThread(() =>
        {
            if (_disposed)
            {
                return;
            }
            if (state == AgentRuntimeConnectionState.Connected && _isInitialized)
            {
                StartExecutionTargetRefresh();
                _tasks.Run(_ => _runtimeRefresh.MarkDirty());
                ClearStatus();
            }
            else if (state == AgentRuntimeConnectionState.Connected)
            {
                _tasks.Run(InitializeAsync);
            }
            else if (state is AgentRuntimeConnectionState.Unavailable or AgentRuntimeConnectionState.Reconnecting)
            {
                SetStatus("Agent Runtime is unavailable. Reconnecting...", AgentWorkspaceStatusKind.Warning);
            }
        });

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _editorObservationStarted, 1) == 0)
            _tasks.Run(ObserveEditorProvidersAsync);
        while (true)
        {
            var request = _requests.Begin(ListRefreshChannel, cancellationToken);
            var applied = false;
            var replayPendingRefresh = false;
            var replayPendingCatalogRefresh = false;
            Task targetReplay = Task.CompletedTask;
            try
            {
                var targetsTask = _executionGateway.ListTargetsAsync(request.CancellationToken);
                await Task.WhenAll(
                        _workspaceService.InitializeAsync(request.CancellationToken),
                        targetsTask)
                    .WaitAsync(request.CancellationToken)
                    .ConfigureAwait(false);
                var targets = await targetsTask.WaitAsync(request.CancellationToken).ConfigureAwait(false);
                var workspaces = await LoadWorkspacesAsync(request.CancellationToken).ConfigureAwait(false);
                request.CancellationToken.ThrowIfCancellationRequested();
                await _uiDispatcher.InvokeAsync(() =>
                {
                    if (_disposed || !_requests.IsCurrent(request))
                    {
                        return;
                    }

                    ReloadTargets(targets);
                    _listDetail.Reconcile(workspaces);
                    _isInitialized = true;
                    replayPendingRefresh = _initializationRefreshPending;
                    _initializationRefreshPending = false;
                    replayPendingCatalogRefresh = _initializationCatalogRefreshPending;
                    _initializationCatalogRefreshPending = false;
                    if (replayPendingCatalogRefresh)
                    {
                        targetReplay = StartExecutionTargetRefresh();
                    }
                    applied = true;
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                request.CancellationToken.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                _requests.Complete(request);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!applied)
            {
                continue;
            }
            if (replayPendingRefresh)
            {
                await _runtimeRefresh.MarkDirty().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            if (replayPendingCatalogRefresh)
            {
                await targetReplay.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return;
        }
    }

    private void LoadWorkspace(
        AgentWorkspaceRecord? workspace,
        bool refreshEditorSections = true)
    {
        if (workspace is not null
            && _workspaceDrafts.TryGetValue(workspace.WorkspaceId, out var draft)
            && draft.IsDirty)
        {
            ApplyWorkspaceDraft(workspace, draft);
            return;
        }

        var binding = workspace is null
            ? null
            : _workspaceService.ListBindings(workspace.WorkspaceId)
                .FirstOrDefault(item => string.Equals(item.Role, AgentWorkspaceBindingRoles.PrimaryExecutionTarget, StringComparison.OrdinalIgnoreCase));
        var target = ResolveTargetOption(binding?.ContributionId);
        _suppressDraftTracking = true;
        try
        {
            DisplayName = workspace?.DisplayName ?? string.Empty;
            Description = workspace?.Description ?? string.Empty;
            LoadWorkspacePaths(workspace);
            LoadWorkspaceDocuments(workspace);
            SelectedExecutionTarget = target;
            if (workspace is null || refreshEditorSections)
            {
                ReplaceEditorSections([]);
            }
        }
        finally
        {
            _suppressDraftTracking = false;
        }

        if (workspace is null)
        {
            _currentEditorSectionRefresh = Task.CompletedTask;
            return;
        }

        RegisterCleanWorkspaceDraft(workspace);
        if (refreshEditorSections)
        {
            StartEditorSectionRefresh();
        }
        else
        {
            MarkCurrentWorkspaceDetailReady();
        }
    }

    private void LoadWorkspacePaths(AgentWorkspaceRecord? workspace)
        => ReplaceWorkspacePaths(workspace?.Paths ?? []);

    private void LoadWorkspaceDocuments(AgentWorkspaceRecord? workspace)
        => ReplaceWorkspaceDocuments(workspace?.Documents ?? []);

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

    private static StringComparison GetPathStringComparison()
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private void OnListDetailPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsCompactLayout));
        OnPropertyChanged(nameof(IsEditorActive));
        OnPropertyChanged(nameof(IsListActive));
        OnPropertyChanged(nameof(ShowWideLayout));
        OnPropertyChanged(nameof(ShowCompactList));
        OnPropertyChanged(nameof(ShowCompactEditor));
        OnPropertyChanged(nameof(ShowListPane));
        OnPropertyChanged(nameof(ShowEditorPane));
    }

    private void OnWorkspaceSelectionChanged(
        AgentWorkspaceRecord? previous,
        AgentWorkspaceRecord? current)
    {
        OnPropertyChanged(nameof(SelectedWorkspace));
        OnPropertyChanged(nameof(HasSelectedWorkspace));
        DeleteWorkspaceCommand.NotifyCanExecuteChanged();
        SaveWorkspaceCommand.NotifyCanExecuteChanged();
        var sameWorkspace = previous is not null
            && current is not null
            && string.Equals(
                previous.WorkspaceId,
                current.WorkspaceId,
                StringComparison.OrdinalIgnoreCase);
        LoadWorkspace(current, refreshEditorSections: !sameWorkspace);
    }

    private void OnWorkspaceSelectionChanging(
        AgentWorkspaceRecord? previous,
        AgentWorkspaceRecord? current)
        => CaptureCurrentWorkspaceDraft();

    private async Task RefreshWorkspaceListAsync(CancellationToken cancellationToken)
    {
        var request = _requests.Begin(ListRefreshChannel, cancellationToken);
        try
        {
            var workspaces = await LoadWorkspacesAsync(request.CancellationToken).ConfigureAwait(false);
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (_requests.IsCurrent(request))
                {
                    var previousWorkspaceId = SelectedWorkspace?.WorkspaceId;
                    var previousTargetId = SelectedExecutionTarget?.TargetId;
                    _listDetail.Reconcile(workspaces);
                    // Execution bindings are a separate projection. An unchanged
                    // workspace record must not hide an authoritative target change.
                    if (SelectedWorkspace is { } selected
                        && string.Equals(previousWorkspaceId, selected.WorkspaceId, StringComparison.OrdinalIgnoreCase)
                        && !IsWorkspaceDraftDirty(selected.WorkspaceId))
                    {
                        var target = ResolveTargetOption(ResolveWorkspaceTargetId(selected.WorkspaceId));
                        if (!string.Equals(previousTargetId, target.TargetId, StringComparison.OrdinalIgnoreCase)
                            || !Equals(SelectedExecutionTarget, target))
                            LoadWorkspace(selected, refreshEditorSections: true);
                    }
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            _requests.Complete(request);
        }
    }

    private void DiscardPendingWorkspaceRefresh()
    {
        _runtimeRefresh.DiscardPending();
        _requests.Invalidate(ListRefreshChannel);
    }

    private async Task<IReadOnlyList<AgentWorkspaceRecord>> LoadWorkspacesAsync(
        CancellationToken cancellationToken)
        => _dashboardLoader is null
            ? _workspaceService.ListWorkspaces()
            : (await _dashboardLoader.LoadDashboardAsync(cancellationToken).ConfigureAwait(false)).Workspaces;
}

internal enum AgentWorkspaceOperation
{
    Save,
    DiscoverEditor,
}
