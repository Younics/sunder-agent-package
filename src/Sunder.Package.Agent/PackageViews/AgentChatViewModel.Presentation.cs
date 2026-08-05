using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Shared.PackageViews;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel
{
    private readonly IPresentationDispatcher _uiDispatcher;

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
        IAgentAttachmentGateway? attachmentService = null)
        : this(
            profileService,
            workspaceService,
            sessionService,
            permissionService,
            runCoordinator,
            PresentationDispatcher.Capture(),
            selectionState,
            toolPresentationService,
            activityQuietDelay,
            warmupService,
            shellViewService,
            attachmentService)
    {
    }

    internal AgentChatViewModel(
        IAgentProfileGateway profileService,
        IAgentWorkspaceGateway workspaceService,
        IAgentSessionGateway sessionService,
        IAgentPermissionGateway permissionService,
        IAgentRunGateway runCoordinator,
        IPresentationDispatcher uiDispatcher,
        AgentChatSelectionStateService? selectionState = null,
        AgentToolPresentationService? toolPresentationService = null,
        TimeSpan? activityQuietDelay = null,
        IAgentExecutionGateway? warmupService = null,
        IPackageShellViewService? shellViewService = null,
        IAgentAttachmentGateway? attachmentService = null)
    {
        _activityTicker.SetEnabled(false);
        _profileService = profileService;
        _workspaceService = workspaceService;
        _sessionService = sessionService;
        _turnMutationGateway = sessionService as IAgentTurnMutationGateway;
        _permissionService = permissionService;
        _attachmentService = attachmentService;
        _runCoordinator = runCoordinator;
        _correlatedRunCoordinator = runCoordinator as IAgentCorrelatedRunGateway;
        _runCommandStatusGateway = runCoordinator as IAgentRunCommandStatusGateway;
        _warmupService = warmupService;
        _selectionState = selectionState;
        _toolPresentationService = toolPresentationService ?? new AgentToolPresentationService();
        _shellViewService = shellViewService;
        _uiDispatcher = uiDispatcher;
        _permissionPanel = new AgentPermissionPanelState(permissionService, runCoordinator, uiDispatcher);
        _runtimeAvailability = profileService as IAgentRuntimeAvailability;
        _runtimeFailureClassifier = profileService as IAgentRuntimeFailureClassifier;
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
            LoadToolDetailAsync,
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
        RunActivityRow = new AgentActivityTranscriptRowViewModel(_activityTicker);
        TailSentinelRow = new AgentTranscriptTailSentinelRowViewModel();
        _transcriptItemsProjection = new TranscriptItemsProjection<AgentTranscriptRowViewModel>(
            Messages,
            RunActivityRow,
            TailSentinelRow);
        TranscriptItems = _transcriptItemsProjection.Items;
        _runActivity = new AgentRunActivityState(
            () => IsDisplayedSessionRunActive,
            () => _timeline.IsFollowingLatest,
            activityQuietDelay);
        _timeline.RowsChanging += OnTimelineRowsChanging;
        _timeline.RowsChanged += OnTimelineRowsChanged;
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

    private void RunOnUiThread(Action action)
    {
        _backgroundTasks.Run(_ => InvokeOnUiThreadAsync(action));
    }

    private Task InvokeOnUiThreadAsync(Action action) => _uiDispatcher.InvokeAsync(action);

    private void TrackBackgroundTask(Task? task)
    {
        if (task is not null)
        {
            _backgroundTasks.Run(task);
        }
    }
}
