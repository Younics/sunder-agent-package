using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveMarkdown.Avalonia;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Theming;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentToolInvocationRowViewModel : AgentTranscriptRowViewModel
{
    private readonly AgentTurnRecord _turn;
    private readonly string _toolId;
    private readonly string _argumentsJson;
    private readonly AgentToolPresentationService _presentationService;
    private readonly Func<AgentTurnRecord, AgentTurnItemRecord, IReadOnlyList<AgentChildSessionLinkViewModel>>? _childSessionLinksResolver;
    private AgentTurnItemRecord _currentItem;
    private string _toolLabel = string.Empty;
    private string _headerDetailText = string.Empty;
    private string _statusIconText = string.Empty;
    private string _outputText = string.Empty;
    private string _errorCodeText = string.Empty;
    private string _backendText = string.Empty;

    public AgentToolInvocationRowViewModel(
        AgentTurnRecord turn,
        AgentTurnItemRecord item,
        AgentToolPresentationService presentationService,
        Func<AgentTurnRecord, AgentTurnItemRecord, IReadOnlyList<AgentChildSessionLinkViewModel>>? childSessionLinksResolver = null)
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

    public bool ShowDetails => IsExpanded && HasDetails;

    public string ExpandGlyph => IsExpanded ? "▴" : "▾";

    [ObservableProperty]
    private ObservableStringBuilder _detailMarkdownBuilder = new();

    [ObservableProperty]
    private IBrush _stateBrush = Brushes.Gray;

    [ObservableProperty]
    private IBrush _stateSoftBrush = Brushes.Transparent;

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
                OnPropertyChanged(nameof(ChildSessionDisplayText));
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

    public string ErrorCodeText
    {
        get => _errorCodeText;
        private set
        {
            if (SetProperty(ref _errorCodeText, value))
            {
                OnPropertyChanged(nameof(HasErrorCode));
                OnPropertyChanged(nameof(HasMetadata));
            }
        }
    }

    public string BackendText
    {
        get => _backendText;
        private set
        {
            if (SetProperty(ref _backendText, value))
            {
                OnPropertyChanged(nameof(HasBackend));
                OnPropertyChanged(nameof(HasMetadata));
            }
        }
    }

    public bool HasDetails => DetailMarkdownBuilder.Length > 0;

    public bool HasHeaderDetail => !string.IsNullOrWhiteSpace(HeaderDetailText);

    public bool HasOutput => !string.IsNullOrWhiteSpace(OutputText);

    public bool HasErrorCode => !string.IsNullOrWhiteSpace(ErrorCodeText);

    public bool HasBackend => !string.IsNullOrWhiteSpace(BackendText);

    public bool HasMetadata => HasErrorCode || HasBackend;

    public bool HasMarkdownDetails => HasDetails;

    public ObservableCollection<AgentChildSessionLinkViewModel> ChildSessionLinks { get; } = [];

    public AgentChildSessionLinkViewModel? ChildSessionLink => ChildSessionLinks.FirstOrDefault();

    public bool HasChildSessionLinks => ChildSessionLinks.Count > 0;

    public bool HasChildSessionLink => HasChildSessionLinks;

    public string ChildSessionDisplayText
        => ChildSessionLink is null
            ? string.Empty
            : string.IsNullOrWhiteSpace(HeaderDetailText)
                ? ChildSessionLink.DisplayText
                : HeaderDetailText;

    public bool IsFailed => string.Equals(StatusText, "Failed", StringComparison.OrdinalIgnoreCase);

    public bool IsRunning => string.Equals(StatusText, "Running", StringComparison.OrdinalIgnoreCase);

    public bool IsCompleted => string.Equals(StatusText, "Completed", StringComparison.OrdinalIgnoreCase);

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
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsCompleted));
    }

    private void ApplyPresentationDetails(AgentTurnItemRecord item)
    {
        _currentItem = item;
        var presentationItem = string.IsNullOrWhiteSpace(item.ArgumentsJson) && !string.IsNullOrWhiteSpace(_argumentsJson)
            ? item with { ArgumentsJson = _argumentsJson }
            : item;
        var presentation = _presentationService.Resolve(presentationItem);
        HeaderDetailText = presentation.HeaderText?.Trim() ?? string.Empty;
        SummaryText = BuildSummaryLine(ToolLabel, HeaderDetailText);
        DetailMarkdownBuilder.Clear();
        DetailMarkdownBuilder.Append(presentation.DetailMarkdown?.Trim() ?? string.Empty);
        OutputText = presentation.OutputText?.Trim() ?? string.Empty;
        ErrorCodeText = item.ErrorCode ?? string.Empty;
        BackendText = item.BackendId ?? string.Empty;
        RefreshChildSessionLink();
        OnPropertyChanged(nameof(HasDetails));
        OnPropertyChanged(nameof(ShowDetails));
        OnPropertyChanged(nameof(HasMarkdownDetails));
    }

    public void RefreshChildSessionLink()
        => RefreshChildSessionLinks();

    public void RefreshChildSessionLinks()
    {
        IReadOnlyList<AgentChildSessionLinkViewModel> links = [];
        if (_childSessionLinksResolver is not null)
        {
            links = _childSessionLinksResolver.Invoke(_turn, _currentItem);
        }

        if (links.Count == 0)
        {
            links = ResolveChildSessionLinks(_toolId, _currentItem, _argumentsJson);
        }

        ReconcileChildSessionLinks(links);

        OnPropertyChanged(nameof(ChildSessionLink));
        OnPropertyChanged(nameof(HasChildSessionLinks));
        OnPropertyChanged(nameof(HasChildSessionLink));
        OnPropertyChanged(nameof(ChildSessionDisplayText));
    }

    private void ReconcileChildSessionLinks(IReadOnlyList<AgentChildSessionLinkViewModel> links)
    {
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

    private static IReadOnlyList<AgentChildSessionLinkViewModel> ResolveChildSessionLinks(string toolId, AgentTurnItemRecord item, string argumentsJson)
        => toolId.ToLowerInvariant() switch
        {
            "task" => ResolveTaskChildSessionLink(item, argumentsJson) is { } link ? [link] : [],
            "delegate_tasks" => ResolveDelegateTaskChildSessionLinks(item, argumentsJson),
            _ => [],
        };

    private static AgentChildSessionLinkViewModel? ResolveTaskChildSessionLink(AgentTurnItemRecord item, string argumentsJson)
    {
        var (payloadSessionId, payloadTitle, payloadSubagentName) = ParseTaskPayload(item.StructuredPayloadJson);
        var sessionId = payloadSessionId ?? ParseGuid(item.BackendId);
        if (sessionId is null)
        {
            return null;
        }

        var (argumentDescription, argumentSubagentType) = ParseTaskArguments(argumentsJson);
        var title = FirstNonBlank(payloadTitle, argumentDescription, "Subagent session")!;
        var subtitle = FormatSubagentLabel(FirstNonBlank(payloadSubagentName, argumentSubagentType, "subagent")!);
        return new AgentChildSessionLinkViewModel(sessionId.Value, title, subtitle);
    }

    private static IReadOnlyList<AgentChildSessionLinkViewModel> ResolveDelegateTaskChildSessionLinks(AgentTurnItemRecord item, string argumentsJson)
    {
        var payloads = ParseDelegateTaskPayload(item.StructuredPayloadJson);
        if (payloads.Count == 0)
        {
            return [];
        }

        var arguments = ParseDelegateTaskArguments(argumentsJson);
        var links = new List<AgentChildSessionLinkViewModel>(payloads.Count);
        for (var index = 0; index < payloads.Count; index++)
        {
            var payload = payloads[index];
            var argument = index < arguments.Count ? arguments[index] : default;
            var sessionId = payload.SessionId;
            if (sessionId is null)
            {
                continue;
            }

            var title = FirstNonBlank(payload.Title, argument.Description, "Subagent session")!;
            var subtitle = FormatSubagentLabel(FirstNonBlank(payload.SubagentName, argument.SubagentType, "subagent")!);
            links.Add(new AgentChildSessionLinkViewModel(sessionId.Value, title, subtitle));
        }

        return links;
    }

    private static (Guid? SessionId, string? Title, string? SubagentName) ParseTaskPayload(string? structuredPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(structuredPayloadJson))
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(structuredPayloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            return (
                TryGetGuid(document.RootElement, "childSessionId"),
                TryGetString(document.RootElement, "childSessionTitle"),
                TryGetString(document.RootElement, "subagentName"));
        }
        catch
        {
            return default;
        }
    }

    private static IReadOnlyList<(Guid? SessionId, string? Title, string? SubagentName)> ParseDelegateTaskPayload(string? structuredPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(structuredPayloadJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(structuredPayloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("tasks", out var tasks)
                || tasks.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var results = new List<(Guid? SessionId, string? Title, string? SubagentName)>();
            foreach (var task in tasks.EnumerateArray())
            {
                if (task.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                results.Add((
                    TryGetGuid(task, "childSessionId"),
                    TryGetString(task, "childSessionTitle"),
                    TryGetString(task, "subagentName")));
            }

            return results;
        }
        catch
        {
            return [];
        }
    }

    private static (string? Description, string? SubagentType) ParseTaskArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            return (
                TryGetString(document.RootElement, "description"),
                TryGetString(document.RootElement, "subagent_type"));
        }
        catch
        {
            return default;
        }
    }

    private static IReadOnlyList<(string? Description, string? SubagentType)> ParseDelegateTaskArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("tasks", out var tasks)
                || tasks.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var results = new List<(string? Description, string? SubagentType)>();
            foreach (var task in tasks.EnumerateArray())
            {
                if (task.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                results.Add((
                    TryGetString(task, "description"),
                    TryGetString(task, "subagent_type")));
            }

            return results;
        }
        catch
        {
            return [];
        }
    }

    private static Guid? TryGetGuid(JsonElement element, string propertyName)
        => TryGetString(element, propertyName) is { } value ? ParseGuid(value) : null;

    private static Guid? ParseGuid(string? value)
        => Guid.TryParse(value, out var parsed) ? parsed : null;

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? Normalize(property.GetString())
            : null;

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FormatSubagentLabel(string subagentName)
        => subagentName.EndsWith("subagent", StringComparison.OrdinalIgnoreCase)
            ? subagentName
            : $"{subagentName} subagent";

    private static string BuildSummaryLine(string toolLabel, string? argumentText)
        => string.IsNullOrWhiteSpace(argumentText)
            ? toolLabel
            : $"{toolLabel} {argumentText}";

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
