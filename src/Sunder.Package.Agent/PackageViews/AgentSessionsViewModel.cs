using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentSessionsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SuccessStatusDisplayDuration = TimeSpan.FromSeconds(3);

    private readonly AgentSessionService _sessionService;
    private CancellationTokenSource? _successStatusClearCancellation;
    private bool _suppressSelectionHandlers;
    private bool _disposed;

    public AgentSessionsViewModel(AgentSessionService sessionService)
    {
        _sessionService = sessionService;
        _sessionService.SessionChanged += OnSessionChanged;
        ReloadSessions(selectSessionId: null);
    }

    public ObservableCollection<AgentSessionListEntryViewModel> Sessions { get; } = [];

    public bool HasSelectedSession => SelectedSession is not null;

    public bool HasNoSessions => Sessions.Count == 0;

    public bool IsListActive => !IsEditorActive;

    public bool ShowWideLayout => !IsCompactLayout;

    public bool ShowCompactList => IsCompactLayout && IsListActive;

    public bool ShowCompactEditor => IsCompactLayout && IsEditorActive;

    public bool ShowListPane => ShowWideLayout || ShowCompactList;

    public bool ShowEditorPane => ShowWideLayout || ShowCompactEditor;

    [ObservableProperty]
    private AgentSessionListEntryViewModel? _selectedSession;

    [ObservableProperty]
    private bool _isCompactLayout;

    [ObservableProperty]
    private bool _isEditorActive;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusText))]
    private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusSuccess))]
    [NotifyPropertyChangedFor(nameof(IsStatusWarning))]
    [NotifyPropertyChangedFor(nameof(IsStatusError))]
    private AgentSessionStatusKind _statusKind = AgentSessionStatusKind.None;

    [ObservableProperty]
    private string _detailStateText = "No session selected.";

    [ObservableProperty]
    private string _detailCreatedText = string.Empty;

    [ObservableProperty]
    private string _detailUpdatedText = string.Empty;

    public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

    public bool IsStatusSuccess => StatusKind == AgentSessionStatusKind.Success;

    public bool IsStatusWarning => StatusKind == AgentSessionStatusKind.Warning;

    public bool IsStatusError => StatusKind == AgentSessionStatusKind.Error;

    partial void OnSelectedSessionChanged(AgentSessionListEntryViewModel? value)
    {
        SaveSessionCommand.NotifyCanExecuteChanged();
        DeleteSessionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasSelectedSession));

        if (_suppressSelectionHandlers)
        {
            return;
        }

        LoadSession(value);
        if (IsCompactLayout && value is not null)
        {
            IsEditorActive = true;
        }
    }

    partial void OnIsCompactLayoutChanged(bool value)
    {
        if (value && !IsEditorActive)
        {
            SelectedSession = null;
        }
        else if (!value && SelectedSession is null)
        {
            SelectedSession = Sessions.FirstOrDefault();
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

    [RelayCommand]
    private void CreateSession()
    {
        try
        {
            var session = _sessionService.CreateSession("New Session");
            ReloadSessions(session.SessionId);
            IsEditorActive = true;
            ClearStatus();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, AgentSessionStatusKind.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditSession))]
    private void SaveSession()
    {
        if (SelectedSession is null)
        {
            return;
        }

        try
        {
            var sessionId = SelectedSession.SessionId;
            var updated = SelectedSession.Session with
            {
                Title = string.IsNullOrWhiteSpace(Title) ? "Unnamed Session" : Title.Trim(),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

            _sessionService.UpdateSession(updated);
            var shouldClearSelection = IsCompactLayout;
            ReloadSessions(sessionId);
            if (shouldClearSelection)
            {
                SelectedSession = null;
                ClearStatus();
            }
            else
            {
                SetStatus("Session saved.", AgentSessionStatusKind.Success, autoClear: true);
            }

            IsEditorActive = false;
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, AgentSessionStatusKind.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditSession))]
    private void DeleteSession()
    {
        if (SelectedSession is null)
        {
            return;
        }

        try
        {
            var deletedTitle = SelectedSession.Title;
            var shouldClearSelection = IsCompactLayout;
            _sessionService.DeleteSession(SelectedSession.SessionId);
            ReloadSessions(selectSessionId: null);
            if (shouldClearSelection)
            {
                SelectedSession = null;
                ClearStatus();
            }
            else
            {
                SetStatus($"Deleted session '{deletedTitle}'.", AgentSessionStatusKind.Success, autoClear: true);
            }

            IsEditorActive = false;
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, AgentSessionStatusKind.Error);
        }
    }

    private bool CanEditSession() => SelectedSession is not null;

    [RelayCommand]
    private void BackToSessionList()
    {
        if (IsCompactLayout)
        {
            SelectedSession = null;
        }

        IsEditorActive = false;
    }

    [RelayCommand]
    private void OpenSessionEditor(AgentSessionListEntryViewModel? session)
    {
        if (session is null)
        {
            return;
        }

        ActivateSession(session);
    }

    public void ActivateSession(AgentSessionListEntryViewModel session)
    {
        if (SelectedSession?.SessionId != session.SessionId)
        {
            SelectedSession = session;
        }

        if (IsCompactLayout)
        {
            IsEditorActive = true;
        }
    }

    private void OnSessionChanged(Guid sessionId)
        => RunOnUiThread(() => ApplySessionChanged(sessionId));

    private void ApplySessionChanged(Guid sessionId)
    {
        var session = _sessionService.GetSession(sessionId);
        if (session is null || session.ParentSessionId is not null)
        {
            RemoveSession(sessionId);
            return;
        }

        var checkpoint = _sessionService.GetLatestCheckpoint(session.SessionId);
        var existingIndex = FindSessionIndex(session.SessionId);
        if (existingIndex < 0)
        {
            Sessions.Insert(FindSortedSessionTargetIndex(session), new AgentSessionListEntryViewModel(session, checkpoint));
            OnPropertyChanged(nameof(HasNoSessions));
            ClearStatus();
            return;
        }

        var item = Sessions[existingIndex];
        item.UpdateSession(session);
        item.ApplyCheckpoint(checkpoint);

        var targetIndex = FindSortedSessionTargetIndex(session);
        if (targetIndex != existingIndex)
        {
            Sessions.Move(existingIndex, targetIndex);
        }

        if (SelectedSession?.SessionId == sessionId)
        {
            LoadSession(SelectedSession);
        }

        ClearStatus();
    }

    private void RemoveSession(Guid sessionId)
    {
        var index = FindSessionIndex(sessionId);
        if (index < 0)
        {
            return;
        }

        var wasSelected = SelectedSession?.SessionId == sessionId;
        _suppressSelectionHandlers = true;
        try
        {
            Sessions.RemoveAt(index);
            if (wasSelected)
            {
                SelectedSession = IsCompactLayout ? null : Sessions.FirstOrDefault();
                if (IsCompactLayout)
                {
                    IsEditorActive = false;
                }
            }
        }
        finally
        {
            _suppressSelectionHandlers = false;
        }

        OnPropertyChanged(nameof(HasNoSessions));

        LoadSession(SelectedSession);
        ClearStatus();
    }

    private int FindSortedSessionTargetIndex(AgentSessionRecord session)
    {
        var targetIndex = 0;
        foreach (var item in Sessions)
        {
            if (item.SessionId == session.SessionId)
            {
                continue;
            }

            if (CompareSessionOrder(session, item.Session) < 0)
            {
                break;
            }

            targetIndex++;
        }

        return targetIndex;
    }

    private static int CompareSessionOrder(AgentSessionRecord left, AgentSessionRecord right)
    {
        var updatedComparison = right.UpdatedAtUtc.CompareTo(left.UpdatedAtUtc);
        return updatedComparison != 0
            ? updatedComparison
            : right.CreatedAtUtc.CompareTo(left.CreatedAtUtc);
    }

    private void ReloadSessions(Guid? selectSessionId)
    {
        var sessions = _sessionService.ListSessions()
            .Where(session => session.ParentSessionId is null)
            .OrderByDescending(session => session.UpdatedAtUtc)
            .ThenByDescending(session => session.CreatedAtUtc)
            .ToArray();

        var currentSessionId = SelectedSession?.SessionId;

        _suppressSelectionHandlers = true;
        try
        {
            ReconcileSessions(sessions);
            OnPropertyChanged(nameof(HasNoSessions));

            var selectedSession = Sessions.FirstOrDefault(session => session.SessionId == selectSessionId);
            if (selectedSession is null && (!IsCompactLayout || selectSessionId is not null))
            {
                selectedSession = Sessions.FirstOrDefault(session => session.SessionId == currentSessionId)
                                  ?? Sessions.FirstOrDefault();
            }

            SelectedSession = selectedSession;
        }
        finally
        {
            _suppressSelectionHandlers = false;
        }

        LoadSession(SelectedSession);
        ClearStatus();
    }

    private void ReconcileSessions(IReadOnlyList<AgentSessionRecord> sessions)
    {
        var desiredSessionIds = sessions.Select(session => session.SessionId).ToHashSet();
        for (var index = Sessions.Count - 1; index >= 0; index--)
        {
            if (!desiredSessionIds.Contains(Sessions[index].SessionId))
            {
                Sessions.RemoveAt(index);
            }
        }

        for (var index = 0; index < sessions.Count; index++)
        {
            var session = sessions[index];
            var existingIndex = FindSessionIndex(session.SessionId);
            var checkpoint = _sessionService.GetLatestCheckpoint(session.SessionId);
            if (existingIndex < 0)
            {
                Sessions.Insert(index, new AgentSessionListEntryViewModel(session, checkpoint));
                continue;
            }

            var item = Sessions[existingIndex];
            item.UpdateSession(session);
            item.ApplyCheckpoint(checkpoint);
            if (existingIndex != index)
            {
                Sessions.Move(existingIndex, index);
            }
        }
    }

    private int FindSessionIndex(Guid sessionId)
    {
        for (var index = 0; index < Sessions.Count; index++)
        {
            if (Sessions[index].SessionId == sessionId)
            {
                return index;
            }
        }

        return -1;
    }

    private void LoadSession(AgentSessionListEntryViewModel? session)
    {
        Title = session?.Title ?? string.Empty;
        DetailStateText = session?.StatusText ?? "No session selected.";
        DetailCreatedText = session is null ? string.Empty : $"Created: {FormatTimestamp(session.Session.CreatedAtUtc)}";
        DetailUpdatedText = session is null ? string.Empty : $"Updated: {FormatTimestamp(session.Session.UpdatedAtUtc)}";
    }

    private void RunOnUiThread(Action action)
    {
        if (Avalonia.Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
    }

    private static string FormatTimestamp(DateTimeOffset value)
        => value.ToLocalTime().ToString("g");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelSuccessStatusClear();
        _sessionService.SessionChanged -= OnSessionChanged;
    }

    private void ClearStatus()
        => SetStatus(string.Empty, AgentSessionStatusKind.None);

    private void SetStatus(string message, AgentSessionStatusKind kind, bool autoClear = false)
    {
        CancelSuccessStatusClear();
        StatusKind = string.IsNullOrWhiteSpace(message) ? AgentSessionStatusKind.None : kind;
        StatusText = message;
        if (autoClear && StatusKind == AgentSessionStatusKind.Success)
        {
            ScheduleSuccessStatusClear(message);
        }
    }

    private void ScheduleSuccessStatusClear(string message)
    {
        var cancellation = new CancellationTokenSource();
        _successStatusClearCancellation = cancellation;
        _ = ClearSuccessStatusAfterDelayAsync(message, cancellation);
    }

    private async Task ClearSuccessStatusAfterDelayAsync(string message, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(SuccessStatusDisplayDuration, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            if (_successStatusClearCancellation == cancellation
                && StatusKind == AgentSessionStatusKind.Success
                && string.Equals(StatusText, message, StringComparison.Ordinal))
            {
                ClearStatus();
            }
        });
    }

    private void CancelSuccessStatusClear()
    {
        var cancellation = _successStatusClearCancellation;
        if (cancellation is null)
        {
            return;
        }

        _successStatusClearCancellation = null;
        cancellation.Cancel();
        cancellation.Dispose();
    }
}

