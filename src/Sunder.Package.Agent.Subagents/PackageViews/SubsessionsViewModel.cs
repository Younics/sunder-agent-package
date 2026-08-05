using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
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
    private const string ListRefreshChannel = "subsessions-list";
    private const string NavigationChannel = "subsessions-navigation";

    private readonly AgentRpcCatalog? _rpcCatalog;
    private readonly IPresentationDispatcher _uiDispatcher;
    private readonly TranscriptTimelineState<SubsessionTranscriptRowViewModel> _timeline;
    private readonly TranscriptItemsProjection<SubsessionTranscriptRowViewModel> _transcriptItemsProjection;
    private readonly ActivityTicker _activityTicker = new();
    private readonly AgentRunActivityState _runActivity;
    private readonly AsyncOnce _initialization = new();
    private readonly PresentationTaskScope _tasks = new();
    private readonly LatestRequestCoordinator _requests = new();
    private readonly KeyedAdaptiveListDetailState<Guid, SubsessionListItemViewModel> _listDetail;
    private readonly SerializedRefreshLoop _runtimeRefresh;
    private readonly Dictionary<Guid, AgentSessionRecord> _knownSessions = [];
    private readonly Dictionary<string, AgentProfileRecord> _knownProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, AgentRunCheckpointRecord> _knownCheckpoints = [];
    private ISubsessionSessionReader? _sessionReader;
    private ISubsessionCheckpointReader? _checkpointReader;
    private ISubsessionTranscriptPageReader? _transcriptReader;
    private ISubsessionChangeNotifications? _changeNotifications;
    private CancellationTokenSource? _navigationHighlightCancellation;
    private long _navigationHighlightGeneration;
    private Task _transcriptLoadOperation = Task.CompletedTask;
    private int _forceRuntimeTranscriptRefresh;
    private int _suppressAutomaticTranscriptLoad;
    private bool _disposed;

    public SubsessionsViewModel(
        AgentRpcCatalog? rpcCatalog,
        TimeSpan? activityQuietDelay = null)
        : this(rpcCatalog, activityQuietDelay, PresentationDispatcher.Capture())
    {
    }

    internal SubsessionsViewModel(
        AgentRpcCatalog? rpcCatalog,
        TimeSpan? activityQuietDelay,
        IPresentationDispatcher uiDispatcher)
    {
        _uiDispatcher = uiDispatcher;
        _activityTicker.SetEnabled(false);
        _rpcCatalog = rpcCatalog;
        var toolPresentation = new TranscriptToolPresentationService(() =>
            rpcCatalog is not null
                ? rpcCatalog.GetServiceReferences(AgentRpcServices.ToolSources)
                    .Select(static reference =>
                        (IAgentToolPresentationResolver)new RpcToolSourcePresentationResolver(reference))
                : []);
        var rowFactory = new SubsessionTranscriptRowFactory(
            toolPresentation,
            LoadToolDetailAsync,
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
        RunActivityRow = new SubsessionActivityTranscriptRowViewModel(_activityTicker);
        TailSentinelRow = new SubsessionTranscriptTailSentinelRowViewModel();
        _transcriptItemsProjection = new TranscriptItemsProjection<SubsessionTranscriptRowViewModel>(
            Messages,
            RunActivityRow,
            TailSentinelRow);
        TranscriptItems = _transcriptItemsProjection.Items;
        _listDetail = new KeyedAdaptiveListDetailState<Guid, SubsessionListItemViewModel>(
            Subsessions,
            static subsession => subsession.SessionId,
            static (current, incoming) => current.UpdateFrom(incoming));
        _runActivity = new AgentRunActivityState(
            () => IsSelectedSubsessionRunActive,
            () => _timeline.IsFollowingLatest,
            activityQuietDelay);
        _timeline.RowsChanging += isPageApplication => TranscriptChanging?.Invoke(isPageApplication);
        _timeline.RowsChanged += OnTimelineRowsChanged;
        _timeline.PropertyChanged += OnTimelinePropertyChanged;
        _timeline.TurnProjected += OnTimelineTurnProjected;
        _runActivity.Changed += OnRunActivityStateChanged;
        _listDetail.PropertyChanged += OnListDetailPropertyChanged;
        _runtimeRefresh = new SerializedRefreshLoop(
            RefreshFromRuntimeAsync,
            exception => RunOnUiThread(() => SetLoadFailure(exception.Message)));
    }

    private Task<AgentTranscriptToolDetailRecord?> LoadToolDetailAsync(
        AgentTranscriptToolDetailRequest request,
        CancellationToken cancellationToken)
        => _transcriptReader?.LoadToolDetailAsync(request, cancellationToken)
           ?? Task.FromResult<AgentTranscriptToolDetailRecord?>(null);

    public ObservableCollection<SubsessionListItemViewModel> Subsessions { get; } = [];

    public ObservableCollection<SubsessionTranscriptRowViewModel> Messages { get; }
        = new TranscriptObservableCollection<SubsessionTranscriptRowViewModel>();

    public SubsessionActivityTranscriptRowViewModel RunActivityRow { get; }

    internal SubsessionTranscriptTailSentinelRowViewModel TailSentinelRow { get; }

    public ReadOnlyObservableCollection<SubsessionTranscriptRowViewModel> TranscriptItems { get; }

    public event Action? TranscriptChanged;

    public event Action<bool>? TranscriptChanging;

    public bool IsListActive => !IsDetailActive;

    public bool ShowWideLayout => _listDetail.Layout == AdaptiveListDetailLayout.Wide;

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

    internal object? NavigationAnchorKey { get; private set; }

    internal long? NavigationSelectionAuthorityRevision { get; private set; }

    public SubsessionListItemViewModel? SelectedSubsession
    {
        get => _listDetail.SelectedItem;
        set
        {
            if (value is null)
            {
                CancelNavigation();
                _listDetail.ShowList();
            }
            else
            {
                ActivateSubsession(value);
            }
        }
    }

    public bool IsCompactLayout
    {
        get => _listDetail.Layout == AdaptiveListDetailLayout.Compact;
        set => _listDetail.SetLayout(value
            ? AdaptiveListDetailLayout.Compact
            : AdaptiveListDetailLayout.Wide);
    }

    public bool IsDetailActive
    {
        get => IsCompactLayout && !_listDetail.IsList;
        set
        {
            if (!value)
            {
                CancelNavigation();
                _listDetail.ShowList();
            }
            else if (SelectedSubsession is { } selected
                     && !_listDetail.IsExistingDetail)
            {
                _listDetail.ShowExistingDetail(selected);
            }
        }
    }

    internal AdaptiveListDetailRoute Route => _listDetail.Route;

    internal AdaptiveListDetailLayout Layout => _listDetail.Layout;

    internal AdaptiveDetailPhase DetailPhase => _listDetail.DetailPhase;

    internal long IntentRevision => _listDetail.IntentRevision;

    [RelayCommand]
    private void BackToSubsessionsList()
    {
        CancelNavigation();
        _listDetail.ShowList();
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
        if (!(_listDetail.IsExistingDetail && ReferenceEquals(SelectedSubsession, subsession)))
        {
            CancelNavigation();
            _listDetail.ShowExistingDetail(subsession);
        }
    }

    [RelayCommand]
    private async Task OpenChildSessionAsync(SubsessionChildSessionLinkViewModel? childSession)
    {
        if (childSession is null
            || SelectedSubsession?.SessionId == childSession.SessionId
            && _listDetail.IsExistingDetail)
        {
            return;
        }

        await ReloadSubsessionsAsync(childSession.SessionId);
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
        if (_sessionReader is not null || _rpcCatalog is null)
        {
            return;
        }

        var runtimeReference = _rpcCatalog
            .GetServiceReferences(AgentRpcServices.RuntimeCatalogs)
            .FirstOrDefault();
        if (runtimeReference is not null)
        {
            var adapter = new SubsessionLocalRuntimeAdapter(runtimeReference);
            SetRuntimePorts(adapter, adapter, adapter, adapter, ownsChangeNotifications: true);
        }
    }

    internal async Task ReloadSubsessionsAsync(
        Guid? selectedSessionId,
        CancellationToken cancellationToken = default,
        bool suppressTranscriptLoad = false)
    {
        EnsureRuntimeReaders();
        if (_sessionReader is null || _checkpointReader is null)
        {
            throw new InvalidOperationException("The Agent runtime is not available.");
        }

        var refresh = _requests.Begin(ListRefreshChannel, cancellationToken);
        var selectionAuthorityRevision = 0L;
        var currentSelectionId = selectedSessionId;
        var active = false;
        try
        {
            await RunOnUiThreadAsync(() =>
            {
                selectionAuthorityRevision = IntentRevision;
                currentSelectionId ??= SelectedSubsession?.SessionId;
                active = true;
            }, refresh.CancellationToken);
            if (!active)
            {
                return;
            }

            var sessionsTask = _sessionReader.ListSessionsAsync(refresh.CancellationToken);
            var checkpointsTask = _checkpointReader.ListLatestCheckpointsAsync(refresh.CancellationToken);
            await Task.WhenAll(sessionsTask, checkpointsTask);
            var catalog = await sessionsTask;
            var checkpoints = await checkpointsTask;
            var subsessions = catalog.Sessions
                .Where(session => session.ParentSessionId is not null)
                .OrderByDescending(session => session.UpdatedAtUtc)
                .ThenByDescending(session => session.CreatedAtUtc)
                .ToArray();
            var applied = false;
            var transcriptLoadOperation = Task.CompletedTask;
            await RunOnUiThreadAsync(
                () =>
                {
                    if (_disposed || !_requests.IsCurrent(refresh))
                    {
                        return;
                    }

                    var preserveLiveSelection = selectionAuthorityRevision != IntentRevision;
                    var liveSelectionId = preserveLiveSelection
                        ? SelectedSubsession?.SessionId
                        : currentSelectionId;
                    RunWithAutomaticTranscriptLoadSuppressed(
                        () => ApplySubsessionSnapshot(
                            preserveLiveSelection ? null : selectedSessionId,
                            liveSelectionId,
                            catalog,
                            checkpoints,
                            subsessions,
                            preserveLiveSelection),
                        suppressTranscriptLoad);
                    transcriptLoadOperation = Volatile.Read(ref _transcriptLoadOperation);
                    applied = true;
                },
                refresh.CancellationToken);
            if (applied)
            {
                await transcriptLoadOperation.WaitAsync(refresh.CancellationToken);
            }
        }
        catch (OperationCanceledException) when (
            refresh.CancellationToken.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _requests.Complete(refresh);
        }
    }

    private void ApplySubsessionSnapshot(
        Guid? requestedSessionId,
        Guid? currentSelectionId,
        SubsessionSessionCatalog catalog,
        IReadOnlyList<AgentRunCheckpointRecord> checkpoints,
        IReadOnlyList<AgentSessionRecord> subsessions,
        bool preserveLiveSelection = false)
    {
        ReplaceRuntimeSnapshot(catalog, checkpoints);
        HasLoadError = false;
        var rows = subsessions.Select(session =>
        {
            _knownCheckpoints.TryGetValue(session.SessionId, out var checkpoint);
            return new SubsessionListItemViewModel(session, BuildSubtitle(session), checkpoint);
        }).ToArray();
        StatusText = rows.Length == 0
            ? "No sub-sessions have been created yet."
            : $"{rows.Length} sub-session(s).";
        _listDetail.Reconcile(rows);
        OnPropertyChanged(nameof(HasNoSubsessions));

        if (!preserveLiveSelection && requestedSessionId is { } requested
            && FindSubsessionItem(requested) is { } requestedSubsession)
        {
            _listDetail.ShowExistingDetail(requestedSubsession);
        }
        else if (!preserveLiveSelection
                 && currentSelectionId is { } current
                 && !_listDetail.IsExistingDetail
                 && FindSubsessionItem(current) is { } currentSubsession)
        {
            _listDetail.ShowExistingDetail(currentSubsession);
        }

        OnPropertyChanged(nameof(IsSelectedSubsessionRunActive));
        ApplyRunActivityState();
    }

    private SubsessionListItemViewModel? FindSubsessionItem(Guid sessionId)
        => Subsessions.FirstOrDefault(session => session.SessionId == sessionId);

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
        => _tasks.Run(_runtimeRefresh.MarkDirty());

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
        => RunOnUiThread(() =>
        {
            ApplyRunActivityState();
            _timeline.NotifyRowsChanged();
        });

    private void ApplyRunActivityState()
        => RunActivityRow.SetPresentation(
            _runActivity.Text,
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
        if (_uiDispatcher.CheckAccess())
        {
            if (!_disposed)
            {
                action();
            }
            return;
        }

        _tasks.Run(cancellationToken => RunOnUiThreadAsync(action, cancellationToken));
    }

    private void OnListDetailPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(KeyedAdaptiveListDetailState<Guid, SubsessionListItemViewModel>.SelectedItem))
        {
            NavigationAnchorKey = null;
            NavigationSelectionAuthorityRevision = null;
            OnPropertyChanged(nameof(SelectedSubsession));
            OnPropertyChanged(nameof(HasSelectedSubsession));
            OnPropertyChanged(nameof(IsSelectedSubsessionRunActive));
            if (_listDetail.IsExistingDetail && SelectedSubsession is { } selected)
            {
                var ticket = _listDetail.BeginDetailLoad();
                _listDetail.TrySetDetailReady(ticket);
                if (Volatile.Read(ref _suppressAutomaticTranscriptLoad) == 0)
                {
                    LoadTranscript(selected.SessionId);
                }
                else
                {
                    Volatile.Write(ref _transcriptLoadOperation, Task.CompletedTask);
                }
            }
            else
            {
                if (Volatile.Read(ref _suppressAutomaticTranscriptLoad) == 0)
                {
                    LoadTranscript(null);
                }
                else
                {
                    Volatile.Write(ref _transcriptLoadOperation, Task.CompletedTask);
                }
            }
        }

        OnPropertyChanged(nameof(IsCompactLayout));
        OnPropertyChanged(nameof(IsDetailActive));
        OnPropertyChanged(nameof(IsListActive));
        OnPropertyChanged(nameof(ShowWideLayout));
        OnPropertyChanged(nameof(ShowCompactList));
        OnPropertyChanged(nameof(ShowCompactDetail));
        OnPropertyChanged(nameof(ShowListPane));
        OnPropertyChanged(nameof(ShowDetailPane));
    }

    private void CancelNavigation()
    {
        _requests.Invalidate(NavigationChannel);
        NavigationAnchorKey = null;
        NavigationSelectionAuthorityRevision = null;
    }

    private void RunWithAutomaticTranscriptLoadSuppressed(Action action, bool suppress = true)
    {
        if (!suppress)
        {
            action();
            return;
        }

        Interlocked.Increment(ref _suppressAutomaticTranscriptLoad);
        try
        {
            action();
        }
        finally
        {
            Interlocked.Decrement(ref _suppressAutomaticTranscriptLoad);
        }
    }

    private async Task RefreshFromRuntimeAsync(CancellationToken cancellationToken)
    {
        Guid? selectedSessionId = null;
        var selectionAuthorityRevision = 0L;
        await RunOnUiThreadAsync(() =>
        {
            selectedSessionId = SelectedSubsession?.SessionId;
            selectionAuthorityRevision = IntentRevision;
        }, cancellationToken);

        await ReloadSubsessionsAsync(null, cancellationToken);
        var forceTranscript = Interlocked.Exchange(ref _forceRuntimeTranscriptRefresh, 0) != 0;
        Task transcriptLoad = Task.CompletedTask;
        await RunOnUiThreadAsync(() =>
        {
            if (selectionAuthorityRevision != IntentRevision
                || SelectedSubsession?.SessionId != selectedSessionId)
            {
                return;
            }

            if (forceTranscript)
            {
                LoadTranscript(selectedSessionId, forceReplacement: true);
                transcriptLoad = Volatile.Read(ref _transcriptLoadOperation);
            }
            if (selectedSessionId is { } sessionId)
            {
                ApplySessionChanged(sessionId);
            }
        }, cancellationToken);
        await transcriptLoad.WaitAsync(cancellationToken);
    }

    private async Task RunOnUiThreadAsync(Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _uiDispatcher.InvokeAsync(() =>
        {
            if (!_disposed && !cancellationToken.IsCancellationRequested)
            {
                action();
            }
        });
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
        ISubsessionChangeNotifications changeNotifications,
        bool ownsChangeNotifications = false)
    {
        _sessionReader = sessionReader;
        _checkpointReader = checkpointReader;
        _transcriptReader = transcriptReader;
        _changeNotifications = changeNotifications;
        _ownsChangeNotifications = ownsChangeNotifications;
        changeNotifications.SessionChanged += OnSessionChanged;
        changeNotifications.TurnChanged += OnTurnChanged;
        changeNotifications.ResnapshotRequired += OnRuntimeResnapshotRequired;
    }

    private static Guid? TryGetSessionId(IReadOnlyDictionary<string, string?> parameters)
        => parameters.TryGetValue(
               SubagentConstants.SubsessionNavigationSessionIdKey,
               out var value)
           && Guid.TryParse(value, out var sessionId)
            ? sessionId
            : null;

}
