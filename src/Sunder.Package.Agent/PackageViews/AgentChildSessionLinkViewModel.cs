using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Theming;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChildSessionLinkViewModel : ObservableObject
{
    public AgentChildSessionLinkViewModel(
        Guid sessionId,
        string title,
        string subtitle,
        AgentRunStatus status = AgentRunStatus.Idle)
    {
        SessionId = sessionId;
        _title = title;
        _subtitle = subtitle;
        ApplyStatus(status);
    }

    public Guid SessionId { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private string _title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private string _subtitle;

    [ObservableProperty]
    private string _statusText = "Idle";

    [ObservableProperty]
    private string _statusIconText = "!";

    [ObservableProperty]
    private IBrush? _stateBrush;

    [ObservableProperty]
    private IBrush? _stateSoftBrush;

    public string DisplayText => string.IsNullOrWhiteSpace(Subtitle) ? Title : $"{Subtitle} · {Title}";

    public void Update(string title, string subtitle, AgentRunStatus status)
    {
        Title = title;
        Subtitle = subtitle;
        ApplyStatus(status);
    }

    private void ApplyStatus(AgentRunStatus status)
    {
        StatusText = status == AgentRunStatus.Completed ? "Done" : status.ToString();
        StatusIconText = ResolveStatusIcon(status);
        StateBrush = ResolveStateBrush(status);
        StateSoftBrush = ResolveStateSoftBrush(status);
    }

    private static string ResolveStatusIcon(AgentRunStatus status)
        => status switch
        {
            AgentRunStatus.Completed => "✓",
            AgentRunStatus.Running => "i",
            _ => "!",
        };

    private static IBrush? ResolveStateBrush(AgentRunStatus status)
    {
        var resourceKey = status switch
        {
            AgentRunStatus.Completed => SunderThemeKeys.SuccessBrush,
            AgentRunStatus.Running => SunderThemeKeys.AccentBrush,
            AgentRunStatus.Failed => SunderThemeKeys.DangerBrush,
            AgentRunStatus.Interrupted or AgentRunStatus.Stopped => SunderThemeKeys.WarningBrush,
            _ => SunderThemeKeys.ForegroundMutedBrush,
        };

        return AgentThemeBrushes.Resolve(resourceKey);
    }

    private static IBrush? ResolveStateSoftBrush(AgentRunStatus status)
    {
        var resourceKey = status switch
        {
            AgentRunStatus.Completed => SunderThemeKeys.SuccessSoftBrush,
            AgentRunStatus.Running => SunderThemeKeys.InfoSoftBrush,
            AgentRunStatus.Failed => SunderThemeKeys.DangerSoftBrush,
            AgentRunStatus.Interrupted or AgentRunStatus.Stopped => SunderThemeKeys.WarningSoftBrush,
            _ => SunderThemeKeys.SurfacePopoverBrush,
        };

        return AgentThemeBrushes.Resolve(resourceKey);
    }
}
