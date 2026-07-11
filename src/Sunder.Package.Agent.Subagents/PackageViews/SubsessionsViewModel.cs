using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.PackageViews;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia.Theming;

namespace Sunder.Package.Agent.Subagents.PackageViews;

public sealed partial class SubsessionsViewModel : ObservableObject, IDisposable, IPackageViewNavigationTarget
{
    private const int InitialTranscriptTurnLimit = 60;
    private const int OlderTranscriptTurnPageSize = 30;
    private const int TranscriptVisibleRowLimit = 60;

    private readonly IPackageExtensionCatalog? _extensionCatalog;
    private readonly TranscriptTimelineState<SubsessionTranscriptRowViewModel> _timeline;
    private readonly AgentRunActivityState _runActivity;
    private readonly Task _initialization;
    private IAgentRuntimeCatalog? _runtimeCatalog;
    private bool _isReconcilingSubsessionSelection;
    private bool _isRestoringReconciledSubsessionSelection;
    private bool _disposed;

    public SubsessionsViewModel(
        IPackageExtensionCatalog extensionCatalog,
        TimeSpan? activityQuietDelay = null)
        : this(extensionCatalog, activityQuietDelay, initialize: true)
    {
    }

    public SubsessionsViewModel()
        : this(null, null, initialize: true)
    {
    }

    private SubsessionsViewModel(
        IPackageExtensionCatalog? extensionCatalog,
        TimeSpan? activityQuietDelay,
        bool initialize)
    {
        _extensionCatalog = extensionCatalog;
        var toolPresentation = new TranscriptToolPresentationService(() =>
            extensionCatalog?.GetExtensions(PackageExtensionPoints.ToolSources)
                .OfType<IAgentToolPresentationResolver>()
            ?? []);
        var rowFactory = new SubsessionTranscriptRowFactory(
            toolPresentation,
            ResolveChildSessionLinksFromRuntime);
        var rowProjector = new TranscriptRowProjector<SubsessionTranscriptRowViewModel>(
            Messages,
            rowFactory,
            TranscriptVisibleRowLimit * 2);
        _timeline = new TranscriptTimelineState<SubsessionTranscriptRowViewModel>(
            rowProjector,
            InitialTranscriptTurnLimit,
            OlderTranscriptTurnPageSize,
            TranscriptVisibleRowLimit);
        _runActivity = new AgentRunActivityState(
            () => IsSelectedSubsessionRunActive,
            () => _timeline.IsFollowingLatest,
            activityQuietDelay);
        _timeline.RowsChanging += () => TranscriptChanging?.Invoke();
        _timeline.RowsChanged += OnTimelineRowsChanged;
        _timeline.PropertyChanged += OnTimelinePropertyChanged;
        _timeline.TurnProjected += OnTimelineTurnProjected;
        _runActivity.Changed += OnRunActivityStateChanged;
        _initialization = initialize ? InitializeCoreAsync() : Task.CompletedTask;
    }

    public ObservableCollection<SubsessionListItemViewModel> Subsessions { get; } = [];

    public ObservableCollection<SubsessionTranscriptRowViewModel> Messages { get; } = [];

    public event Action? TranscriptChanged;

    public event Action? TranscriptChanging;

    public bool IsListActive => !IsDetailActive;

    public bool ShowWideLayout => !IsCompactLayout;

    public bool ShowCompactList => IsCompactLayout && IsListActive;

    public bool ShowCompactDetail => IsCompactLayout && IsDetailActive;

    public bool ShowListPane => ShowWideLayout || ShowCompactList;

    public bool ShowDetailPane => ShowWideLayout || ShowCompactDetail;

    public bool HasSelectedSubsession => SelectedSubsession is not null;

    public bool HasNoSubsessions => Subsessions.Count == 0;

    public bool HasTranscriptRows => Messages.Count > 0;

    public bool ShowEmptyTranscript => HasSelectedSubsession && !HasTranscriptRows;

    public bool HasOlderTranscriptRows => _timeline.HasOlderRows;

    public bool HasNewerTranscriptRows => _timeline.HasNewerRows;

    public bool IsLoadingOlderTranscriptRows => _timeline.IsLoadingOlder;

    public bool IsLoadingNewerTranscriptRows => _timeline.IsLoadingNewer;

