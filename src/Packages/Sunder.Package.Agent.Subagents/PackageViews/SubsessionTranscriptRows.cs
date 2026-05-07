using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveMarkdown.Avalonia;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Theming;

namespace Sunder.Package.Agent.Subagents.PackageViews;

public abstract class SubsessionTranscriptRowViewModel(Guid rowId, DateTimeOffset createdAtUtc) : ObservableObject
{
    public Guid RowId { get; } = rowId;

    public DateTimeOffset CreatedAtUtc { get; } = createdAtUtc;
}

public sealed class SubsessionTextTranscriptRowViewModel : SubsessionTranscriptRowViewModel
{
    private string _content;
    private ObservableStringBuilder _markdownBuilder;

    public SubsessionTextTranscriptRowViewModel(AgentTurnRecord turn, string content)
        : base(turn.TurnId, turn.CreatedAtUtc)
    {
        Role = turn.Role;
        RoleLabel = turn.Role.ToString().ToUpperInvariant();
        RoleGlyph = ResolveRoleGlyph(turn.Role);
        _content = content;
        _markdownBuilder = new ObservableStringBuilder().Append(content);
    }

    public AgentMessageRole Role { get; }

    public string RoleLabel { get; }

    public string RoleGlyph { get; }

    public bool IsUser => Role == AgentMessageRole.User;

    public bool IsNotUser => !IsUser;

    public bool IsAssistant => Role == AgentMessageRole.Assistant;

    public bool HasContent => !string.IsNullOrWhiteSpace(Content);

    public string Content
    {
        get => _content;
        private set => SetProperty(ref _content, value);
    }

    public ObservableStringBuilder MarkdownBuilder
    {
        get => _markdownBuilder;
        private set => SetProperty(ref _markdownBuilder, value);
    }

    public void UpdateContent(string content)
    {
        if (string.Equals(Content, content, StringComparison.Ordinal))
        {
            return;
        }

        Content = content;
        MarkdownBuilder.Clear();
        MarkdownBuilder.Append(content);
        OnPropertyChanged(nameof(HasContent));
    }

    private static string ResolveRoleGlyph(AgentMessageRole role)
        => role switch
        {
            AgentMessageRole.User => "U",
            AgentMessageRole.Assistant => "A",
            AgentMessageRole.System => "S",
            AgentMessageRole.Tool => "T",
            _ => "?"
        };
}

public sealed partial class SubsessionActivityTranscriptRowViewModel : SubsessionTranscriptRowViewModel, IDisposable
{
    private readonly DispatcherTimer _timer;
    private string _activityTextBase;
    private int _tick = 3;

