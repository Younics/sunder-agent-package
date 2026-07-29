using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.PackageViews;

public sealed record HistoryFilterOptionViewModel(string Id, string DisplayName, string? ParentId = null);

public sealed record HistoryChoiceViewModel(string Id, string DisplayName);

public sealed record HistoryActiveFilterChipViewModel(string Key, string Label)
{
    public string AutomationName => $"Remove filter: {Label}";
}

public sealed class AgentHistorySearchResultViewModel
{
    internal AgentHistorySearchResultViewModel(HistorySearchHit hit)
    {
        Hit = hit;
        WorkspaceName = hit.WorkspaceName;
        SessionTitle = hit.SessionTitle;
        TimestampText = hit.TimestampUtc.ToLocalTime().ToString("g");
        IsUserResult = hit.AnchorKind == HistoryAnchorKind.Text && hit.Role == AgentMessageRole.User;
        IsAssistantResult = hit.AnchorKind == HistoryAnchorKind.Text
                            && hit.Role == AgentMessageRole.Assistant;
        IsActivityResult = hit.AnchorKind == HistoryAnchorKind.Activity;
        SenderText = IsUserResult
            ? "User"
            : IsAssistantResult ? "Assistant" : "Activity";
        HeaderText = $"{SenderText} - {TimestampText}";
        RoleActivityText = IsActivityResult ? hit.Activity.ToString() : SenderText;
        ActivityLabel = IsActivityResult && hit.Activity != HistoryActivityKind.None
            ? $"{hit.Activity} activity"
            : string.Empty;
        Snippet = hit.Snippet;
        Paths = Distinct(hit.Paths);
        DisplayReasons = hit.MatchReasons
            .Where(static reason => !string.Equals(reason, "Recent history", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        MatchReasons = DisplayReasons;
        IsChildSession = hit.IsChildSession;
        ContextText = hit.IsChildSession
            ? $"{hit.SessionTitle} - {hit.WorkspaceName} - child session"
            : $"{hit.SessionTitle} - {hit.WorkspaceName}";
        WorkspaceContext = ContextText;
        HasPaths = Paths.Count > 0;
        HasActivityLabel = ActivityLabel.Length > 0;
        HasMatchReasons = DisplayReasons.Count > 0;
        PathLabel = Paths.Count == 1 ? "Path" : "Paths";
        PathSummary = Aggregate(Paths);
        PathToolTip = string.Join(Environment.NewLine, Paths);
        DisplayReasonsText = string.Join(" - ", DisplayReasons);
        AutomationName = Bound($"Open {SenderText} history result in {SessionTitle}, {TimestampText}", 240);
        var metadata = string.Join(". ", new[]
        {
            HasPaths ? $"{PathLabel}: {PathSummary}" : null,
            HasMatchReasons ? $"Matched by {DisplayReasonsText}" : null,
        }.Where(static value => value is not null));
        AutomationHelpText = Bound(
            $"Opens the transcript at this result. {ContextText}. {Snippet}"
            + (metadata.Length == 0 ? string.Empty : $". {metadata}"),
            900);
    }

    internal HistorySearchHit Hit { get; }
    public string WorkspaceName { get; }
    public string SessionTitle { get; }
    public string WorkspaceContext { get; }
    public string TimestampText { get; }
    public string SenderText { get; }
    public string HeaderText { get; }
    public string RoleActivityText { get; }
    public string ActivityLabel { get; }
    public string Snippet { get; }
    public IReadOnlyList<string> Paths { get; }
    public IReadOnlyList<string> MatchReasons { get; }
    public IReadOnlyList<string> DisplayReasons { get; }
    public bool IsChildSession { get; }
    public bool IsUserResult { get; }
    public bool IsAssistantResult { get; }
    public bool IsActivityResult { get; }
    public bool HasActivityLabel { get; }
    public bool HasPaths { get; }
    public bool HasMatchReasons { get; }
    public string ContextText { get; }
    public string PathLabel { get; }
    public string PathSummary { get; }
    public string PathToolTip { get; }
    public string DisplayReasonsText { get; }
    public string AutomationName { get; }
    public string AutomationHelpText { get; }

    private static IReadOnlyList<string> Distinct(IReadOnlyList<string> values)
        => values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string Aggregate(IReadOnlyList<string> values)
        => string.Join(" | ", values);

    private static string Bound(string value, int maximumCharacters)
        => HistorySearchText.BoundAtRuneBoundary(value, maximumCharacters);
}
