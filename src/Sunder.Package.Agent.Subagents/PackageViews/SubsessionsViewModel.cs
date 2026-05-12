using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Theming;

namespace Sunder.Package.Agent.Subagents.PackageViews;

public sealed partial class SubsessionsViewModel : ObservableObject, IDisposable, IPackageViewNavigationTarget
{
    private const int InitialTranscriptTurnLimit = 100;
    private const int OlderTranscriptTurnPageSize = 60;
    private static readonly TimeSpan DefaultActivityQuietDelay = TimeSpan.FromMilliseconds(900);

    private readonly IPackageExtensionCatalog? _extensionCatalog;
    private readonly SubsessionToolPresentationService _toolPresentationService;
    private readonly DispatcherTimer _activityQuietTimer;
    private readonly TimeSpan _activityQuietDelay;
    private readonly Dictionary<Guid, SubsessionTextTranscriptRowViewModel> _textRowsByTurnId = new();
    private readonly Dictionary<string, SubsessionToolInvocationRowViewModel> _toolRowsByCallId = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _loadedTurnIds = new();
    private IAgentRuntimeCatalog? _runtimeCatalog;
    private SubsessionActivityTranscriptRowViewModel? _activityRow;
    private DateTimeOffset? _oldestLoadedTurnCreatedAtUtc;
    private Guid? _oldestLoadedTurnId;
    private string _activityTextBase = "Thinking";
    private bool _hasVisibleRunActivity;
    private bool _showActivityAfterQuiet;
    private bool _isReconcilingSubsessionSelection;
    private bool _isRestoringReconciledSubsessionSelection;
    private bool _disposed;

    public SubsessionsViewModel(IPackageExtensionCatalog extensionCatalog, TimeSpan? activityQuietDelay = null)
        : this(extensionCatalog, new SubsessionToolPresentationService(extensionCatalog), activityQuietDelay)
    {
    }

    public SubsessionsViewModel()
        : this(null, new SubsessionToolPresentationService(), null)
    {
    }

    private SubsessionsViewModel(
        IPackageExtensionCatalog? extensionCatalog,
        SubsessionToolPresentationService toolPresentationService,
        TimeSpan? activityQuietDelay)
    {
        _extensionCatalog = extensionCatalog;
        _toolPresentationService = toolPresentationService;
        _activityQuietDelay = activityQuietDelay ?? DefaultActivityQuietDelay;
        _activityQuietTimer = new DispatcherTimer
        {
            Interval = _activityQuietDelay <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : _activityQuietDelay,
        };
        _activityQuietTimer.Tick += OnActivityQuietTimerTick;
        EnsureRuntimeCatalog();
    }

    public ObservableCollection<SubsessionListItemViewModel> Subsessions { get; } = [];

    public ObservableCollection<SubsessionTranscriptRowViewModel> Messages { get; } = [];

    public event Action? TranscriptChanged;

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

    public bool CanLoadOlderTranscriptRows => HasOlderTranscriptRows && !IsLoadingOlderTranscriptRows && SelectedSubsession is not null;

    public bool IsSelectedSubsessionRunActive => SelectedSubsession?.IsRunActive == true;

    [ObservableProperty]
    private SubsessionListItemViewModel? _selectedSubsession;

    [ObservableProperty]
    private bool _isCompactLayout;

    [ObservableProperty]
    private bool _isDetailActive;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _hasOlderTranscriptRows;