public enum AgentSessionStatusKind
{
    None = 0,
    Success,
    Warning,
    Error,
}

public sealed partial class AgentSessionListEntryViewModel : ObservableObject
{
    private AgentSessionRecord _session;

    public AgentSessionListEntryViewModel(AgentSessionRecord session, AgentRunCheckpointRecord? checkpoint)
    {
        _session = session;
        ApplyCheckpoint(checkpoint);
    }

    public Guid SessionId => _session.SessionId;

    public AgentSessionRecord Session => _session;

    public string Title => _session.Title;

    public string UpdatedText => _session.UpdatedAtUtc.ToLocalTime().ToString("g");

    [ObservableProperty]
    private string _statusText = "No run state recorded yet.";

    [ObservableProperty]
    private string _statusBadgeText = "Idle";

    public void UpdateSession(AgentSessionRecord session)
    {
        var oldTitle = _session.Title;
        var oldUpdatedAtUtc = _session.UpdatedAtUtc;
        _session = session;
        if (!string.Equals(oldTitle, session.Title, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(Title));
        }

        if (oldUpdatedAtUtc != session.UpdatedAtUtc)
        {
            OnPropertyChanged(nameof(UpdatedText));
        }

        OnPropertyChanged(nameof(Session));
    }

    public void ApplyCheckpoint(AgentRunCheckpointRecord? checkpoint)
    {
        if (checkpoint is null)
        {
            StatusText = "No run state recorded yet.";
            StatusBadgeText = "Idle";
            return;
        }

        StatusBadgeText = checkpoint.Status.ToString();
        StatusText = string.IsNullOrWhiteSpace(checkpoint.Summary)
            ? checkpoint.Status.ToString()
            : checkpoint.Summary!;
    }
}