    public bool IsTranscriptLoading => _timeline.IsInitialLoading;

    public bool CanLoadOlderTranscriptRows => _timeline.CanLoadOlder && SelectedSubsession is not null;

    public bool CanLoadNewerTranscriptRows => _timeline.CanLoadNewer && SelectedSubsession is not null;

    public bool IsSelectedSubsessionRunActive => SelectedSubsession?.IsRunActive == true;

    [ObservableProperty]
    private SubsessionListItemViewModel? _selectedSubsession;

    [ObservableProperty]
    private bool _isCompactLayout;

    [ObservableProperty]
    private bool _isDetailActive;

    [ObservableProperty]
    private string _statusText = string.Empty;

    partial void OnSelectedSubsessionChanged(SubsessionListItemViewModel? value)
    {
        if (_isReconcilingSubsessionSelection)
        {
            return;
        }

        OnPropertyChanged(nameof(HasSelectedSubsession));
        OnPropertyChanged(nameof(IsSelectedSubsessionRunActive));
        if (!_isRestoringReconciledSubsessionSelection)
        {
            LoadTranscript(value?.SessionId);
        }

        if (IsCompactLayout && value is not null)
        {
            IsDetailActive = true;
        }
    }

    partial void OnIsCompactLayoutChanged(bool value)
    {
        if (value && !IsDetailActive)
        {
            SelectedSubsession = null;
        }
        else if (!value && SelectedSubsession is null)
        {
            SelectedSubsession = Subsessions.FirstOrDefault();
        }

        OnPropertyChanged(nameof(ShowWideLayout));
        OnPropertyChanged(nameof(ShowCompactList));
        OnPropertyChanged(nameof(ShowCompactDetail));
        OnPropertyChanged(nameof(ShowListPane));
        OnPropertyChanged(nameof(ShowDetailPane));
    }

