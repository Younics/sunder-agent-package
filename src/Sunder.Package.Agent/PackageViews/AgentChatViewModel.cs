using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel : ObservableObject, IDisposable
{
    private const int InitialTranscriptTurnLimit = 60;
    private const int OlderTranscriptTurnPageSize = 30;
    private const int TranscriptVisibleRowLimit = 60;
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
    private readonly Dictionary<string, AgentToolInvocationRowViewModel> _toolRowsByCallId = new(
        StringComparer.Ordinal
    );
    private readonly AgentTranscriptTurnWindow _transcriptTurnWindow = new(TranscriptVisibleRowLimit * 2);
    private readonly Dictionary<Guid, AgentTurnRecord> _pendingTranscriptTurnsByTurnId = new();
    private readonly object _pendingSendSync = new();
    private readonly HashSet<Guid> _pendingSendSessionIds = [];
    private AgentSessionListItemViewModel? _observedSelectedSession;
    private AgentActivityTranscriptRowViewModel? _activityRow;
    private CancellationTokenSource? _transcriptRefreshCts;
    private string _globalStatusText = string.Empty;
    private string _activityTextBase = "Thinking";
    private bool _activityIsReasoning;
    private bool _hasVisibleRunActivity;
    private bool _showActivityAfterQuiet;
    private bool _isReconcilingSessionSelection;
    private bool _isRestoringReconciledSessionSelection;
    private bool _isReplacingTranscriptWindow;
    private bool _isTranscriptDetachedFromLatest;
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
        AgentAttachmentService? attachmentService = null
    )
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
            Interval =
                _activityQuietDelay <= TimeSpan.Zero
                    ? TimeSpan.FromMilliseconds(1)
                    : _activityQuietDelay,
        };
        _activityQuietTimer.Tick += OnActivityQuietTimerTick;
        PendingAttachments.CollectionChanged += OnPendingAttachmentsChanged;
        _profileService.ProfileChanged += OnProfilesChanged;
        _workspaceService.WorkspacesChanged += OnWorkspacesChanged;
        _sessionService.SessionChanged += OnSessionChanged;
        _sessionService.TurnChanged += OnTurnChanged;
        _sessionService.TranscriptReset += OnTranscriptReset;
        _sessionService.RunActivityChanged += OnRunActivityChanged;
        ReloadProfiles(_selectionState?.GetSelectedProfileId());
        ReloadWorkspaces(_selectionState?.GetSelectedWorkspaceId());
        ScheduleSelectedWorkspaceWarmup();
    }

    public ObservableCollection<AgentWorkspaceRecord> Workspaces { get; } = [];

    public ObservableCollection<AgentProfileRecord> Profiles { get; } = [];

    public ObservableCollection<AgentSessionListItemViewModel> Sessions { get; } = [];

    public ObservableCollection<AgentWorkspacePathChipViewModel> WideWorkspacePathChips { get; } = [];

    public ObservableCollection<AgentWorkspacePathChipViewModel> NarrowWorkspacePathChips { get; } = [];

    public ObservableCollection<AgentTranscriptRowViewModel> Messages { get; } = [];

    public IReadOnlyList<string> WorkspacePathChipLabels => _workspacePathChipLabels;

    public ObservableCollection<AgentPendingPermissionRequestRecord> PendingPermissionRequests { get; } =
        [];

    public event Action? TranscriptChanged;

    public event Action? TranscriptChanging;

    public bool IsSelectedSessionRunActive => SelectedSession?.IsRunActive == true;

    public bool IsSelectedSessionRunInactive =>
        SelectedSession is not null
        && !IsSelectedSessionRunActive
        && !IsSendPending(SelectedSession.SessionId);

    public bool ShowSendAction =>
        !IsSelectedSessionSendPending() && (IsSelectedSessionRunInactive || IsRollbackPending);

    public bool ShowStopAction => IsSelectedSessionRunActive && !IsRollbackPending;

    public bool IsDisplayedSessionRunActive => DisplayedSession?.IsRunActive == true;

    public bool IsComposerCollapsed => !IsComposerExpanded;

    public bool CanUseChat =>
        SelectedSession is not null && SelectedProfile is not null && SelectedWorkspace is not null;

    public bool CannotUseChat => !CanUseChat;

    public bool HasWorkspaces => Workspaces.Count > 0;

    public bool HasNoWorkspaces => !HasWorkspaces;

    public bool HasWorkspacePathChips => _workspacePathChipLabels.Length > 0;

    public bool HasWideWorkspacePathOverflow => !string.IsNullOrWhiteSpace(WideWorkspacePathOverflowText);

    public bool HasNarrowWorkspacePathOverflow => !string.IsNullOrWhiteSpace(NarrowWorkspacePathOverflowText);

    public bool HasProfiles => Profiles.Count > 0;

    public bool HasNoProfiles => !HasProfiles;

    public bool HasSelectedSession => SelectedSession is not null;

    public bool ShowSetupInstructions => !CanUseChat;

    public bool ShowTranscriptSurface => IsComposerCollapsed || !CanUseChat;

    public bool ShowCollapsedComposer => CanUseChat && IsComposerCollapsed;

    public bool ShowExpandedComposer => CanUseChat && IsComposerExpanded;

    public bool IsRollbackPending => PendingRollbackTurnId is not null;

    public string ClearComposerButtonText => IsRollbackPending ? "Cancel Rollback" : "Clear";

    public string ClearComposerToolTipText => IsRollbackPending
        ? "Cancel rollback and clear message"
        : "Clear message and attachments";

    public bool CanLoadOlderTranscriptRows =>
        HasOlderTranscriptRows && !IsLoadingOlderTranscriptRows && !IsTranscriptLoading && DisplayedSession is not null;

    public bool CanLoadNewerTranscriptRows =>
        HasNewerTranscriptRows && !IsLoadingNewerTranscriptRows && !IsTranscriptLoading && DisplayedSession is not null;

    [ObservableProperty]
    private AgentWorkspaceRecord? _selectedWorkspace;

    [ObservableProperty]
    private AgentProfileRecord? _selectedProfile;

    [ObservableProperty]
    private AgentSessionListItemViewModel? _selectedSession;

    [ObservableProperty]
    private AgentSessionListItemViewModel? _displayedSession;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWideWorkspacePathOverflow))]
    private string _wideWorkspacePathOverflowText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNarrowWorkspacePathOverflow))]
    private string _narrowWorkspacePathOverflowText = string.Empty;

    [ObservableProperty]
    private string _draftMessage = string.Empty;

    [ObservableProperty]
    private Guid? _pendingRollbackTurnId;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _setupTitle = "Create a workspace before chatting";

    [ObservableProperty]
    private string _setupDescription =
        "Create an Agent, select a workspace and session, then start chatting.";

    [ObservableProperty]
    private bool _isUnrestrictedModeEnabled;

    [ObservableProperty]
    private bool _isSendOnEnterEnabled = true;

    [ObservableProperty]
    private bool _isComposerExpanded;

    [ObservableProperty]
    private bool _hasPendingPermissionRequests;

    [ObservableProperty]
    private bool _hasOlderTranscriptRows;

    [ObservableProperty]
    private bool _hasNewerTranscriptRows;

    [ObservableProperty]
    private bool _isLoadingOlderTranscriptRows;

    [ObservableProperty]
    private bool _isLoadingNewerTranscriptRows;

    [ObservableProperty]
    private bool _isTranscriptLoading;

    partial void OnHasOlderTranscriptRowsChanged(bool value) =>
        OnPropertyChanged(nameof(CanLoadOlderTranscriptRows));

    partial void OnHasNewerTranscriptRowsChanged(bool value) =>
        OnPropertyChanged(nameof(CanLoadNewerTranscriptRows));

    partial void OnIsLoadingOlderTranscriptRowsChanged(bool value) =>
        OnPropertyChanged(nameof(CanLoadOlderTranscriptRows));

    partial void OnIsLoadingNewerTranscriptRowsChanged(bool value) =>
        OnPropertyChanged(nameof(CanLoadNewerTranscriptRows));

    partial void OnIsTranscriptLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLoadOlderTranscriptRows));
        OnPropertyChanged(nameof(CanLoadNewerTranscriptRows));
    }

    partial void OnIsUnrestrictedModeEnabledChanged(bool value)
    {
        if (_suppressPermissionState || SelectedSession is null)
        {
            return;
        }

        _permissionService.SetSessionUnrestrictedMode(SelectedSession.SessionId, value);
        ApplySessionStatus(
            SelectedSession,
            value
                ? "Unrestricted Mode is enabled for this session. Ask-style approvals are auto-approved, but hard constraints still apply."
                : "Unrestricted Mode is disabled for this session."
        );
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
        if (
            SelectedSession is not null
            && !string.Equals(SelectedSession.DraftMessage, value, StringComparison.Ordinal)
        )
        {
            SelectedSession.DraftMessage = value;
        }

        SendMessageCommand.NotifyCanExecuteChanged();
        ClearComposerCommand.NotifyCanExecuteChanged();
    }

    partial void OnPendingRollbackTurnIdChanged(Guid? value)
    {
        OnPropertyChanged(nameof(IsRollbackPending));
        OnPropertyChanged(nameof(ClearComposerButtonText));
        OnPropertyChanged(nameof(ClearComposerToolTipText));
        OnPropertyChanged(nameof(ShowSendAction));
        OnPropertyChanged(nameof(ShowStopAction));
        SendMessageCommand.NotifyCanExecuteChanged();
        ClearComposerCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task ApprovePermissionAsync(AgentPendingPermissionRequestRecord? request)
    {
        var selectedSession = SelectedSession;
        if (
            selectedSession is null
            || request is null
            || string.IsNullOrWhiteSpace(request.RequestId)
        )
        {
            return;
        }

        var checkpoint = await _runCoordinator.ApprovePendingPermissionAsync(
            request.SessionId,
            request.RequestId
        );
        ReloadPendingPermissionRequests();
        ApplySessionStatus(
            selectedSession,
            checkpoint?.Summary ?? "Permission request was no longer pending."
        );
        SyncSelectedSessionState(selectedSession.SessionId);
    }

    [RelayCommand]
    private async Task ApprovePermissionForSessionAsync(
        AgentPendingPermissionRequestRecord? request
    )
    {
        var selectedSession = SelectedSession;
        if (
            selectedSession is null
            || request is null
            || string.IsNullOrWhiteSpace(request.RequestId)
        )
        {
            return;
        }

        _permissionService.SaveSessionApproval(
            request.SessionId,
            request.ActionId,
            request.BoundaryId
        );
        var checkpoint = await _runCoordinator.ApprovePendingPermissionAsync(
            request.SessionId,
            request.RequestId
        );
        ReloadPendingPermissionRequests();
        ApplySessionStatus(
            selectedSession,
            checkpoint?.Summary ?? "Permission request was no longer pending."
        );
        SyncSelectedSessionState(selectedSession.SessionId);
    }

    [RelayCommand]
    private void DenyPermission(AgentPendingPermissionRequestRecord? request)
    {
        var selectedSession = SelectedSession;
        if (
            selectedSession is null
            || request is null
            || string.IsNullOrWhiteSpace(request.RequestId)
        )
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
        if (IsRollbackPending)
        {
            CancelRollback();
            return;
        }

        DraftMessage = string.Empty;
        if (SelectedSession is not null)
        {
            SelectedSession.DraftMessage = string.Empty;
        }

        ClearPendingAttachments();
    }

    private bool CanClearComposer() =>
        IsRollbackPending || !string.IsNullOrEmpty(DraftMessage) || PendingAttachments.Count > 0;

    [RelayCommand(CanExecute = nameof(CanSendMessage), AllowConcurrentExecutions = true)]
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
            ApplySessionStatus(
                selectedSession,
                "Select a workspace before chatting. Sessions are scoped to their workspace."
            );
            return;
        }

        var sessionId = selectedSession.SessionId;
        var workspaceId = workspace.WorkspaceId;
        var draftSnapshot = DraftMessage;
        var message = draftSnapshot.Trim();
        var attachmentIds = PendingAttachments
            .Select(attachment => attachment.AttachmentId)
            .ToArray();
        var attachments = PendingAttachments
            .Select(attachment => attachment.UploadRequest)
            .ToArray();
        if (string.IsNullOrWhiteSpace(message) && attachments.Length == 0)
        {
            return;
        }

        var profile = SelectedProfile;
        if (profile is null)
        {
            ApplySessionStatus(selectedSession, "Select or create an Agent before chatting.");
            return;
        }

        var profileId = profile.ProfileId;
        var chatBinding = _profileService.GetChatBinding(profile.ProfileId);
        if (
            string.IsNullOrWhiteSpace(chatBinding?.ProviderId)
            || string.IsNullOrWhiteSpace(chatBinding.ModelId)
        )
        {
            ApplySessionStatus(
                selectedSession,
                "The selected profile is missing a provider or model. Configure it in Agents before chatting."
            );
            return;
        }

        var rollbackAnchorTurnId = PendingRollbackTurnId;
        if (!TryBeginPendingSend(sessionId))
        {
            ApplySessionStatus(
                selectedSession,
                "A message is already being sent for this session."
            );
            return;
        }

        try
        {
            var readiness = await _profileService.GetChatProviderReadinessAsync(
                chatBinding.ProviderId
            );
            if (readiness is null)
            {
                ApplySessionStatus(
                    selectedSession,
                    "The selected provider is unavailable. Review package status and profile configuration before chatting."
                );
                return;
            }

            if (readiness.Status != AgentProviderReadinessStatus.Ready)
            {
                ApplySessionStatus(selectedSession, readiness.Message);
                return;
            }

            ClearSubmittedComposerState(
                selectedSession,
                sessionId,
                draftSnapshot,
                attachmentIds,
                rollbackAnchorTurnId
            );

            if (rollbackAnchorTurnId is { } anchorTurnId)
            {
                await _runCoordinator.RollbackAndQueueUserMessageAsync(
                    sessionId,
                    anchorTurnId,
                    profileId,
                    message,
                    workspaceId,
                    attachments
                );
            }
            else
            {
                await _runCoordinator.QueueUserMessageAsync(
                    sessionId,
                    profileId,
                    message,
                    workspaceId,
                    attachments
                );
            }

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
        finally
        {
            EndPendingSend(sessionId);
        }
    }

    private bool CanSendMessage() =>
        CanUseChat
        && !IsSelectedSessionSendPending()
        && (IsSelectedSessionRunInactive || IsRollbackPending)
        && (!string.IsNullOrWhiteSpace(DraftMessage) || PendingAttachments.Count > 0);

    private void ClearSubmittedComposerState(
        AgentSessionListItemViewModel submittedSession,
        Guid sessionId,
        string draftSnapshot,
        IReadOnlyList<Guid> attachmentIds,
        Guid? rollbackAnchorTurnId
    )
    {
        if (string.Equals(submittedSession.DraftMessage, draftSnapshot, StringComparison.Ordinal))
        {
            submittedSession.DraftMessage = string.Empty;
        }

        if (SelectedSession?.SessionId != sessionId)
        {
            return;
        }

        if (string.Equals(DraftMessage, draftSnapshot, StringComparison.Ordinal))
        {
            DraftMessage = string.Empty;
        }

        if (
            PendingAttachments.Select(attachment => attachment.AttachmentId)
                .SequenceEqual(attachmentIds)
        )
        {
            ClearPendingAttachments();
        }

        if (rollbackAnchorTurnId is not null && PendingRollbackTurnId == rollbackAnchorTurnId)
        {
            ClearRollbackStateOnly();
        }
    }

    private bool IsSelectedSessionSendPending() =>
        SelectedSession is not null && IsSendPending(SelectedSession.SessionId);

    private bool IsSendPending(Guid sessionId)
    {
        lock (_pendingSendSync)
        {
            return _pendingSendSessionIds.Contains(sessionId);
        }
    }

    private bool TryBeginPendingSend(Guid sessionId)
    {
        lock (_pendingSendSync)
        {
            if (!_pendingSendSessionIds.Add(sessionId))
            {
                return false;
            }
        }

        NotifySendPendingStateChanged(sessionId);
        return true;
    }

    private void EndPendingSend(Guid sessionId)
    {
        var removed = false;
        lock (_pendingSendSync)
        {
            removed = _pendingSendSessionIds.Remove(sessionId);
        }

        if (removed)
        {
            NotifySendPendingStateChanged(sessionId);
        }
    }

    private void NotifySendPendingStateChanged(Guid sessionId)
    {
        if (SelectedSession?.SessionId == sessionId)
        {
            NotifySelectedSessionRunStateChanged();
        }
    }

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
        _sessionService.TranscriptReset -= OnTranscriptReset;
        _sessionService.RunActivityChanged -= OnRunActivityChanged;
        _activityQuietTimer.Stop();
        _activityQuietTimer.Tick -= OnActivityQuietTimerTick;
        _workspaceWarmupCts?.Cancel();
        _workspaceWarmupCts?.Dispose();
        CancelPendingTranscriptRefresh();
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

}

public sealed record AgentWorkspacePathChipViewModel(string Label);