    public SubsessionActivityTranscriptRowViewModel(string activityTextBase = "Thinking")
        : base(Guid.Empty, DateTimeOffset.UtcNow)
    {
        _activityTextBase = string.IsNullOrWhiteSpace(activityTextBase) ? "Processing" : activityTextBase.Trim();
        _thinkingText = FormatThinkingText(_activityTextBase, _tick);
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(420),
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    public string RoleGlyph => "A";

    [ObservableProperty]
    private string _thinkingText = "Thinking...";

    public void SetActivityTextBase(string activityTextBase)
    {
        var normalized = string.IsNullOrWhiteSpace(activityTextBase) ? "Processing" : activityTextBase.Trim();
        if (string.Equals(_activityTextBase, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _activityTextBase = normalized;
        ThinkingText = FormatThinkingText(_activityTextBase, _tick);
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _tick++;
        ThinkingText = FormatThinkingText(_activityTextBase, _tick);
    }

    private static string FormatThinkingText(string activityTextBase, int tick)
        => activityTextBase + new string('.', tick % 4);

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
    }
}

public sealed partial class SubsessionToolInvocationRowViewModel : SubsessionTranscriptRowViewModel
{
    private readonly AgentTurnRecord _turn;
    private readonly string _toolId;
    private readonly string _argumentsJson;
    private readonly SubsessionToolPresentationService _presentationService;
    private readonly Func<AgentTurnRecord, AgentTurnItemRecord, IReadOnlyList<SubsessionChildSessionLinkViewModel>>? _childSessionLinksResolver;
    private AgentTurnItemRecord _currentItem;
    private string _toolLabel = string.Empty;
    private string _headerDetailText = string.Empty;
    private string _statusIconText = string.Empty;
    private string _outputText = string.Empty;

    public SubsessionToolInvocationRowViewModel(
        AgentTurnRecord turn,
        AgentTurnItemRecord item,
        SubsessionToolPresentationService presentationService,
        Func<AgentTurnRecord, AgentTurnItemRecord, IReadOnlyList<SubsessionChildSessionLinkViewModel>>? childSessionLinksResolver = null)
        : base(turn.TurnId, turn.CreatedAtUtc)
    {
        _turn = turn;
        _toolId = item.ToolId ?? "unknown_tool";
        _argumentsJson = item.ArgumentsJson ?? "{}";
        _presentationService = presentationService;
        _childSessionLinksResolver = childSessionLinksResolver;
        _currentItem = item;
        ToolLabel = HumanizeToolName(_toolId);
        StatusText = item.Kind == AgentTurnItemKind.ToolResult
            ? (item.IsError ? "Failed" : "Completed")
            : "Running";
        StatusIconText = ResolveStatusIcon(StatusText);
        _isExpanded = item.Kind == AgentTurnItemKind.ToolResult && item.IsError;
        StateBrush = ResolveStateBrush(StatusText);
        StateSoftBrush = ResolveStateSoftBrush(StatusText);
        ApplyPresentationDetails(item);
    }

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private ObservableStringBuilder _detailMarkdownBuilder = new();

    [ObservableProperty]
    private IBrush _stateBrush = Brushes.Gray;

    [ObservableProperty]
    private IBrush _stateSoftBrush = Brushes.Transparent;

    public bool ShowDetails => IsExpanded && HasDetails;

    public string ExpandGlyph => IsExpanded ? "▴" : "▾";

    public string ToolLabel
    {
        get => _toolLabel;
        private set => SetProperty(ref _toolLabel, value);
    }

    public string HeaderDetailText
    {
        get => _headerDetailText;
        private set
        {
            if (SetProperty(ref _headerDetailText, value))
            {
                OnPropertyChanged(nameof(HasHeaderDetail));
            }
        }
    }

    public string StatusIconText
    {
        get => _statusIconText;
        private set => SetProperty(ref _statusIconText, value);
    }

    public string OutputText
    {
        get => _outputText;
        private set
        {
            if (SetProperty(ref _outputText, value))
            {
                OnPropertyChanged(nameof(HasOutput));
            }
        }
    }

    public bool HasDetails => DetailMarkdownBuilder.Length > 0;

    public bool HasHeaderDetail => !string.IsNullOrWhiteSpace(HeaderDetailText);

    public bool HasOutput => !string.IsNullOrWhiteSpace(OutputText);

    public bool HasMarkdownDetails => HasDetails;

    public ObservableCollection<SubsessionChildSessionLinkViewModel> ChildSessionLinks { get; } = [];

    public bool HasChildSessionLinks => ChildSessionLinks.Count > 0;

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDetails));
        OnPropertyChanged(nameof(ExpandGlyph));
    }

    [RelayCommand]
    private void ToggleExpanded()
        => IsExpanded = !IsExpanded;

    public void ApplyResult(AgentTurnRecord turn, AgentTurnItemRecord item)
    {
        StatusText = item.IsError ? "Failed" : "Completed";
        StatusIconText = ResolveStatusIcon(StatusText);
        StateBrush = ResolveStateBrush(StatusText);
        StateSoftBrush = ResolveStateSoftBrush(StatusText);
        if (item.IsError)
        {
            IsExpanded = true;
        }

        ApplyPresentationDetails(item);
        OnPropertyChanged(nameof(HasDetails));
        OnPropertyChanged(nameof(ShowDetails));
        OnPropertyChanged(nameof(HasMarkdownDetails));
    }

    private void ApplyPresentationDetails(AgentTurnItemRecord item)
    {
        _currentItem = string.IsNullOrWhiteSpace(item.ArgumentsJson) && !string.IsNullOrWhiteSpace(_argumentsJson)
            ? item with { ArgumentsJson = _argumentsJson }
            : item;
        var presentation = _presentationService.Resolve(_currentItem);
        HeaderDetailText = presentation.HeaderText?.Trim() ?? string.Empty;
        SummaryText = string.IsNullOrWhiteSpace(HeaderDetailText) ? ToolLabel : $"{ToolLabel} {HeaderDetailText}";
        DetailMarkdownBuilder.Clear();
        DetailMarkdownBuilder.Append(presentation.DetailMarkdown?.Trim() ?? string.Empty);
        OutputText = presentation.OutputText?.Trim() ?? string.Empty;
        RefreshChildSessionLink();
        OnPropertyChanged(nameof(HasDetails));
        OnPropertyChanged(nameof(ShowDetails));
        OnPropertyChanged(nameof(HasMarkdownDetails));
    }

    public void RefreshChildSessionLink()
    {
        var links = _childSessionLinksResolver?.Invoke(_turn, _currentItem) ?? [];
        var desiredSessionIds = links.Select(link => link.SessionId).ToHashSet();
        for (var index = ChildSessionLinks.Count - 1; index >= 0; index--)
        {
            if (!desiredSessionIds.Contains(ChildSessionLinks[index].SessionId))
            {
                ChildSessionLinks.RemoveAt(index);
            }
        }

        for (var index = 0; index < links.Count; index++)
        {
            var link = links[index];
            var existingIndex = FindChildSessionLinkIndex(link.SessionId);
            if (existingIndex < 0)
            {
                ChildSessionLinks.Insert(index, link);
                continue;
            }

            var existing = ChildSessionLinks[existingIndex];
            existing.Update(link.Title, link.Subtitle, ParseLinkStatus(link.StatusText));
            if (existingIndex != index)
            {
                ChildSessionLinks.Move(existingIndex, index);
            }
        }

        OnPropertyChanged(nameof(HasChildSessionLinks));
    }

    private int FindChildSessionLinkIndex(Guid sessionId)
    {
        for (var index = 0; index < ChildSessionLinks.Count; index++)
        {
            if (ChildSessionLinks[index].SessionId == sessionId)
            {
                return index;
            }
        }

        return -1;
    }

    private static AgentRunStatus ParseLinkStatus(string statusText)
        => string.Equals(statusText, "Done", StringComparison.OrdinalIgnoreCase)
            ? AgentRunStatus.Completed
            : Enum.TryParse<AgentRunStatus>(statusText, ignoreCase: true, out var status)
                ? status
                : AgentRunStatus.Idle;

    private static string HumanizeToolName(string toolId)
    {
        var parts = toolId.Split(['_', '-', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return "Tool";
        }

        return string.Join(" ", parts.Select(part => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(part.ToLowerInvariant())));
    }

    private static string ResolveStatusIcon(string statusText)
        => string.Equals(statusText, "Completed", StringComparison.OrdinalIgnoreCase)
            ? "✓"
            : string.Equals(statusText, "Running", StringComparison.OrdinalIgnoreCase)
                ? "i"
                : "!";

    private static IBrush ResolveStateBrush(string statusText)
    {
        if (TryGetBrush(
                string.Equals(statusText, "Completed", StringComparison.OrdinalIgnoreCase)
                    ? SunderThemeKeys.SuccessBrush
                    : string.Equals(statusText, "Running", StringComparison.OrdinalIgnoreCase)
                        ? SunderThemeKeys.AccentBrush
                        : SunderThemeKeys.DangerBrush) is { } brush)
        {
            return brush;
        }

        return string.Equals(statusText, "Completed", StringComparison.OrdinalIgnoreCase)
            ? Brushes.MediumSeaGreen
            : string.Equals(statusText, "Running", StringComparison.OrdinalIgnoreCase)
                ? Brushes.SteelBlue
                : Brushes.IndianRed;
    }

    private static IBrush ResolveStateSoftBrush(string statusText)
    {
        if (TryGetBrush(
                string.Equals(statusText, "Completed", StringComparison.OrdinalIgnoreCase)
                    ? SunderThemeKeys.SuccessSoftBrush
                    : string.Equals(statusText, "Running", StringComparison.OrdinalIgnoreCase)
                        ? SunderThemeKeys.InfoSoftBrush
                        : SunderThemeKeys.DangerSoftBrush) is { } brush)
        {
            return brush;
        }

        return Brushes.Transparent;
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

public sealed partial class SubsessionChildSessionLinkViewModel : ObservableObject
{
    public SubsessionChildSessionLinkViewModel(
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
        StatusIconText = status switch
        {
            AgentRunStatus.Completed => "✓",
            AgentRunStatus.Running => "i",
            _ => "!",
        };
        StateBrush = ResolveStateBrush(status);
        StateSoftBrush = ResolveStateSoftBrush(status);
    }

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