    partial void OnIsDetailActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(IsListActive));
        OnPropertyChanged(nameof(ShowCompactList));
        OnPropertyChanged(nameof(ShowCompactDetail));
        OnPropertyChanged(nameof(ShowListPane));
        OnPropertyChanged(nameof(ShowDetailPane));
    }

    public ValueTask OnNavigatedToAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sessionId = TryGetSessionId(context.Parameters);
        if (sessionId is not null
            && SelectedSubsession?.SessionId == sessionId.Value
            && IsDetailActive)
        {
            return ValueTask.CompletedTask;
        }

        ReloadSubsessions(sessionId);
        if (sessionId is not null)
        {
            IsDetailActive = true;
        }

        return ValueTask.CompletedTask;
    }

    [RelayCommand]
    private void BackToSubsessionsList()
    {
        if (IsCompactLayout)
        {
            SelectedSubsession = null;
        }

        IsDetailActive = false;
    }

    [RelayCommand]
    private void OpenSubsession(SubsessionListItemViewModel? subsession)
    {
        if (subsession is not null)
        {
            ActivateSubsession(subsession);
        }
    }

    public void ActivateSubsession(SubsessionListItemViewModel subsession)
    {
        if (SelectedSubsession?.SessionId != subsession.SessionId)
        {
            SelectedSubsession = subsession;
        }

        if (IsCompactLayout)
        {
            IsDetailActive = true;
        }
    }

    [RelayCommand]
    private void OpenChildSession(SubsessionChildSessionLinkViewModel? childSession)
    {
        if (childSession is null
            || SelectedSubsession?.SessionId == childSession.SessionId && IsDetailActive)
        {
            return;
        }

        ReloadSubsessions(childSession.SessionId);
        IsDetailActive = true;
    }

    public async Task<bool> LoadOlderTranscriptRowsAsync(object? protectedAnchorKey = null)
    {
        var runtime = _runtimeCatalog;
        if (runtime is null)
        {
            return false;
        }

        var loaded = await _timeline.LoadOlderAsync(
            (sessionId, beforeCreatedAt, beforeTurnId, limit, cancellationToken) => Task.Run(
                () => runtime.ListTurnsBefore(
                    sessionId,
                    beforeCreatedAt,
                    beforeTurnId,
                    limit),
                cancellationToken),
            protectedAnchorKey);
        if (loaded)
        {
            ApplyRunActivityState();
        }

        return loaded;
    }

    public async Task<bool> LoadNewerTranscriptRowsAsync(object? protectedAnchorKey = null)
    {
        var runtime = _runtimeCatalog;
        if (runtime is null)
        {
            return false;
        }

        var loaded = await _timeline.LoadNewerAsync(
            (sessionId, afterCreatedAt, afterTurnId, limit, cancellationToken) => Task.Run(
                () => runtime.ListTurnsAfter(
                    sessionId,
                    afterCreatedAt,
                    afterTurnId,
                    limit),
                cancellationToken),
            protectedAnchorKey);
        if (loaded)
        {
            _runActivity.NotifyFollowStateChanged();
            ApplyRunActivityState();
        }

        return loaded;
    }

    [RelayCommand]
    private void JumpToLatestTranscript()
    {
        if (_timeline.RequestJumpToLatest())
        {
            LoadTranscript(SelectedSubsession?.SessionId);
        }
    }

    public void DetachTranscriptFromLatest()
    {
        _timeline.DetachFromLatest();
        _runActivity.NotifyFollowStateChanged();
    }

    public void ResumeTranscriptFollowingLatestIfCaughtUp()
    {
        if (_timeline.ResumeFollowingLatestIfCaughtUp())
        {
            _runActivity.NotifyFollowStateChanged();
            ApplyRunActivityState();
            _timeline.NotifyRowsChanged();
        }
    }

    internal void SetTranscriptJumpToLatestVisible(bool isVisible)
        => _timeline.SetJumpToLatestVisible(isVisible);

    internal void SetTranscriptViewportAnchor(TranscriptViewportAnchorData? anchor)
        => _timeline.SetViewportAnchor(anchor);

    internal void SetTranscriptRowExpanded(
        SubsessionTranscriptRowViewModel row,
        bool isExpanded)
        => _timeline.SetRowExpanded(row, isExpanded);

    private void EnsureRuntimeCatalog()
    {
        if (_runtimeCatalog is not null || _extensionCatalog is null)
        {
            return;
        }

        _runtimeCatalog = _extensionCatalog
            .GetExtensions(PackageExtensionPoints.RuntimeCatalogs)
            .FirstOrDefault();
        if (_runtimeCatalog is not null)
        {
            _runtimeCatalog.SessionChanged += OnSessionChanged;
            _runtimeCatalog.TurnChanged += OnTurnChanged;
        }
    }

    private void ReloadSubsessions(Guid? selectedSessionId)
    {
        EnsureRuntimeCatalog();
        var runtime = _runtimeCatalog;
        if (runtime is null)
        {
            StatusText = "The Agent runtime is not available.";
            return;
        }

        var currentSelectionId = selectedSessionId ?? SelectedSubsession?.SessionId;
        var subsessions = runtime.ListSessions()
            .Where(session => session.ParentSessionId is not null)
            .OrderByDescending(session => session.UpdatedAtUtc)
            .ThenByDescending(session => session.CreatedAtUtc)
            .ToArray();
        ReconcileSubsessions(runtime, subsessions);

        OnPropertyChanged(nameof(HasNoSubsessions));
        var selectedSubsession = Subsessions.FirstOrDefault(
            session => session.SessionId == currentSelectionId);
        if (selectedSubsession is null && (!IsCompactLayout || selectedSessionId is not null))
        {
            selectedSubsession = Subsessions.FirstOrDefault();
        }

        SelectedSubsession = selectedSubsession;
        OnPropertyChanged(nameof(IsSelectedSubsessionRunActive));
        StatusText = Subsessions.Count == 0
            ? "No sub-sessions have been created yet."
            : $"{Subsessions.Count} sub-session(s).";
        ApplyRunActivityState();
    }

    private void ReconcileSubsessions(
        IAgentRuntimeCatalog runtime,
        IReadOnlyList<AgentSessionRecord> sessions)
    {
        var desiredSessionIds = sessions.Select(session => session.SessionId).ToHashSet();
        var selectedSessionId = SelectedSubsession?.SessionId;
        var shouldPreserveSelection = selectedSessionId is not null
            && desiredSessionIds.Contains(selectedSessionId.Value);

        _isReconcilingSubsessionSelection = shouldPreserveSelection;
        try
        {
            for (var index = Subsessions.Count - 1; index >= 0; index--)
            {
                if (!desiredSessionIds.Contains(Subsessions[index].SessionId))
                {
                    Subsessions.RemoveAt(index);
                }
            }

            for (var index = 0; index < sessions.Count; index++)
            {
                var session = sessions[index];
                var existingIndex = FindSubsessionIndex(session.SessionId);
                var subtitle = BuildSubtitle(runtime, session);
                var checkpoint = runtime.GetLatestCheckpoint(session.SessionId);
                if (existingIndex < 0)
                {
                    Subsessions.Insert(
                        index,
                        new SubsessionListItemViewModel(session, subtitle, checkpoint));
                    continue;
                }

                var item = Subsessions[existingIndex];
                item.UpdateSession(session, subtitle);
                item.ApplyCheckpoint(checkpoint);
                if (existingIndex != index)
                {
                    Subsessions.Move(existingIndex, index);
                }
            }
        }
        finally
        {
            _isReconcilingSubsessionSelection = false;
        }

        RestoreReconciledSubsessionSelection(selectedSessionId, shouldPreserveSelection);
    }

    private void RestoreReconciledSubsessionSelection(
        Guid? sessionId,
        bool shouldPreserveSession)
    {
        if (!shouldPreserveSession
            || sessionId is null
            || SelectedSubsession?.SessionId == sessionId.Value
            || FindSubsessionItem(sessionId.Value) is not { } session)
        {
            return;
        }

        _isRestoringReconciledSubsessionSelection = true;
        try
        {
            SelectedSubsession = session;
        }
        finally
        {
            _isRestoringReconciledSubsessionSelection = false;
        }
    }

    private int FindSubsessionIndex(Guid sessionId)
    {
        for (var index = 0; index < Subsessions.Count; index++)
        {
            if (Subsessions[index].SessionId == sessionId)
            {
                return index;
            }
        }

        return -1;
    }

    private SubsessionListItemViewModel? FindSubsessionItem(Guid sessionId)
    {
        var index = FindSubsessionIndex(sessionId);
        return index < 0 ? null : Subsessions[index];
    }

    private static string BuildSubtitle(IAgentRuntimeCatalog runtime, AgentSessionRecord session)
    {
        var parentTitle = session.ParentSessionId is { } parentSessionId
            ? runtime.GetSession(parentSessionId)?.Title
            : null;
        var profileName = string.IsNullOrWhiteSpace(session.ProfileId)
            ? null
            : runtime.GetProfile(session.ProfileId)?.DisplayName;
        var subtitle = string.Join(
            " · ",
            new[] { FormatAgentKind(profileName, session.AgentKind), parentTitle }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(subtitle) ? "Subsession" : subtitle;
    }

    private static string? FormatAgentKind(string? profileName, string? agentKind)
    {
        var name = string.IsNullOrWhiteSpace(profileName) ? agentKind : profileName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return string.Equals(agentKind, "subagent", StringComparison.OrdinalIgnoreCase)
               && !name.EndsWith("subagent", StringComparison.OrdinalIgnoreCase)
            ? $"{name} subagent"
            : name;
    }

    private void LoadTranscript(Guid? sessionId)
    {
        _runActivity.Reset();
        if (_runtimeCatalog is null || sessionId is null)
        {
            _timeline.ClearSession();
            return;
        }

        var ticket = _timeline.BeginInitialLoad(sessionId.Value);
        if (Application.Current is null)
        {
            CompleteTranscriptLoad(
                ticket,
                _runtimeCatalog.ListRecentTurns(
                    sessionId.Value,
                    InitialTranscriptTurnLimit + 1));
            return;
        }

        _ = LoadTranscriptAsync(ticket);
    }

    private async Task LoadTranscriptAsync(TranscriptLoadTicket ticket)
    {
        try
        {
            var runtime = _runtimeCatalog;
            if (runtime is null)
            {
                _timeline.TryFailInitialLoad(ticket);
                return;
            }

            var turns = await Task.Run(
                () => runtime.ListRecentTurns(ticket.SessionId, InitialTranscriptTurnLimit + 1),
                ticket.Generation.CancellationToken);
            await Dispatcher.UIThread.InvokeAsync(
                () => CompleteTranscriptLoad(ticket, turns),
                DispatcherPriority.Background);
        }
        catch (OperationCanceledException) when (ticket.Generation.CancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(
                () => _timeline.TryFailInitialLoad(ticket),
                DispatcherPriority.Background);
        }
    }

    private void CompleteTranscriptLoad(
        TranscriptLoadTicket ticket,
        IReadOnlyList<AgentTurnRecord> turns)
    {
        if (!_timeline.TryCompleteInitialLoad(ticket, turns))
        {
            return;
        }

        _runActivity.TrackCheckpoint(_runtimeCatalog?.GetLatestCheckpoint(ticket.SessionId));
        ApplyRunActivityState();
    }

    private IReadOnlyList<SubsessionChildSessionLinkViewModel> ResolveChildSessionLinksFromRuntime(
        AgentTurnRecord turn,
        AgentTurnItemRecord item)
    {
        var runtime = _runtimeCatalog;
        if (runtime is null
            || !IsSubagentTool(item.ToolId)
            || string.IsNullOrWhiteSpace(item.CallId))
        {
            return [];
        }

        return runtime.ListSessions()
            .Where(session => session.ParentSessionId == turn.SessionId
                              && string.Equals(
                                  session.ParentToolCallId,
                                  item.CallId,
                                  StringComparison.Ordinal))
            .OrderBy(session => session.CreatedAtUtc)
            .Select(session =>
            {
                var profileName = string.IsNullOrWhiteSpace(session.ProfileId)
                    ? null
                    : runtime.GetProfile(session.ProfileId)?.DisplayName;
                return new SubsessionChildSessionLinkViewModel(
                    session.SessionId,
                    session.Title,
                    FormatAgentKind(profileName, session.AgentKind) ?? "Subsession",
                    runtime.GetLatestCheckpoint(session.SessionId)?.Status
                    ?? AgentRunStatus.Idle);
            })
            .ToArray();
    }

    private static bool IsSubagentTool(string? toolId)
        => string.Equals(toolId, SubagentConstants.TaskToolId, StringComparison.OrdinalIgnoreCase)
           || string.Equals(
               toolId,
               SubagentConstants.DelegateTasksToolId,
               StringComparison.OrdinalIgnoreCase);

    private void OnSessionChanged(Guid sessionId)
        => RunOnUiThread(() => ApplySessionChanged(sessionId));

    private void ApplySessionChanged(Guid sessionId)
    {
        if (_disposed)
        {
            return;
        }

        var selectedId = SelectedSubsession?.SessionId;
        ReloadSubsessions(selectedId);
        if (SelectedSubsession?.SessionId == sessionId)
        {
            _runActivity.TrackCheckpoint(_runtimeCatalog?.GetLatestCheckpoint(sessionId));
            ApplyRunActivityState();
            _timeline.NotifyRowsChanged();
        }

        _timeline.Projector.RefreshRelatedRows();
    }

    private void OnTurnChanged(Guid sessionId, AgentTurnRecord turn)
        => RunOnUiThread(() =>
        {
            if (!_disposed && SelectedSubsession?.SessionId == sessionId)
            {
                _timeline.ApplyLiveTurn(turn);
                ApplyRunActivityState();
            }
        });

    private void OnTimelineTurnProjected(
        AgentTurnRecord turn,
        bool trackRunActivity,
        bool scheduleQuietTimer)
    {
        if (trackRunActivity)
        {
            _runActivity.TrackTurn(turn, scheduleQuietTimer);
        }
    }

    private void OnRunActivityStateChanged()
    {
        ApplyRunActivityState();
        _timeline.NotifyRowsChanged();
    }

    private void ApplyRunActivityState()
        => _timeline.ApplyActivity(
            _runActivity.Text,
            _runActivity.IsReasoning,
            _runActivity.ShouldShow);

    private void OnTimelineRowsChanged()
    {
        NotifyTranscriptStateChanged();
        TranscriptChanged?.Invoke();
    }

    private void OnTimelinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TranscriptTimelineState<SubsessionTranscriptRowViewModel>.HasOlderRows):
                OnPropertyChanged(nameof(HasOlderTranscriptRows));
                break;
            case nameof(TranscriptTimelineState<SubsessionTranscriptRowViewModel>.HasNewerRows):
                OnPropertyChanged(nameof(HasNewerTranscriptRows));
                break;
            case nameof(TranscriptTimelineState<SubsessionTranscriptRowViewModel>.IsLoadingOlder):
                OnPropertyChanged(nameof(IsLoadingOlderTranscriptRows));
                break;
            case nameof(TranscriptTimelineState<SubsessionTranscriptRowViewModel>.IsLoadingNewer):
                OnPropertyChanged(nameof(IsLoadingNewerTranscriptRows));
                break;
            case nameof(TranscriptTimelineState<SubsessionTranscriptRowViewModel>.IsInitialLoading):
                OnPropertyChanged(nameof(IsTranscriptLoading));
                break;
        }

        OnPropertyChanged(nameof(CanLoadOlderTranscriptRows));
        OnPropertyChanged(nameof(CanLoadNewerTranscriptRows));
    }

    private void NotifyTranscriptStateChanged()
    {
        OnPropertyChanged(nameof(HasTranscriptRows));
        OnPropertyChanged(nameof(ShowEmptyTranscript));
        OnPropertyChanged(nameof(CanLoadOlderTranscriptRows));
        OnPropertyChanged(nameof(CanLoadNewerTranscriptRows));
    }

    private static void RunOnUiThread(Action action)
    {
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
    }

    private static Guid? TryGetSessionId(IReadOnlyDictionary<string, string?> parameters)
        => parameters.TryGetValue(
               SubagentConstants.SubsessionNavigationSessionIdKey,
               out var value)
           && Guid.TryParse(value, out var sessionId)
            ? sessionId
            : null;
}

