using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentSessionsViewModel : ObservableObject, IDisposable
{
    private readonly AgentSessionService _sessionService;
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
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _detailStateText = "No session selected.";

    [ObservableProperty]
    private string _detailCreatedText = string.Empty;

    [ObservableProperty]
    private string _detailUpdatedText = string.Empty;

    [ObservableProperty]
    private string _detailProfileText = string.Empty;

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
    }

    partial void OnIsCompactLayoutChanged(bool value)
    {
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
            StatusText = "Session created.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
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
            ReloadSessions(sessionId);
            StatusText = "Session saved.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
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
            _sessionService.DeleteSession(SelectedSession.SessionId);
            ReloadSessions(selectSessionId: null);
            IsEditorActive = false;
            StatusText = $"Deleted session '{deletedTitle}'.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private bool CanEditSession() => SelectedSession is not null;

    [RelayCommand]
    private void BackToSessionList()
        => IsEditorActive = false;

    [RelayCommand]
    private void OpenSessionEditor(AgentSessionListEntryViewModel? session)
    {
        if (session is null)
        {
            return;
        }

        SelectedSession = session;
        IsEditorActive = true;
    }

    private void OnSessionChanged(Guid sessionId)
        => RunOnUiThread(() => ReloadSessions(SelectedSession?.SessionId ?? sessionId));

    private void ReloadSessions(Guid? selectSessionId)
    {
        var sessions = _sessionService.ListSessions()
            .Where(session => session.ParentSessionId is null)
            .OrderByDescending(session => session.UpdatedAtUtc)
            .ThenByDescending(session => session.CreatedAtUtc)
            .ToArray();

        ReconcileSessions(sessions);
        OnPropertyChanged(nameof(HasNoSessions));

        _suppressSelectionHandlers = true;
        try
        {
            SelectedSession = Sessions.FirstOrDefault(session => session.SessionId == selectSessionId)
                ?? Sessions.FirstOrDefault(session => session.SessionId == SelectedSession?.SessionId)
                ?? Sessions.FirstOrDefault();
        }
        finally
        {
            _suppressSelectionHandlers = false;
        }

        LoadSession(SelectedSession);
        StatusText = Sessions.Count == 0 ? "No sessions yet." : $"{Sessions.Count} session(s).";
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
        DetailProfileText = string.IsNullOrWhiteSpace(session?.Session.ProfileId)
            ? "No profile bound yet."
            : session.Session.ProfileId!;
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
        _sessionService.SessionChanged -= OnSessionChanged;
    }
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
