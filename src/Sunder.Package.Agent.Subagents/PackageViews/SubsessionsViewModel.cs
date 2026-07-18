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
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Package.Agent.Subagents.Runtime;
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
    private readonly ActivityTicker _activityTicker = new();
    private readonly AgentRunActivityState _runActivity;
    private readonly AsyncOnce _initialization = new();
    private readonly PresentationTaskScope _tasks = new();
    private readonly Dictionary<Guid, AgentSessionRecord> _knownSessions = [];
    private readonly Dictionary<string, AgentProfileRecord> _knownProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, AgentRunCheckpointRecord> _knownCheckpoints = [];
    private ISubsessionSessionReader? _sessionReader;
    private ISubsessionCheckpointReader? _checkpointReader;
    private ISubsessionTranscriptPageReader? _transcriptReader;
    private ISubsessionChangeNotifications? _changeNotifications;
    private bool _isReconcilingSubsessionSelection;
    private bool _isRestoringReconciledSubsessionSelection;
    private bool _disposed;

    public SubsessionsViewModel(
        IPackageExtensionCatalog? extensionCatalog,
        TimeSpan? activityQuietDelay = null)
    {
        _activityTicker.SetEnabled(false);
        _extensionCatalog = extensionCatalog;
        var toolPresentation = new TranscriptToolPresentationService(() =>
            extensionCatalog?.GetExtensions(PackageExtensionPoints.ToolSources)
                .OfType<IAgentToolPresentationResolver>()
            ?? []);
        var rowFactory = new SubsessionTranscriptRowFactory(
            toolPresentation,
            _activityTicker,
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
        _timeline.RowsChanging += isPageApplication => TranscriptChanging?.Invoke(isPageApplication);
        _timeline.RowsChanged += OnTimelineRowsChanged;
        _timeline.PropertyChanged += OnTimelinePropertyChanged;
        _timeline.TurnProjected += OnTimelineTurnProjected;
        _runActivity.Changed += OnRunActivityStateChanged;
    }

    public SubsessionsViewModel()
        : this(null, null)
    {
    }

    public ObservableCollection<SubsessionListItemViewModel> Subsessions { get; } = [];

    public ObservableCollection<SubsessionTranscriptRowViewModel> Messages { get; } = [];

    public event Action? TranscriptChanged;

    public event Action<bool>? TranscriptChanging;

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

    internal bool IsTranscriptFollowingLatest => _timeline.IsFollowingLatest;

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

    public async ValueTask OnNavigatedToAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sessionId = TryGetSessionId(context.Parameters);
        try
        {
            await EnsureInitializedAsync(sessionId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return;
        }
        if (sessionId is not null
            && SelectedSubsession?.SessionId == sessionId.Value
            && IsDetailActive)
        {
            return;
        }

        if (sessionId is null)
        {
            return;
        }

        if (FindSubsessionItem(sessionId.Value) is { } subsession)
        {
            SelectedSubsession = subsession;
        }
        else
        {
            await ReloadSubsessionsAsync(sessionId, cancellationToken);
        }

        IsDetailActive = true;
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
    private async Task OpenChildSessionAsync(SubsessionChildSessionLinkViewModel? childSession)
    {
        if (childSession is null
            || SelectedSubsession?.SessionId == childSession.SessionId && IsDetailActive)
        {
            return;
        }

        await ReloadSubsessionsAsync(childSession.SessionId);
        IsDetailActive = true;
    }

    [RelayCommand]
    private void JumpToLatestTranscript()
    {
        if (_timeline.RequestJumpToLatest())
        {
            LoadTranscript(SelectedSubsession?.SessionId);
        }
    }

    public bool DetachTranscriptFromLatest()
    {
        if (_timeline.DetachFromLatest())
        {
            _runActivity.NotifyFollowStateChanged();
            return true;
        }

        return false;
    }

    public bool ResumeTranscriptFollowingLatestIfCaughtUp()
    {
        if (_timeline.ResumeFollowingLatestIfCaughtUp())
        {
            _runActivity.NotifyFollowStateChanged();
            ApplyRunActivityState();
            _timeline.NotifyRowsChanged();
            return true;
        }

        return false;
    }

    internal void SetTranscriptJumpToLatestVisible(bool isVisible)
        => _timeline.SetJumpToLatestVisible(isVisible);

    internal void SetTranscriptViewportAnchor(TranscriptViewportAnchorData? anchor)
        => _timeline.SetViewportAnchor(anchor);

    internal TranscriptViewportAnchorData? TranscriptViewportAnchor => _timeline.ViewportAnchor;

    internal void SetTranscriptPresentationActive(bool isActive)
        => _activityTicker.SetEnabled(isActive);

    internal void SetTranscriptRowExpanded(
        SubsessionTranscriptRowViewModel row,
        bool isExpanded)
        => _timeline.SetRowExpanded(row, isExpanded);

    private void EnsureRuntimeReaders()
    {
        if (_sessionReader is not null || _extensionCatalog is null)
        {
            return;
        }

        var runtime = _extensionCatalog
            .GetExtensions(PackageExtensionPoints.RuntimeCatalogs)
            .FirstOrDefault();
        if (runtime is not null)
        {
            var adapter = new SubsessionLocalRuntimeAdapter(runtime);
            SetRuntimePorts(adapter, adapter, adapter, adapter);
        }
    }

    private async Task ReloadSubsessionsAsync(
        Guid? selectedSessionId,
        CancellationToken cancellationToken = default)
    {
        EnsureRuntimeReaders();
        if (_sessionReader is null || _checkpointReader is null)
        {
            StatusText = "The Agent runtime is not available.";
            return;
        }

        var currentSelectionId = selectedSessionId ?? SelectedSubsession?.SessionId;
        var sessionsTask = _sessionReader.ListSessionsAsync(cancellationToken);
        var checkpointsTask = _checkpointReader.ListLatestCheckpointsAsync(cancellationToken);
        await Task.WhenAll(sessionsTask, checkpointsTask);
        var catalog = await sessionsTask;
        var checkpoints = await checkpointsTask;
        var subsessions = catalog.Sessions
            .Where(session => session.ParentSessionId is not null)
            .OrderByDescending(session => session.UpdatedAtUtc)
            .ThenByDescending(session => session.CreatedAtUtc)
            .ToArray();
        await RunOnUiThreadAsync(
            () => ApplySubsessionSnapshot(
                selectedSessionId,
                currentSelectionId,
                catalog,
                checkpoints,
                subsessions),
            cancellationToken);
    }

    private void ApplySubsessionSnapshot(
        Guid? requestedSessionId,
        Guid? currentSelectionId,
        SubsessionSessionCatalog catalog,
        IReadOnlyList<AgentRunCheckpointRecord> checkpoints,
        IReadOnlyList<AgentSessionRecord> subsessions)
    {
        ReplaceRuntimeSnapshot(catalog, checkpoints);
        ReconcileSubsessions(subsessions);
        OnPropertyChanged(nameof(HasNoSubsessions));
        var selectedSubsession = Subsessions.FirstOrDefault(
            session => session.SessionId == currentSelectionId);
        if (selectedSubsession is null && (!IsCompactLayout || requestedSessionId is not null))
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

    private void ReconcileSubsessions(IReadOnlyList<AgentSessionRecord> sessions)
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
                var subtitle = BuildSubtitle(session);
                _knownCheckpoints.TryGetValue(session.SessionId, out var checkpoint);
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

    private string BuildSubtitle(AgentSessionRecord session)
    {
        var parentTitle = session.ParentSessionId is { } parentSessionId
            && _knownSessions.TryGetValue(parentSessionId, out var parentSession)
            ? parentSession.Title
            : null;
        var profileName = string.IsNullOrWhiteSpace(session.ProfileId)
            ? null
            : _knownProfiles.GetValueOrDefault(session.ProfileId)?.DisplayName;
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

    private IReadOnlyList<SubsessionChildSessionLinkViewModel> ResolveChildSessionLinksFromRuntime(
        AgentTurnRecord turn,
        AgentTurnItemRecord item)
    {
        if (!IsSubagentTool(item.ToolId)
            || string.IsNullOrWhiteSpace(item.CallId))
        {
            return [];
        }

        return _knownSessions.Values
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
                    : _knownProfiles.GetValueOrDefault(session.ProfileId)?.DisplayName;
                _knownCheckpoints.TryGetValue(session.SessionId, out var checkpoint);
                return new SubsessionChildSessionLinkViewModel(
                    session.SessionId,
                    session.Title,
                    FormatAgentKind(profileName, session.AgentKind) ?? "Subsession",
                    checkpoint?.Status ?? AgentRunStatus.Idle);
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
        => _tasks.Run(async cancellationToken =>
        {
            try
            {
                await ReloadSubsessionsAsync(SelectedSubsession?.SessionId, cancellationToken);
                await RunOnUiThreadAsync(() => ApplySessionChanged(sessionId), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                await RunOnUiThreadAsync(() => StatusText = ex.Message, cancellationToken);
            }
        });

    private void ApplySessionChanged(Guid sessionId)
    {
        if (_disposed)
        {
            return;
        }

        if (SelectedSubsession?.SessionId == sessionId)
        {
            _knownCheckpoints.TryGetValue(sessionId, out var checkpoint);
            _runActivity.TrackCheckpoint(checkpoint);
            ApplyRunActivityState();
            _timeline.NotifyRowsChanged();
        }

        _timeline.RefreshRelatedRows();
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

    private void RunOnUiThread(Action action)
    {
        _tasks.Run(async cancellationToken =>
        {
            if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    action();
                }
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    action();
                }
            }, DispatcherPriority.Background);
        });
    }

    private async Task RunOnUiThreadAsync(Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                action();
            }
        }, DispatcherPriority.Background);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void ReplaceRuntimeSnapshot(
        SubsessionSessionCatalog catalog,
        IReadOnlyList<AgentRunCheckpointRecord> checkpoints)
    {
        _knownSessions.Clear();
        foreach (var session in catalog.Sessions)
        {
            _knownSessions[session.SessionId] = session;
        }

        _knownProfiles.Clear();
        foreach (var profile in catalog.Profiles)
        {
            _knownProfiles[profile.ProfileId] = profile;
        }

        _knownCheckpoints.Clear();
        foreach (var checkpoint in checkpoints)
        {
            _knownCheckpoints[checkpoint.SessionId] = checkpoint;
        }
    }

    private void SetRuntimePorts(
        ISubsessionSessionReader sessionReader,
        ISubsessionCheckpointReader checkpointReader,
        ISubsessionTranscriptPageReader transcriptReader,
        ISubsessionChangeNotifications changeNotifications)
    {
        _sessionReader = sessionReader;
        _checkpointReader = checkpointReader;
        _transcriptReader = transcriptReader;
        _changeNotifications = changeNotifications;
        changeNotifications.SessionChanged += OnSessionChanged;
        changeNotifications.TurnChanged += OnTurnChanged;
    }

    private static Guid? TryGetSessionId(IReadOnlyDictionary<string, string?> parameters)
        => parameters.TryGetValue(
               SubagentConstants.SubsessionNavigationSessionIdKey,
               out var value)
           && Guid.TryParse(value, out var sessionId)
            ? sessionId
            : null;
}