public sealed partial class SubsessionListItemViewModel : ObservableObject
{
    private AgentSessionRecord _session;

    public SubsessionListItemViewModel(
        AgentSessionRecord session,
        string subtitle,
        AgentRunCheckpointRecord? checkpoint)
    {
        _session = session;
        Subtitle = subtitle;
        ApplyCheckpoint(checkpoint);
    }

    public Guid SessionId => _session.SessionId;

    public AgentSessionRecord Session => _session;

    public string Title => _session.Title;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private string _statusText = "No run state recorded yet.";

    [ObservableProperty]
    private string _statusBadgeText = "Idle";

    [ObservableProperty]
    private IBrush? _statusBrush;

    [ObservableProperty]
    private bool _isRunActive;

    public void UpdateSession(AgentSessionRecord session, string subtitle)
    {
        var oldTitle = _session.Title;
        _session = session;
        if (!string.Equals(oldTitle, session.Title, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(Title));
        }

        if (!string.Equals(Subtitle, subtitle, StringComparison.Ordinal))
        {
            Subtitle = subtitle;
        }

        OnPropertyChanged(nameof(Session));
    }

    public void ApplyCheckpoint(AgentRunCheckpointRecord? checkpoint)
    {
        if (checkpoint is null)
        {
            StatusText = "No run state recorded yet.";
            StatusBadgeText = "Idle";
            StatusBrush = ResolveStatusBrush(AgentRunStatus.Idle);
            IsRunActive = false;
            return;
        }

        StatusText = $"Run revision {checkpoint.RunRevision}: {checkpoint.Status} · {checkpoint.Summary}";
        StatusBadgeText = checkpoint.Status == AgentRunStatus.Completed
            ? "Done"
            : checkpoint.Status.ToString();
        StatusBrush = ResolveStatusBrush(checkpoint.Status);
        IsRunActive = checkpoint.Status == AgentRunStatus.Running;
    }

    private static IBrush? ResolveStatusBrush(AgentRunStatus status)
    {
        var resourceKey = status switch
        {
            AgentRunStatus.Completed => SunderThemeKeys.SuccessBrush,
            AgentRunStatus.Running => SunderThemeKeys.AccentBrush,
            AgentRunStatus.Failed => SunderThemeKeys.DangerBrush,
            AgentRunStatus.Interrupted or AgentRunStatus.Stopped => SunderThemeKeys.WarningBrush,
            _ => SunderThemeKeys.ForegroundMutedBrush,
        };
        return SubagentThemeBrushes.Resolve(resourceKey);
    }
}
