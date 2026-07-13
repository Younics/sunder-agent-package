using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Avalonia.Theming;

namespace Sunder.Package.Agent.Subagents.PackageViews;

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
            _ => SunderThemeKeys.ForegroundMutedBrush,
        };
        return SubagentThemeBrushes.Resolve(resourceKey);
    }
}
