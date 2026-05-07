using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
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
    private IBrush _stateBrush = Brushes.Gray;

    [ObservableProperty]
    private IBrush _stateSoftBrush = Brushes.Transparent;

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

    private static IBrush ResolveStateBrush(AgentRunStatus status)
    {
        var resourceKey = status switch
        {
            AgentRunStatus.Completed => SunderThemeKeys.SuccessBrush,
            AgentRunStatus.Running => SunderThemeKeys.AccentBrush,
            AgentRunStatus.Failed => SunderThemeKeys.DangerBrush,
            AgentRunStatus.Interrupted or AgentRunStatus.Stopped => SunderThemeKeys.WarningBrush,
            _ => SunderThemeKeys.ForegroundMutedBrush,
        };

        if (TryGetBrush(resourceKey) is { } brush)
        {
            return brush;
        }

        return status switch
        {
            AgentRunStatus.Completed => Brushes.MediumSeaGreen,
            AgentRunStatus.Running => Brushes.SteelBlue,
            AgentRunStatus.Failed => Brushes.IndianRed,
            AgentRunStatus.Interrupted or AgentRunStatus.Stopped => Brushes.Goldenrod,
            _ => Brushes.Gray,
        };
    }

    private static IBrush ResolveStateSoftBrush(AgentRunStatus status)
    {
        var resourceKey = status switch
        {
            AgentRunStatus.Completed => SunderThemeKeys.SuccessSoftBrush,
            AgentRunStatus.Running => SunderThemeKeys.InfoSoftBrush,
            AgentRunStatus.Failed => SunderThemeKeys.DangerSoftBrush,
            AgentRunStatus.Interrupted or AgentRunStatus.Stopped => SunderThemeKeys.WarningSoftBrush,
            _ => SunderThemeKeys.SurfacePopoverBrush,
        };

        return TryGetBrush(resourceKey) ?? Brushes.Transparent;
    }

    private static IBrush? TryGetBrush(string resourceKey)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return null;
        }

        var application = Application.Current;
        if (application?.Resources.TryGetResource(resourceKey, application.ActualThemeVariant, out var resource) == true
            && resource is IBrush brush)
        {
            return brush;
        }

        return null;
    }
}
