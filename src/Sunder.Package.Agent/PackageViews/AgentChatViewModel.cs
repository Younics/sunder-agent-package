using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Shared.PackageViews;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel : ObservableObject, IDisposable
{
    private const int InitialTranscriptTurnLimit = 45;
    private const int OlderTranscriptTurnPageSize = 30;
    private const int TranscriptVisibleRowLimit = 60;
    private const string SubsessionsViewId = "sunder.package.agent.subagents.sessions";
    private const string SubsessionNavigationSessionIdKey = "sessionId";
    private readonly IAgentProfileGateway _profileService;
    private readonly IAgentWorkspaceGateway _workspaceService;
    private readonly IAgentSessionGateway _sessionService;
    private readonly IAgentTurnMutationGateway? _turnMutationGateway;
    private readonly IAgentPermissionGateway _permissionService;
    private readonly IAgentAttachmentGateway? _attachmentService;
    private readonly IAgentRunGateway _runCoordinator;
    private readonly IAgentExecutionGateway? _warmupService;
    private readonly AgentChatSelectionStateService? _selectionState;
    private readonly AgentToolPresentationService _toolPresentationService;
    private readonly IPackageShellViewService? _shellViewService;
    private readonly TranscriptTimelineState<AgentTranscriptRowViewModel> _timeline;
    private readonly ActivityTicker _activityTicker = new();
    private readonly AgentRunActivityState _runActivity;
    private readonly AgentComposerState _composer = new();
    private readonly AgentPermissionPanelState _permissionPanel;
    private readonly IAgentRuntimeAvailability? _runtimeAvailability;
    private readonly IAgentChatSnapshotGateway? _chatSnapshotGateway;
    private readonly IAgentTranscriptPageGateway? _transcriptPageGateway;
    private readonly IAgentChatSessionCommandGateway? _chatSessionCommandGateway;
    private readonly IAgentChatPermissionCommandGateway? _chatPermissionCommandGateway;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly AsyncOnce _initialization = new();
    private readonly PresentationTaskScope _backgroundTasks = new();
    private readonly SemaphoreSlim _selectionPersistenceGate = new(1, 1);
    private readonly Dictionary<Guid, AgentSessionSnapshot> _snapshotSessions = [];
    private readonly Dictionary<Guid, string> _sessionDrafts = [];
    private AgentSessionListItemViewModel? _observedSelectedSession;
    private readonly object _chatSnapshotRequestLock = new();
    private CancellationTokenSource? _chatSnapshotRequestCancellation;
    private string _globalStatusText = string.Empty;
    private bool _isReconcilingSessionSelection;
    private bool _isRestoringReconciledSessionSelection;
    private bool _suppressWorkspaceSelection;
    private int _chatSnapshotRequestGeneration;
    private bool _isApplyingChatSnapshot;
    private bool _isInitialized;
    private bool _hasStartupError;
    private bool _disposed;

    public AgentChatViewModel(
        IAgentProfileGateway profileService,
        IAgentWorkspaceGateway workspaceService,
        IAgentSessionGateway sessionService,
        IAgentPermissionGateway permissionService,
        IAgentRunGateway runCoordinator,
        AgentChatSelectionStateService? selectionState = null,
        AgentToolPresentationService? toolPresentationService = null,
        TimeSpan? activityQuietDelay = null,
        IAgentExecutionGateway? warmupService = null,
        IPackageShellViewService? shellViewService = null,
        IAgentAttachmentGateway? attachmentService = null
    )
    {
        _activityTicker.SetEnabled(false);
        _profileService = profileService;
        _workspaceService = workspaceService;
        _sessionService = sessionService;
        _turnMutationGateway = sessionService as IAgentTurnMutationGateway;
        _permissionService = permissionService;
        _attachmentService = attachmentService;
        _runCoordinator = runCoordinator;
        _warmupService = warmupService;
        _selectionState = selectionState;
        _toolPresentationService = toolPresentationService ?? new AgentToolPresentationService();
        _shellViewService = shellViewService;
        _permissionPanel = new AgentPermissionPanelState(permissionService, runCoordinator);
        _runtimeAvailability = profileService as IAgentRuntimeAvailability;
        _chatSnapshotGateway = profileService as IAgentChatSnapshotGateway;
        _transcriptPageGateway = sessionService as IAgentTranscriptPageGateway;
        _chatSessionCommandGateway = sessionService as IAgentChatSessionCommandGateway;
        _chatPermissionCommandGateway = permissionService as IAgentChatPermissionCommandGateway;
        if (_chatSnapshotGateway is not null)
        {
            _chatSnapshotGateway.ChatSnapshotReloaded += OnChatSnapshotReloaded;
        }
        if (_runtimeAvailability is not null)
        {
            _runtimeAvailability.ConnectionStateChanged += OnRuntimeConnectionStateChanged;
        }
        var rowFactory = new AgentTranscriptRowFactory(
            _toolPresentationService,
            _activityTicker,
            ResolveTurnSenderDisplayName,
            ResolveChildSessionLinksFromStore);
        var rowProjector = new TranscriptRowProjector<AgentTranscriptRowViewModel>(
            Messages,
            rowFactory,
            TranscriptVisibleRowLimit * 2);
        _timeline = new TranscriptTimelineState<AgentTranscriptRowViewModel>(
            rowProjector,
            InitialTranscriptTurnLimit,
            OlderTranscriptTurnPageSize,
            TranscriptVisibleRowLimit);
        _runActivity = new AgentRunActivityState(
            () => IsDisplayedSessionRunActive,
            () => _timeline.IsFollowingLatest,
            activityQuietDelay);
        _timeline.RowsChanging += isPageApplication => TranscriptChanging?.Invoke(isPageApplication);
        _timeline.RowsChanged += () => TranscriptChanged?.Invoke();
        _timeline.PropertyChanged += OnTimelinePropertyChanged;
        _timeline.TurnProjected += OnTimelineTurnProjected;
        _runActivity.Changed += OnRunActivityStateChanged;
        PendingAttachments.CollectionChanged += OnPendingAttachmentsChanged;
        _profileService.ProfileChanged += OnProfilesChanged;
        _workspaceService.WorkspacesChanged += OnWorkspacesChanged;
        _sessionService.SessionChanged += OnSessionChanged;
        if (_turnMutationGateway is not null)
        {
            _turnMutationGateway.TurnMutated += OnTurnMutated;
        }
        else
        {
            _sessionService.TurnChanged += OnTurnChanged;
        }
        _sessionService.TranscriptReset += OnTranscriptReset;
        _sessionService.RunActivityChanged += OnRunActivityChanged;
    }

    public ObservableCollection<AgentWorkspaceRecord> Workspaces { get; } = [];

    public ObservableCollection<AgentProfileRecord> Profiles { get; } = [];

    public ObservableCollection<AgentSessionListItemViewModel> Sessions { get; } = [];

    public ObservableCollection<AgentWorkspacePathChipViewModel> WideWorkspacePathChips { get; } = [];

    public ObservableCollection<AgentWorkspacePathChipViewModel> NarrowWorkspacePathChips { get; } = [];

    public ObservableCollection<AgentTranscriptRowViewModel> Messages { get; } = [];

    public IReadOnlyList<string> WorkspacePathChipLabels => _workspacePathChipLabels;

    public ObservableCollection<AgentPendingPermissionRequestRecord> PendingPermissionRequests
        => _permissionPanel.Requests;

    public event Action? TranscriptChanged;

    public event Action<bool>? TranscriptChanging;

    public event Action<Guid>? TranscriptTailFollowRequested;

    internal bool IsInitialized => _isInitialized;

    internal Guid? DisplayedTranscriptSessionId => DisplayedSession?.SessionId;

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
        _timeline.CanLoadOlder && DisplayedSession is not null;

    public bool CanLoadNewerTranscriptRows =>
        _timeline.CanLoadNewer && DisplayedSession is not null;

    public bool HasOlderTranscriptRows => _timeline.HasOlderRows;

    public bool HasNewerTranscriptRows => _timeline.HasNewerRows;

    public bool IsLoadingOlderTranscriptRows => _timeline.IsLoadingOlder;

    public bool IsLoadingNewerTranscriptRows => _timeline.IsLoadingNewer;

    public bool IsTranscriptLoading => _timeline.IsInitialLoading;

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

    public string DraftMessage
    {
        get => _composer.Text;
        set
        {
            if (string.Equals(_composer.Text, value, StringComparison.Ordinal))
            {
                return;
            }

            _composer.Text = value;
            OnPropertyChanged();
            OnDraftMessageChanged(value);
        }
    }

    public Guid? PendingRollbackTurnId
    {
        get => _composer.RollbackTurnId;
        set
        {
            if (_composer.RollbackTurnId == value)
            {
                return;
            }

            _composer.RollbackTurnId = value;
            OnPropertyChanged();
            OnPendingRollbackTurnIdChanged(value);
        }
    }

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _setupTitle = "Create a workspace before chatting";

    [ObservableProperty]
    private string _setupDescription =
        "Create an Agent, select a workspace and session, then start chatting.";

    [ObservableProperty]
    private bool _isSendOnEnterEnabled = true;

    [ObservableProperty]
    private bool _isComposerExpanded;

    partial void OnIsComposerExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsComposerCollapsed));
        OnPropertyChanged(nameof(ShowTranscriptSurface));
        OnPropertyChanged(nameof(ShowCollapsedComposer));
        OnPropertyChanged(nameof(ShowExpandedComposer));
    }

    private void OnDraftMessageChanged(string value)
    {
        if (
            SelectedSession is not null
            && !string.Equals(SelectedSession.DraftMessage, value, StringComparison.Ordinal)
        )
        {
            SelectedSession.DraftMessage = value;
        }

        if (SelectedSession is not null)
        {
            _sessionDrafts[SelectedSession.SessionId] = value;
        }

        SendMessageCommand.NotifyCanExecuteChanged();
        ClearComposerCommand.NotifyCanExecuteChanged();
    }

    private void OnPendingRollbackTurnIdChanged(Guid? value)
    {
        OnPropertyChanged(nameof(IsRollbackPending));
        OnPropertyChanged(nameof(ClearComposerButtonText));
        OnPropertyChanged(nameof(ClearComposerToolTipText));
        OnPropertyChanged(nameof(ShowSendAction));
        OnPropertyChanged(nameof(ShowStopAction));
        SendMessageCommand.NotifyCanExecuteChanged();
        ClearComposerCommand.NotifyCanExecuteChanged();
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
        var message = draftSnapshot;
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
        var chatBinding = (profile.ModelBindings ?? []).FirstOrDefault(binding => string.Equals(
            binding.CapabilityKind,
            AgentModelCapabilityKinds.Chat,
            StringComparison.OrdinalIgnoreCase));
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

        var existingTranscript = await LoadTranscriptPageAsync(
            new AgentTranscriptPageRequest(
                sessionId,
                AgentTranscriptPageDirection.Recent,
                500),
            _lifetimeCancellation.Token);
        var submission = _composer.TryBeginSubmission(
            sessionId,
            existingTranscript.Turns.Select(turn => turn.TurnId).ToHashSet());
        if (submission is null)
        {
            ApplySessionStatus(
                selectedSession,
                "A message is already being sent for this session."
            );
            return;
        }

        NotifySendPendingStateChanged(sessionId);

        try
        {
            var readiness = await _profileService.GetChatProviderReadinessAsync(
                chatBinding.ProviderId,
                _lifetimeCancellation.Token
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

            RequestTranscriptTailFollow(sessionId);
            ClearSubmittedComposerState(selectedSession, submission);

            if (submission.RollbackTurnId is { } anchorTurnId)
            {
                await _runCoordinator.RollbackAndQueueUserMessageAsync(
                    sessionId,
                    anchorTurnId,
                    profileId,
                    message,
                    workspaceId,
                    attachments,
                    _lifetimeCancellation.Token
                );
            }
            else
            {
                await _runCoordinator.QueueUserMessageAsync(
                    sessionId,
                    profileId,
                    message,
                    workspaceId,
                    attachments,
                    _lifetimeCancellation.Token
                );
            }

            await CompleteOrRestoreComposerSubmissionAsync(selectedSession, submission);

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
        catch
        {
            await CompleteOrRestoreComposerSubmissionAsync(selectedSession, submission);
            throw;
        }
        finally
        {
            EndPendingSend(submission);
        }
    }

    private bool CanSendMessage() =>
        CanUseChat
        && !IsSelectedSessionSendPending()
        && (IsSelectedSessionRunInactive || IsRollbackPending)
        && (!string.IsNullOrWhiteSpace(DraftMessage) || PendingAttachments.Count > 0);

    private void ClearSubmittedComposerState(
        AgentSessionListItemViewModel submittedSession,
        AgentComposerSubmission submission
    )
    {
        if (string.Equals(submittedSession.DraftMessage, submission.Text, StringComparison.Ordinal))
        {
            submittedSession.DraftMessage = string.Empty;
        }

        if (SelectedSession?.SessionId != submission.SessionId)
        {
            return;
        }

        _composer.ClearSubmitted(submission);
        NotifyComposerStateChanged();
    }

    private void RestoreUncommittedComposerState(
        AgentSessionListItemViewModel submittedSession,
        AgentComposerSubmission submission)
    {
        if (submission.IsCommitted)
        {
            return;
        }

        if (!string.IsNullOrEmpty(submission.Text)
            && string.IsNullOrEmpty(submittedSession.DraftMessage))
        {
            submittedSession.DraftMessage = submission.Text;
        }

        if (_composer.RestoreUncommitted(submission, SelectedSession?.SessionId))
        {
            NotifyComposerStateChanged();
        }
    }

    private async Task CompleteOrRestoreComposerSubmissionAsync(
        AgentSessionListItemViewModel submittedSession,
        AgentComposerSubmission submission)
    {
        var transcript = await LoadTranscriptPageAsync(
            new AgentTranscriptPageRequest(
                submission.SessionId,
                AgentTranscriptPageDirection.Recent,
                500),
            _lifetimeCancellation.Token);
        var wasCommitted = transcript.Turns
            .Any(turn => turn.Role == AgentMessageRole.User
                         && !submission.ExistingTurnIds.Contains(turn.TurnId));
        if (wasCommitted)
        {
            submission.Commit();
            return;
        }

        RestoreUncommittedComposerState(submittedSession, submission);
    }

    private bool IsSelectedSessionSendPending() =>
        SelectedSession is not null && IsSendPending(SelectedSession.SessionId);

    private bool IsSendPending(Guid sessionId)
        => _composer.IsSendPending(sessionId);

    private void EndPendingSend(AgentComposerSubmission submission)
    {
        if (_composer.EndSubmission(submission))
        {
            NotifySendPendingStateChanged(submission.SessionId);
        }
    }

    private void NotifySendPendingStateChanged(Guid sessionId)
    {
        if (SelectedSession?.SessionId == sessionId)
        {
            NotifySelectedSessionRunStateChanged();
        }
    }

    private void NotifyComposerStateChanged()
    {
        OnPropertyChanged(nameof(DraftMessage));
        OnPropertyChanged(nameof(PendingRollbackTurnId));
        OnPendingRollbackTurnIdChanged(PendingRollbackTurnId);
        OnDraftMessageChanged(DraftMessage);
        OnPropertyChanged(nameof(HasPendingAttachments));
        OnPropertyChanged(nameof(PendingAttachmentSummaryText));
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
        var checkpoint = await _runCoordinator.StopAsync(sessionId, _lifetimeCancellation.Token);

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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCancellation.Cancel();
        lock (_chatSnapshotRequestLock)
        {
            _chatSnapshotRequestCancellation?.Cancel();
            _chatSnapshotRequestCancellation?.Dispose();
            _chatSnapshotRequestCancellation = null;
        }
        if (_chatSnapshotGateway is not null)
        {
            _chatSnapshotGateway.ChatSnapshotReloaded -= OnChatSnapshotReloaded;
        }
        if (_runtimeAvailability is not null)
        {
            _runtimeAvailability.ConnectionStateChanged -= OnRuntimeConnectionStateChanged;
        }
        if (_observedSelectedSession is not null)
        {
            _observedSelectedSession.PropertyChanged -= OnSelectedSessionPropertyChanged;
        }

        PendingAttachments.CollectionChanged -= OnPendingAttachmentsChanged;
        _profileService.ProfileChanged -= OnProfilesChanged;
        _workspaceService.WorkspacesChanged -= OnWorkspacesChanged;
        _sessionService.SessionChanged -= OnSessionChanged;
        if (_turnMutationGateway is not null)
        {
            _turnMutationGateway.TurnMutated -= OnTurnMutated;
        }
        else
        {
            _sessionService.TurnChanged -= OnTurnChanged;
        }
        _sessionService.TranscriptReset -= OnTranscriptReset;
        _sessionService.RunActivityChanged -= OnRunActivityChanged;
        _workspaceWarmupCts?.Cancel();
        _workspaceWarmupCts?.Dispose();
        _runActivity.Changed -= OnRunActivityStateChanged;
        _runActivity.Dispose();
        _timeline.PropertyChanged -= OnTimelinePropertyChanged;
        _timeline.TurnProjected -= OnTimelineTurnProjected;
        _timeline.Dispose();
        _activityTicker.Dispose();
        DisposeAttachmentPreviewImage();
        _backgroundTasks.Dispose();
        _initialization.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private void OnRuntimeConnectionStateChanged(AgentRuntimeConnectionState state)
        => RunOnUiThread(() =>
        {
            if (state is AgentRuntimeConnectionState.Unavailable or AgentRuntimeConnectionState.Reconnecting)
            {
                SetGlobalStatus("Agent Runtime is unavailable. Reconnecting...");
            }
            else if (state == AgentRuntimeConnectionState.Connected)
            {
                if (string.Equals(_globalStatusText, "Agent Runtime is unavailable. Reconnecting...", StringComparison.Ordinal))
                {
                    _globalStatusText = string.Empty;
                }
                RefreshSetupState();
            }
        });

    internal void ReportPresentationFailure(Exception exception)
        => RunOnUiThread(() =>
        {
            if (!_disposed)
            {
                SetGlobalStatus(exception.Message);
            }
        });

    internal void ReportStartupFailure()
        => RunOnUiThread(() =>
        {
            if (_disposed)
            {
                return;
            }

            _hasStartupError = true;
            SetGlobalStatus("Unable to load Agent Chat. Navigate away and return to retry.");
            RefreshSetupState();
        });

    private void RunOnUiThread(Action action)
    {
        _backgroundTasks.Run(_ => InvokeOnUiThreadAsync(action));
    }

    private static async Task InvokeOnUiThreadAsync(Action action)
    {
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Background);
    }

    private void TrackBackgroundTask(Task? task)
    {
        if (task is not null)
        {
            _backgroundTasks.Run(task);
        }
    }

}

public sealed record AgentWorkspacePathChipViewModel(string Label);
