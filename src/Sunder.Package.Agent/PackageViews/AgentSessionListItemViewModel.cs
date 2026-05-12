using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Theming;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentSessionListItemViewModel : ObservableObject
{
    private AgentSessionRecord _session;

    public AgentSessionListItemViewModel(AgentSessionRecord session)
    {
        _session = session;
        ApplyCheckpoint(checkpoint: null, markUnread: false);
    }

    [ObservableProperty]
    private string _draftMessage = string.Empty;

    [ObservableProperty]
    private string _statusText = "No run state recorded yet.";

    [ObservableProperty]
    private string _statusBadgeText = "Idle";

    [ObservableProperty]
    private IBrush? _statusBrush;

    [ObservableProperty]
    private bool _hasUnreadActivity;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isRunActive;

    public Guid SessionId => _session.SessionId;

    public AgentSessionRecord Session => _session;

    public Guid? ParentSessionId => _session.ParentSessionId;

    public string Title => _session.Title;

    public void UpdateSession(AgentSessionRecord session)
    {
        var oldTitle = _session.Title;
        var oldParentSessionId = _session.ParentSessionId;
        _session = session;
        if (!string.Equals(oldTitle, session.Title, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(Title));
        }

        if (oldParentSessionId != session.ParentSessionId)
        {
            OnPropertyChanged(nameof(ParentSessionId));
        }

        OnPropertyChanged(nameof(Session));
    }

    public void ApplyCheckpoint(AgentRunCheckpointRecord? checkpoint, bool markUnread)
    {
        if (checkpoint is null)
        {
            StatusText = "No run state recorded yet.";
            StatusBadgeText = "Idle";
            StatusBrush = ResolveStatusBrush(AgentRunStatus.Idle);
            IsRunActive = false;
        }
        else
        {
            StatusText = FormatCheckpoint(checkpoint);
            StatusBadgeText = checkpoint.Status switch
            {
                AgentRunStatus.Completed => "Done",
                _ => checkpoint.Status.ToString()
            };
            StatusBrush = ResolveStatusBrush(checkpoint.Status);
            IsRunActive = checkpoint.Status == AgentRunStatus.Running;
        }

        if (markUnread && !IsSelected)
        {
            HasUnreadActivity = true;
        }
    }

    public void SetTransientStatus(string statusText)
    {
        StatusText = statusText;
    }

    public void ClearUnreadActivity()
    {
        if (HasUnreadActivity)
        {
            HasUnreadActivity = false;
        }
    }

    private static string FormatCheckpoint(AgentRunCheckpointRecord checkpoint)
        => $"Current run revision: {checkpoint.RunRevision} · Status: {checkpoint.Status} · {checkpoint.Summary}";

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

        return AgentThemeBrushes.Resolve(resourceKey);
    }
}