    [ObservableProperty]
    private bool _isLoadingOlderTranscriptRows;

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
    }

    partial void OnIsCompactLayoutChanged(bool value)
    {
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

    partial void OnHasOlderTranscriptRowsChanged(bool value)
        => OnPropertyChanged(nameof(CanLoadOlderTranscriptRows));

    partial void OnIsLoadingOlderTranscriptRowsChanged(bool value)
        => OnPropertyChanged(nameof(CanLoadOlderTranscriptRows));

    public Task InitializeAsync()
    {
        ReloadSubsessions(null);
        return Task.CompletedTask;
    }

    public ValueTask OnNavigatedToAsync(PackageViewNavigationContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sessionId = TryGetSessionId(context.Parameters);
        if (sessionId is not null && SelectedSubsession?.SessionId == sessionId.Value && IsDetailActive)
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
        => IsDetailActive = false;

    [RelayCommand]
    private void OpenSubsession(SubsessionListItemViewModel? subsession)
    {
        if (subsession is null)
        {
            return;
        }

        SelectedSubsession = subsession;
        IsDetailActive = true;
    }

    [RelayCommand]
    private void OpenChildSession(SubsessionChildSessionLinkViewModel? childSession)
    {
        if (childSession is null)
        {
            return;
        }

        if (SelectedSubsession?.SessionId == childSession.SessionId && IsDetailActive)
        {
            return;
        }

        ReloadSubsessions(childSession.SessionId);
        IsDetailActive = true;
    }

    public async Task<bool> LoadOlderTranscriptRowsAsync()
    {
        if (!CanLoadOlderTranscriptRows || _runtimeCatalog is null || SelectedSubsession is null || _oldestLoadedTurnCreatedAtUtc is null || _oldestLoadedTurnId is null)
        {
            return false;
        }

        IsLoadingOlderTranscriptRows = true;
        try
        {
            await Task.Yield();
            var turns = _runtimeCatalog.ListTurnsBefore(
                SelectedSubsession.SessionId,
                _oldestLoadedTurnCreatedAtUtc.Value,
                _oldestLoadedTurnId.Value,
                OlderTranscriptTurnPageSize);
            if (turns.Count == 0)
            {
                HasOlderTranscriptRows = false;
                return false;
            }

            var insertIndex = 0;
            foreach (var turn in turns.OrderBy(turn => turn.CreatedAtUtc).ThenBy(turn => turn.TurnId))
            {
                insertIndex += ApplyTurnToTranscript(turn, InsertMode.Prepend, insertIndex);
            }

            HasOlderTranscriptRows = turns.Count >= OlderTranscriptTurnPageSize;
            NotifyTranscriptStateChanged();
            return insertIndex > 0;
        }
        finally
        {
            IsLoadingOlderTranscriptRows = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_runtimeCatalog is not null)
        {
            _runtimeCatalog.SessionChanged -= OnSessionChanged;
            _runtimeCatalog.TurnChanged -= OnTurnChanged;
        }

        _activityQuietTimer.Stop();
        _activityQuietTimer.Tick -= OnActivityQuietTimerTick;
        _activityRow?.Dispose();
        _activityRow = null;
    }

    private void EnsureRuntimeCatalog()
    {
        if (_runtimeCatalog is not null || _extensionCatalog is null)
        {
            return;
        }

        _runtimeCatalog = _extensionCatalog.GetExtensions(PackageExtensionPoints.RuntimeCatalogs).FirstOrDefault();
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
        SelectedSubsession = Subsessions.FirstOrDefault(session => session.SessionId == currentSelectionId)
                             ?? Subsessions.FirstOrDefault();
        OnPropertyChanged(nameof(IsSelectedSubsessionRunActive));
        StatusText = Subsessions.Count == 0
            ? "No sub-sessions have been created yet."
            : $"{Subsessions.Count} sub-session(s).";
        UpdateActivityRowForCurrentState();
    }

    private void ReconcileSubsessions(IAgentRuntimeCatalog runtime, IReadOnlyList<AgentSessionRecord> sessions)
    {
        var desiredSessionIds = sessions.Select(session => session.SessionId).ToHashSet();
        var selectedSessionId = SelectedSubsession?.SessionId;
        var shouldPreserveSelectedSession = selectedSessionId is not null && desiredSessionIds.Contains(selectedSessionId.Value);

        _isReconcilingSubsessionSelection = shouldPreserveSelectedSession;
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
                    Subsessions.Insert(index, new SubsessionListItemViewModel(session, subtitle, checkpoint));
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

        RestoreReconciledSubsessionSelection(selectedSessionId, shouldPreserveSelectedSession);
    }

    private void RestoreReconciledSubsessionSelection(Guid? sessionId, bool shouldPreserveSession)
    {
        if (!shouldPreserveSession || sessionId is null || SelectedSubsession?.SessionId == sessionId.Value)
        {
            return;
        }

        var session = FindSubsessionItem(sessionId.Value);
        if (session is null)
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
        var subtitle = string.Join(" · ", new[] { FormatAgentKind(profileName, session.AgentKind), parentTitle }.Where(value => !string.IsNullOrWhiteSpace(value)));
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
        ResetTranscriptWindow();
        var runtime = _runtimeCatalog;
        if (runtime is null || sessionId is null)
        {
            NotifyTranscriptStateChanged();
            TranscriptChanged?.Invoke();
            return;
        }

        var turns = runtime.ListRecentTurns(sessionId.Value, InitialTranscriptTurnLimit);
        foreach (var turn in turns.OrderBy(turn => turn.CreatedAtUtc).ThenBy(turn => turn.TurnId))
        {
            ApplyTurnToTranscript(turn, InsertMode.Append, trackRunActivity: true, scheduleQuietTimer: false);
        }

        HasOlderTranscriptRows = turns.Count >= InitialTranscriptTurnLimit;
        TrackCheckpointActivity(runtime.GetLatestCheckpoint(sessionId.Value));
        UpdateActivityRowForCurrentState();
        NotifyTranscriptStateChanged();
        TranscriptChanged?.Invoke();
    }

    private void ResetTranscriptWindow()
    {
        _activityRow?.Dispose();
        _activityRow = null;
        _textRowsByTurnId.Clear();
        _toolRowsByCallId.Clear();
        _loadedTurnIds.Clear();
        _oldestLoadedTurnCreatedAtUtc = null;
        _oldestLoadedTurnId = null;
        _activityTextBase = "Thinking";
        _hasVisibleRunActivity = false;
        _showActivityAfterQuiet = false;
        _activityQuietTimer.Stop();
        HasOlderTranscriptRows = false;
        IsLoadingOlderTranscriptRows = false;
        Messages.Clear();
        NotifyTranscriptStateChanged();
    }

    private int ApplyTurnToTranscript(
        AgentTurnRecord turn,
        InsertMode insertMode,
        int prependIndex = 0,
        bool trackRunActivity = false,
        bool scheduleQuietTimer = true)
    {
        var insertedRows = 0;
        var isNewTurn = _loadedTurnIds.Add(turn.TurnId);
        TrackOldestLoadedTurn(turn);
        if (insertMode == InsertMode.Append && trackRunActivity)
        {
            TrackVisibleRunActivity(turn, scheduleQuietTimer);
        }

        switch (turn.Kind)
        {
            case AgentTurnKind.ToolCall:
                foreach (var item in turn.Items.Where(item => item.Kind == AgentTurnItemKind.ToolCall))
                {
                    if (!string.IsNullOrWhiteSpace(item.CallId) && _toolRowsByCallId.ContainsKey(item.CallId))
                    {
                        continue;
                    }

                    var row = new SubsessionToolInvocationRowViewModel(turn, item, _toolPresentationService, ResolveChildSessionLinksFromRuntime);
                    if (!string.IsNullOrWhiteSpace(item.CallId))
                    {
                        _toolRowsByCallId[item.CallId] = row;
                    }

                    InsertTranscriptRow(row, insertMode, prependIndex + insertedRows);
                    insertedRows++;
                }

                break;

            case AgentTurnKind.ToolResult:
                foreach (var item in turn.Items.Where(item => item.Kind == AgentTurnItemKind.ToolResult))
                {
                    if (!string.IsNullOrWhiteSpace(item.CallId) && _toolRowsByCallId.TryGetValue(item.CallId, out var existingToolRow))
                    {
                        existingToolRow.ApplyResult(turn, item);
                        continue;
                    }

                    if (!isNewTurn && !string.IsNullOrWhiteSpace(item.CallId) && _toolRowsByCallId.ContainsKey(item.CallId))
                    {
                        continue;
                    }

                    var row = new SubsessionToolInvocationRowViewModel(turn, item, _toolPresentationService, ResolveChildSessionLinksFromRuntime);
                    if (!string.IsNullOrWhiteSpace(item.CallId))
                    {
                        _toolRowsByCallId[item.CallId] = row;
                    }

                    InsertTranscriptRow(row, insertMode, prependIndex + insertedRows);
                    insertedRows++;
                }

                break;

            default:
                var textContent = ExtractTextContent(turn);
                if (string.IsNullOrWhiteSpace(textContent))
                {
                    break;
                }

                if (_textRowsByTurnId.TryGetValue(turn.TurnId, out var existingTextRow))
                {
                    existingTextRow.UpdateContent(textContent);
                    break;
                }

                var textRow = new SubsessionTextTranscriptRowViewModel(turn, textContent);
                _textRowsByTurnId[turn.TurnId] = textRow;
                InsertTranscriptRow(textRow, insertMode, prependIndex + insertedRows);
                insertedRows++;
                break;
        }

        NotifyTranscriptStateChanged();
        return insertedRows;
    }

    private void InsertTranscriptRow(SubsessionTranscriptRowViewModel row, InsertMode insertMode, int prependIndex)
    {
        if (insertMode == InsertMode.Prepend)
        {
            Messages.Insert(Math.Clamp(prependIndex, 0, Messages.Count), row);
            return;
        }

        var insertIndex = _activityRow is null ? Messages.Count : Math.Max(0, Messages.IndexOf(_activityRow));
        Messages.Insert(insertIndex, row);
    }

    private IReadOnlyList<SubsessionChildSessionLinkViewModel> ResolveChildSessionLinksFromRuntime(AgentTurnRecord turn, AgentTurnItemRecord item)
    {
        var runtime = _runtimeCatalog;
        if (runtime is null || !IsSubagentTool(item.ToolId) || string.IsNullOrWhiteSpace(item.CallId))
        {
            return [];
        }

        return runtime.ListSessions()
            .Where(session => session.ParentSessionId == turn.SessionId
                              && string.Equals(session.ParentToolCallId, item.CallId, StringComparison.Ordinal))
            .OrderBy(session => session.CreatedAtUtc)
            .Select(session =>
            {
                var profileName = string.IsNullOrWhiteSpace(session.ProfileId) ? null : runtime.GetProfile(session.ProfileId)?.DisplayName;
                return new SubsessionChildSessionLinkViewModel(
                    session.SessionId,
                    session.Title,
                    FormatAgentKind(profileName, session.AgentKind) ?? "Subsession",
                    runtime.GetLatestCheckpoint(session.SessionId)?.Status ?? AgentRunStatus.Idle);
            })
            .ToArray();
    }

    private static bool IsSubagentTool(string? toolId)
        => string.Equals(toolId, SubagentConstants.TaskToolId, StringComparison.OrdinalIgnoreCase)
           || string.Equals(toolId, SubagentConstants.DelegateTasksToolId, StringComparison.OrdinalIgnoreCase);

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
            TrackCheckpointActivity(_runtimeCatalog?.GetLatestCheckpoint(sessionId));
            UpdateActivityRowForCurrentState();
            TranscriptChanged?.Invoke();
        }

        foreach (var row in _toolRowsByCallId.Values)
        {
            row.RefreshChildSessionLink();
        }
    }

    private void OnTurnChanged(Guid sessionId, AgentTurnRecord turn)
        => RunOnUiThread(() => ApplyTurnChanged(sessionId, turn));

    private void ApplyTurnChanged(Guid sessionId, AgentTurnRecord turn)
    {
        if (_disposed)
        {
            return;
        }

        if (SelectedSubsession?.SessionId != sessionId)
        {
            return;
        }

        ApplyTurnToTranscript(turn, InsertMode.Append, trackRunActivity: true, scheduleQuietTimer: true);
        UpdateActivityRowForCurrentState();
        TranscriptChanged?.Invoke();
    }

    private static void RunOnUiThread(Action action)
    {
        if (Avalonia.Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
    }

    private void UpdateActivityRowForCurrentState()
    {
        if (!IsSelectedSubsessionRunActive)
        {
            _activityQuietTimer.Stop();
            _showActivityAfterQuiet = false;
        }

        if (IsSelectedSubsessionRunActive && (!_hasVisibleRunActivity || _showActivityAfterQuiet))
        {
            if (_activityRow is null)
            {
                _activityRow = new SubsessionActivityTranscriptRowViewModel(_activityTextBase);
                Messages.Add(_activityRow);
                NotifyTranscriptStateChanged();
                return;
            }

            _activityRow.SetActivityTextBase(_activityTextBase);

            var index = Messages.IndexOf(_activityRow);
            if (index >= 0 && index != Messages.Count - 1)
            {
                Messages.Move(index, Messages.Count - 1);
            }
            else if (index < 0)
            {
                Messages.Add(_activityRow);
            }

            NotifyTranscriptStateChanged();
            return;
        }

        if (_activityRow is null)
        {
            return;
        }

        var activityIndex = Messages.IndexOf(_activityRow);
        if (activityIndex >= 0)
        {
            Messages.RemoveAt(activityIndex);
        }

        _activityRow.Dispose();
        _activityRow = null;
        NotifyTranscriptStateChanged();
    }

    private void TrackVisibleRunActivity(AgentTurnRecord turn, bool scheduleQuietTimer)
    {
        if (turn.Role == AgentMessageRole.User)
        {
            _activityTextBase = "Thinking";
            _hasVisibleRunActivity = false;
            _showActivityAfterQuiet = false;
            _activityQuietTimer.Stop();
            return;
        }

        if (HasVisibleRunActivity(turn))
        {
            _hasVisibleRunActivity = true;
            _showActivityAfterQuiet = !scheduleQuietTimer;
            SetActivityTextBase(ResolveActivityTextBase(turn));
            if (scheduleQuietTimer)
            {
                RestartActivityQuietTimer();
            }
        }
    }

    private void RestartActivityQuietTimer()
    {
        _activityQuietTimer.Stop();
        if (!IsSelectedSubsessionRunActive)
        {
            return;
        }

        if (_activityQuietDelay <= TimeSpan.Zero)
        {
            ShowActivityAfterQuietPeriod();
            return;
        }

        _activityQuietTimer.Start();
    }

    private void OnActivityQuietTimerTick(object? sender, EventArgs e)
    {
        _activityQuietTimer.Stop();
        ShowActivityAfterQuietPeriod();
    }

    private void ShowActivityAfterQuietPeriod()
    {
        if (!IsSelectedSubsessionRunActive)
        {
            return;
        }

        _showActivityAfterQuiet = true;
        UpdateActivityRowForCurrentState();
        TranscriptChanged?.Invoke();
    }

    private void SetActivityTextBase(string textBase)
    {
        var normalized = string.IsNullOrWhiteSpace(textBase) ? "Processing" : textBase.Trim();
        if (string.Equals(_activityTextBase, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _activityTextBase = normalized;
        _activityRow?.SetActivityTextBase(normalized);
    }

    private void TrackCheckpointActivity(AgentRunCheckpointRecord? checkpoint)
    {
        if (checkpoint?.Status != AgentRunStatus.Running)
        {
            _activityQuietTimer.Stop();
            _showActivityAfterQuiet = false;
            return;
        }

        SetActivityTextBase(ResolveActivityTextBase(checkpoint));
    }

    private static bool HasVisibleRunActivity(AgentTurnRecord turn)
        => turn.Kind switch
        {
            AgentTurnKind.ToolCall => turn.Items.Any(item => item.Kind == AgentTurnItemKind.ToolCall),
            AgentTurnKind.ToolResult => turn.Items.Any(item => item.Kind == AgentTurnItemKind.ToolResult),
            _ => turn.Role == AgentMessageRole.Assistant && !string.IsNullOrWhiteSpace(ExtractTextContent(turn)),
        };

    private static string ResolveActivityTextBase(AgentTurnRecord turn)
        => turn.Kind switch
        {
            AgentTurnKind.ToolCall => ResolveToolCallActivityText(turn),
            AgentTurnKind.ToolResult => "Processing result",
            _ => turn.Role == AgentMessageRole.Assistant ? "Processing" : "Thinking",
        };

    private static string ResolveToolCallActivityText(AgentTurnRecord turn)
    {
        var toolId = turn.Items.FirstOrDefault(item => item.Kind == AgentTurnItemKind.ToolCall)?.ToolId;
        return string.IsNullOrWhiteSpace(toolId)
            ? "Running tool"
            : $"Running {HumanizeActivityToolName(toolId)}";
    }

    private static string ResolveActivityTextBase(AgentRunCheckpointRecord checkpoint)
    {
        var summary = checkpoint.Summary ?? string.Empty;
        if (TryExtractQuotedToolId(summary, "Executing approved tool '", out var toolId)
            || TryExtractQuotedToolId(summary, "Executing tool '", out toolId))
        {
            return $"Running {HumanizeActivityToolName(toolId ?? string.Empty)}";
        }

        if (summary.Contains("completed. Continuing provider execution", StringComparison.OrdinalIgnoreCase)
            || summary.Contains("continuing provider execution", StringComparison.OrdinalIgnoreCase))
        {
            return "Processing result";
        }

        if (summary.Contains("Provider execution is starting", StringComparison.OrdinalIgnoreCase)
            || summary.Contains("User message queued", StringComparison.OrdinalIgnoreCase))
        {
            return "Thinking";
        }

        return "Processing";
    }

    private static bool TryExtractQuotedToolId(string text, string prefix, out string? toolId)
    {
        toolId = null;
        var start = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return false;
        }

        start += prefix.Length;
        var end = text.IndexOf('\'', start);
        if (end <= start)
        {
            return false;
        }

        toolId = text[start..end];
        return !string.IsNullOrWhiteSpace(toolId);
    }

    private static string HumanizeActivityToolName(string toolId)
    {
        var parts = toolId.Split(['_', '-', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return "tool";
        }

        return string.Join(" ", parts.Select(part => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(part.ToLowerInvariant())));
    }

    private void TrackOldestLoadedTurn(AgentTurnRecord turn)
    {
        if (_oldestLoadedTurnCreatedAtUtc is null
            || turn.CreatedAtUtc < _oldestLoadedTurnCreatedAtUtc
            || turn.CreatedAtUtc == _oldestLoadedTurnCreatedAtUtc && string.CompareOrdinal(turn.TurnId.ToString(), _oldestLoadedTurnId?.ToString()) < 0)
        {
            _oldestLoadedTurnCreatedAtUtc = turn.CreatedAtUtc;
            _oldestLoadedTurnId = turn.TurnId;
        }
    }

    private void NotifyTranscriptStateChanged()
    {
        OnPropertyChanged(nameof(HasTranscriptRows));
        OnPropertyChanged(nameof(ShowEmptyTranscript));
        OnPropertyChanged(nameof(CanLoadOlderTranscriptRows));
    }

    private static string ExtractTextContent(AgentTurnRecord turn)
        => string.Join("\n\n", turn.Items
            .Where(item => item.Kind == AgentTurnItemKind.Text && !string.IsNullOrWhiteSpace(item.TextContent))
            .Select(item => item.TextContent!.Trim()));

    private static Guid? TryGetSessionId(IReadOnlyDictionary<string, string?> parameters)
        => parameters.TryGetValue(SubagentConstants.SubsessionNavigationSessionIdKey, out var value) && Guid.TryParse(value, out var sessionId)
            ? sessionId
            : null;

    private enum InsertMode
    {
        Append,
        Prepend,
    }
}

public sealed partial class SubsessionListItemViewModel : ObservableObject
{
    private AgentSessionRecord _session;

    public SubsessionListItemViewModel(AgentSessionRecord session, string subtitle, AgentRunCheckpointRecord? checkpoint)
    {
        _session = session;
        Subtitle = subtitle;
        ApplyCheckpoint(checkpoint);
    }

    public Guid SessionId => _session.SessionId;

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
        StatusBadgeText = checkpoint.Status == AgentRunStatus.Completed ? "Done" : checkpoint.Status.ToString();
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
            _ => SunderThemeKeys.ForegroundMutedBrush
        };

        return SubagentThemeBrushes.Resolve(resourceKey);
    }
}
