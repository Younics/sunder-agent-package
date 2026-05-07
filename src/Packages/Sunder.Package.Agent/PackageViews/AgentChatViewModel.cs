using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveMarkdown.Avalonia;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Theming;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel : ObservableObject, IDisposable
{
    private const int InitialTranscriptTurnLimit = 100;
    private const int OlderTranscriptTurnPageSize = 60;
    private const string SubsessionsViewId = "sunder.package.agent.subagents.sessions";
    private const string SubsessionNavigationSessionIdKey = "sessionId";
    private static readonly TimeSpan DefaultActivityQuietDelay = TimeSpan.FromMilliseconds(900);

    private readonly AgentProfileService _profileService;
    private readonly AgentWorkspaceService _workspaceService;
    private readonly AgentSessionService _sessionService;
    private readonly AgentAttachmentService? _attachmentService;
    private readonly AgentPermissionService _permissionService;
    private readonly AgentRunCoordinator _runCoordinator;
    private readonly AgentExecutionTargetWarmupService? _warmupService;
    private readonly AgentChatSelectionStateService? _selectionState;
    private readonly AgentToolPresentationService _toolPresentationService;
    private readonly IPackageShellViewService? _shellViewService;
    private readonly DispatcherTimer _activityQuietTimer;
    private readonly TimeSpan _activityQuietDelay;
    private readonly Dictionary<Guid, AgentTextTranscriptRowViewModel> _textRowsByTurnId = new();
    private readonly Dictionary<string, AgentToolInvocationRowViewModel> _toolRowsByCallId = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _loadedTurnIds = new();
    private AgentSessionListItemViewModel? _observedSelectedSession;
    private AgentActivityTranscriptRowViewModel? _activityRow;
    private DateTimeOffset? _oldestLoadedTurnCreatedAtUtc;
    private Guid? _oldestLoadedTurnId;
    private string _globalStatusText = string.Empty;
    private string _activityTextBase = "Thinking";
    private bool _hasVisibleRunActivity;
    private bool _showActivityAfterQuiet;
    private bool _isReconcilingSessionSelection;
    private bool _isRestoringReconciledSessionSelection;
    private bool _suppressWorkspaceSelection;
    private bool _suppressPermissionState;

    public AgentChatViewModel(
        AgentProfileService profileService,
        AgentWorkspaceService workspaceService,
        AgentSessionService sessionService,
        AgentPermissionService permissionService,
        AgentRunCoordinator runCoordinator,
        AgentChatSelectionStateService? selectionState = null,
        AgentToolPresentationService? toolPresentationService = null,
        TimeSpan? activityQuietDelay = null,
        AgentExecutionTargetWarmupService? warmupService = null,
        IPackageShellViewService? shellViewService = null,
        AgentAttachmentService? attachmentService = null)
    {
        _profileService = profileService;
        _workspaceService = workspaceService;
        _sessionService = sessionService;
        _attachmentService = attachmentService;
        _permissionService = permissionService;
        _runCoordinator = runCoordinator;
        _warmupService = warmupService;
        _selectionState = selectionState;
        _toolPresentationService = toolPresentationService ?? new AgentToolPresentationService();
        _shellViewService = shellViewService;
        _activityQuietDelay = activityQuietDelay ?? DefaultActivityQuietDelay;
        _activityQuietTimer = new DispatcherTimer
        {
            Interval = _activityQuietDelay <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : _activityQuietDelay,
        };
        _activityQuietTimer.Tick += OnActivityQuietTimerTick;
        PendingAttachments.CollectionChanged += OnPendingAttachmentsChanged;
        _profileService.ProfileChanged += OnProfilesChanged;
        _workspaceService.WorkspacesChanged += OnWorkspacesChanged;
        _sessionService.SessionChanged += OnSessionChanged;
        _sessionService.TurnChanged += OnTurnChanged;
        ReloadProfiles(_selectionState?.GetSelectedProfileId());
        ReloadWorkspaces(_selectionState?.GetSelectedWorkspaceId());
        ReloadSessions(_selectionState?.GetSelectedSessionId());
        ScheduleSelectedWorkspaceWarmup();
    }

    public ObservableCollection<AgentWorkspaceRecord> Workspaces { get; } = [];

    public ObservableCollection<AgentProfileRecord> Profiles { get; } = [];

    public ObservableCollection<AgentSessionListItemViewModel> Sessions { get; } = [];

    public ObservableCollection<AgentTranscriptRowViewModel> Messages { get; } = [];

    public ObservableCollection<AgentPendingPermissionRequestRecord> PendingPermissionRequests { get; } = [];

    public event Action? TranscriptChanged;

    public bool IsSelectedSessionRunActive => SelectedSession?.IsRunActive == true;

    public bool IsSelectedSessionRunInactive => SelectedSession is not null && !IsSelectedSessionRunActive;

    public bool IsDisplayedSessionRunActive => DisplayedSession?.IsRunActive == true;

    public bool IsComposerCollapsed => !IsComposerExpanded;

    public bool CanUseChat => SelectedSession is not null && SelectedProfile is not null && SelectedWorkspace is not null;

    public bool CannotUseChat => !CanUseChat;

    public bool HasWorkspaces => Workspaces.Count > 0;

    public bool HasNoWorkspaces => !HasWorkspaces;

    public bool HasProfiles => Profiles.Count > 0;

    public bool HasNoProfiles => !HasProfiles;

    public bool HasSelectedSession => SelectedSession is not null;

    public bool ShowSetupInstructions => !CanUseChat;

    public bool ShowTranscriptSurface => IsComposerCollapsed || !CanUseChat;

    public bool ShowCollapsedComposer => CanUseChat && IsComposerCollapsed;

    public bool ShowExpandedComposer => CanUseChat && IsComposerExpanded;

    public bool CanLoadOlderTranscriptRows => HasOlderTranscriptRows && !IsLoadingOlderTranscriptRows && DisplayedSession is not null;

    [ObservableProperty]
    private AgentWorkspaceRecord? _selectedWorkspace;

    [ObservableProperty]
    private AgentProfileRecord? _selectedProfile;

    [ObservableProperty]
    private AgentSessionListItemViewModel? _selectedSession;

    [ObservableProperty]
    private AgentSessionListItemViewModel? _displayedSession;

    [ObservableProperty]
    private string _draftMessage = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _setupTitle = "Create a workspace before chatting";

    [ObservableProperty]
    private string _setupDescription = "Create an Agent Profile, select a workspace and session, then start chatting.";

    [ObservableProperty]
    private bool _isUnrestrictedModeEnabled;

    [ObservableProperty]
    private bool _isComposerExpanded;

    [ObservableProperty]
    private bool _hasPendingPermissionRequests;

    [ObservableProperty]
    private bool _hasOlderTranscriptRows;

    [ObservableProperty]
    private bool _isLoadingOlderTranscriptRows;

    partial void OnHasOlderTranscriptRowsChanged(bool value)
        => OnPropertyChanged(nameof(CanLoadOlderTranscriptRows));

    partial void OnIsLoadingOlderTranscriptRowsChanged(bool value)
        => OnPropertyChanged(nameof(CanLoadOlderTranscriptRows));

    partial void OnSelectedWorkspaceChanged(AgentWorkspaceRecord? value)
    {
        if (_suppressWorkspaceSelection)
        {
            return;
        }

        _selectionState?.SaveSelectedWorkspaceId(value?.WorkspaceId);
        _globalStatusText = string.Empty;
        RefreshSetupState();
        ScheduleSelectedWorkspaceWarmup();
    }

    partial void OnSelectedProfileChanged(AgentProfileRecord? value)
    {
        _selectionState?.SaveSelectedProfileId(value?.ProfileId);
        _globalStatusText = string.Empty;
        CreateSessionCommand.NotifyCanExecuteChanged();
        RefreshSetupState();
    }

    partial void OnIsUnrestrictedModeEnabledChanged(bool value)
    {
        if (_suppressPermissionState || SelectedSession is null)
        {
            return;
        }

        _permissionService.SetSessionUnrestrictedMode(SelectedSession.SessionId, value);
        ApplySessionStatus(SelectedSession, value
            ? "Unrestricted Mode is enabled for this session. Ask-style approvals are auto-approved, but hard constraints still apply."
            : "Unrestricted Mode is disabled for this session.");
    }

    partial void OnIsComposerExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsComposerCollapsed));
        OnPropertyChanged(nameof(ShowTranscriptSurface));
        OnPropertyChanged(nameof(ShowCollapsedComposer));
        OnPropertyChanged(nameof(ShowExpandedComposer));
    }

    partial void OnDraftMessageChanged(string value)
    {
        if (SelectedSession is not null && !string.Equals(SelectedSession.DraftMessage, value, StringComparison.Ordinal))
        {
            SelectedSession.DraftMessage = value;
        }

        SendMessageCommand.NotifyCanExecuteChanged();
        ClearComposerCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task ApprovePermissionAsync(AgentPendingPermissionRequestRecord? request)
    {
        var selectedSession = SelectedSession;
        if (selectedSession is null || request is null || string.IsNullOrWhiteSpace(request.RequestId))
        {
            return;
        }

        var checkpoint = await _runCoordinator.ApprovePendingPermissionAsync(request.SessionId, request.RequestId);
        ReloadPendingPermissionRequests();
        ApplySessionStatus(selectedSession, checkpoint?.Summary ?? "Permission request was no longer pending.");
        SyncSelectedSessionState(selectedSession.SessionId);
    }

    [RelayCommand]
    private async Task ApprovePermissionForSessionAsync(AgentPendingPermissionRequestRecord? request)
    {
        var selectedSession = SelectedSession;
        if (selectedSession is null || request is null || string.IsNullOrWhiteSpace(request.RequestId))
        {
            return;
        }

        _permissionService.SaveSessionApproval(request.SessionId, request.ActionId, request.BoundaryId);
        var checkpoint = await _runCoordinator.ApprovePendingPermissionAsync(request.SessionId, request.RequestId);
        ReloadPendingPermissionRequests();
        ApplySessionStatus(selectedSession, checkpoint?.Summary ?? "Permission request was no longer pending.");
        SyncSelectedSessionState(selectedSession.SessionId);
    }

    [RelayCommand]
    private void DenyPermission(AgentPendingPermissionRequestRecord? request)
    {
        var selectedSession = SelectedSession;
        if (selectedSession is null || request is null || string.IsNullOrWhiteSpace(request.RequestId))
        {
            return;
        }

        _runCoordinator.DenyPendingPermission(request.SessionId, request.RequestId);
        ReloadPendingPermissionRequests();
        ApplySessionStatus(selectedSession, "Permission request denied.");
        SyncSelectedSessionState(selectedSession.SessionId);
    }

    [RelayCommand(CanExecute = nameof(CanClearComposer))]
    private void ClearComposer()
    {
        DraftMessage = string.Empty;
        if (SelectedSession is not null)
        {
            SelectedSession.DraftMessage = string.Empty;
        }

        ClearPendingAttachments();
    }

    private bool CanClearComposer()
        => !string.IsNullOrEmpty(DraftMessage) || PendingAttachments.Count > 0;

    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private async Task SendMessageAsync()
    {
        var selectedSession = SelectedSession;
        if (selectedSession is null)
        {
            SetGlobalStatus("Create or select a session first.");
            return;
        }

        var workspace = SelectedWorkspace;
        if (workspace is null)
        {
            ApplySessionStatus(selectedSession, "Select a workspace before chatting. Sessions can stay open while you switch workspaces.");
            return;
        }

        var message = DraftMessage.Trim();
        if (string.IsNullOrWhiteSpace(message) && PendingAttachments.Count == 0)
        {
            return;
        }

        var profile = SelectedProfile;
        if (profile is null)
        {
            ApplySessionStatus(selectedSession, "Select or create an Agent Profile before chatting.");
            return;
        }

        var chatBinding = _profileService.GetChatBinding(profile.ProfileId);
        if (string.IsNullOrWhiteSpace(chatBinding?.ProviderId) || string.IsNullOrWhiteSpace(chatBinding.ModelId))
        {
            ApplySessionStatus(selectedSession, "The selected profile is missing a provider or model. Configure it in Agent Profiles before chatting.");
            return;
        }

        var readiness = await _profileService.GetChatProviderReadinessAsync(chatBinding.ProviderId);
        if (readiness is null)
        {
            ApplySessionStatus(selectedSession, "The selected provider is unavailable. Review package status and profile configuration before chatting.");
            return;
        }

        if (readiness.Status != AgentProviderReadinessStatus.Ready)
        {
            ApplySessionStatus(selectedSession, readiness.Message);
            return;
        }

        var sessionId = selectedSession.SessionId;
        var attachments = PendingAttachments.Select(attachment => attachment.UploadRequest).ToArray();
        selectedSession.DraftMessage = string.Empty;
        if (SelectedSession?.SessionId == sessionId)
        {
            DraftMessage = string.Empty;
        }

        ClearPendingAttachments();

        await _runCoordinator.QueueUserMessageAsync(sessionId, profile.ProfileId, message, workspace.WorkspaceId, attachments);

        if (SelectedSession?.SessionId == sessionId)
        {
            ReloadPendingPermissionRequests();
            SyncSelectedSessionState(sessionId);
        }
        else
        {
            UpdateSessionState(sessionId, markUnread: true);
        }
    }

    private bool CanSendMessage()
        => CanUseChat && IsSelectedSessionRunInactive && (!string.IsNullOrWhiteSpace(DraftMessage) || PendingAttachments.Count > 0);

    [RelayCommand]
    private async Task StopRunAsync()
    {
        var selectedSession = SelectedSession;
        if (selectedSession is null)
        {
            return;
        }

        var sessionId = selectedSession.SessionId;
        var checkpoint = await _runCoordinator.StopAsync(sessionId);

        if (checkpoint is null)
        {
            ApplySessionStatus(selectedSession, "No active run to stop.");
            return;
        }

        if (SelectedSession?.SessionId == sessionId)
        {
            SyncSelectedSessionState(sessionId);
        }
        else
        {
            UpdateSessionState(sessionId, markUnread: true);
        }
    }

    public void Dispose()
    {
        if (_observedSelectedSession is not null)
        {
            _observedSelectedSession.PropertyChanged -= OnSelectedSessionPropertyChanged;
        }

        PendingAttachments.CollectionChanged -= OnPendingAttachmentsChanged;
        _profileService.ProfileChanged -= OnProfilesChanged;
        _workspaceService.WorkspacesChanged -= OnWorkspacesChanged;
        _sessionService.SessionChanged -= OnSessionChanged;
        _sessionService.TurnChanged -= OnTurnChanged;
        _activityQuietTimer.Stop();
        _activityQuietTimer.Tick -= OnActivityQuietTimerTick;
        _workspaceWarmupCts?.Cancel();
        _workspaceWarmupCts?.Dispose();
        _activityRow?.Dispose();
        DisposeAttachmentPreviewImage();
    }

    private void RunOnUiThread(Action action)
    {
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
    }

    private void OnProfilesChanged(string profileId)
        => RunOnUiThread(() => ReloadProfiles(SelectedProfile?.ProfileId ?? _selectionState?.GetSelectedProfileId()));

    private void ReloadProfiles(string? selectProfileId)
    {
        var profiles = _profileService.ListProfiles();
        Profiles.Clear();
        foreach (var profile in profiles)
        {
            Profiles.Add(profile);
        }

        var desiredProfileId = selectProfileId
            ?? SelectedProfile?.ProfileId;
        SelectedProfile = Profiles.FirstOrDefault(profile => string.Equals(profile.ProfileId, desiredProfileId, StringComparison.OrdinalIgnoreCase))
            ?? Profiles.FirstOrDefault();
        _selectionState?.SaveSelectedProfileId(SelectedProfile?.ProfileId);
        NotifyProfileStateChanged();
        CreateSessionCommand.NotifyCanExecuteChanged();
        RefreshSetupState();
    }

    private bool ReloadWorkspaces(string? selectWorkspaceId)
    {
        var previousSelectedWorkspaceId = SelectedWorkspace?.WorkspaceId;
        var workspaces = _workspaceService.ListWorkspaces();
        _suppressWorkspaceSelection = true;
        try
        {
            ReconcileWorkspaces(workspaces);

            var desiredWorkspaceId = selectWorkspaceId
                ?? previousSelectedWorkspaceId;

            SelectedWorkspace = Workspaces.FirstOrDefault(workspace => string.Equals(workspace.WorkspaceId, desiredWorkspaceId, StringComparison.OrdinalIgnoreCase))
                ?? Workspaces.FirstOrDefault();
        }
        finally
        {
            _suppressWorkspaceSelection = false;
        }

        var selectedWorkspaceChanged = !string.Equals(previousSelectedWorkspaceId, SelectedWorkspace?.WorkspaceId, StringComparison.OrdinalIgnoreCase);
        _selectionState?.SaveSelectedWorkspaceId(SelectedWorkspace?.WorkspaceId);
        NotifyWorkspaceStateChanged();
        CreateSessionCommand.NotifyCanExecuteChanged();
        RefreshSetupState();
        return selectedWorkspaceChanged;
    }

    private void ReconcileWorkspaces(IReadOnlyList<AgentWorkspaceRecord> workspaces)
    {
        var desiredWorkspaceIds = workspaces.Select(workspace => workspace.WorkspaceId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var index = Workspaces.Count - 1; index >= 0; index--)
        {
            if (desiredWorkspaceIds.Contains(Workspaces[index].WorkspaceId))
            {
                continue;
            }

            Workspaces.RemoveAt(index);
        }

        for (var index = 0; index < workspaces.Count; index++)
        {
            var workspace = workspaces[index];
            var existingIndex = FindWorkspaceIndex(workspace.WorkspaceId);
            if (existingIndex < 0)
            {
                Workspaces.Insert(index, workspace);
                continue;
            }

            if (!Equals(Workspaces[existingIndex], workspace))
            {
                Workspaces[existingIndex] = workspace;
            }

            if (existingIndex == index)
            {
                continue;
            }

            Workspaces.Move(existingIndex, index);
        }
    }

    private void NotifyWorkspaceStateChanged()
    {
        OnPropertyChanged(nameof(HasWorkspaces));
        OnPropertyChanged(nameof(HasNoWorkspaces));
    }

    private void NotifyProfileStateChanged()
    {
        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(HasNoProfiles));
    }

    private void RefreshSetupState()
    {
        var (title, description) = GetSetupContent();
        SetupTitle = title;
        SetupDescription = description;
        if (SelectedSession is null)
        {
            StatusText = string.IsNullOrWhiteSpace(_globalStatusText)
                ? description
                : _globalStatusText;
        }
        else if (!CanUseChat)
        {
            StatusText = string.IsNullOrWhiteSpace(_globalStatusText)
                ? GetSetupStatusText()
                : _globalStatusText;
        }

        OnPropertyChanged(nameof(CanUseChat));
        OnPropertyChanged(nameof(CannotUseChat));
        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(HasNoProfiles));
        OnPropertyChanged(nameof(HasSelectedSession));
        OnPropertyChanged(nameof(ShowSetupInstructions));
        OnPropertyChanged(nameof(ShowTranscriptSurface));
        OnPropertyChanged(nameof(ShowCollapsedComposer));
        OnPropertyChanged(nameof(ShowExpandedComposer));
        OnPropertyChanged(nameof(IsSelectedSessionRunInactive));
        SendMessageCommand.NotifyCanExecuteChanged();
    }

    private (string Title, string Description) GetSetupContent()
    {
        if (Profiles.Count == 0)
        {
            return (
                "Create an agent profile before chatting",
                "Agent Profiles choose model settings, instructions, and runtime capabilities. Create one in Agent Profiles, then return here to chat.");
        }

        if (Workspaces.Count == 0)
        {
            return (
                "Create a workspace before chatting",
                "Workspaces choose the execution environment used by sessions. Open Workspaces and create one to start chatting.");
        }

        if (SelectedWorkspace is null)
        {
            return (
                "Select a workspace",
                "Choose the workspace this session should run against. You can switch workspaces without changing sessions.");
        }

        return (
            "Create a session to start chatting",
            "Create or select a session to begin a conversation with the selected agent profile.");
    }

    private string GetSetupStatusText()
    {
        if (Profiles.Count == 0)
        {
            return "Create an Agent Profile before chatting.";
        }

        if (Workspaces.Count == 0)
        {
            return "Create a workspace before chatting. Workspaces choose the execution environment used by sessions.";
        }

        if (SelectedWorkspace is null)
        {
            return "Select a workspace to run the selected session.";
        }

        if (SelectedProfile is null)
        {
            return "Select an Agent Profile before chatting.";
        }

        return "Create a session to start chatting.";
    }

    private int FindWorkspaceIndex(string workspaceId)
    {
        for (var index = 0; index < Workspaces.Count; index++)
        {
            if (string.Equals(Workspaces[index].WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private void OnWorkspacesChanged()
        => RunOnUiThread(ApplyWorkspacesChanged);

    private void ApplyWorkspacesChanged()
    {
        ReloadWorkspaces(SelectedWorkspace?.WorkspaceId);
        ScheduleSelectedWorkspaceWarmup();
    }

    private void SetGlobalStatus(string statusText)
    {
        _globalStatusText = statusText;
        if (SelectedSession is null)
        {
            StatusText = statusText;
        }
    }

}
