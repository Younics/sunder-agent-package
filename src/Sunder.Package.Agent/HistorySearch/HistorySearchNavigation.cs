using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.HistorySearch;

internal sealed record HistorySearchNavigationTarget(
    string WorkspaceId,
    Guid SessionId,
    Guid TurnId,
    Guid ItemId,
    string? CallId,
    HistoryAnchorKind AnchorKind,
    DateTimeOffset CreatedAtUtc);

internal static class HistorySearchNavigation
{
    internal const string WorkspaceIdKey = TranscriptAnchorNavigation.WorkspaceIdKey;
    internal const string SessionIdKey = TranscriptAnchorNavigation.SessionIdKey;
    internal const string TurnIdKey = TranscriptAnchorNavigation.TurnIdKey;
    internal const string ItemIdKey = TranscriptAnchorNavigation.ItemIdKey;
    internal const string CallIdKey = TranscriptAnchorNavigation.CallIdKey;
    internal const string AnchorKindKey = TranscriptAnchorNavigation.AnchorKindKey;
    internal const string CreatedAtUtcKey = TranscriptAnchorNavigation.CreatedAtUtcKey;

    internal static IReadOnlyDictionary<string, string?> ToParameters(HistorySearchHit hit)
        => TranscriptAnchorNavigation.ToParameters(new TranscriptNavigationTarget(
            hit.WorkspaceId,
            hit.SessionId,
            hit.TurnId,
            hit.ItemId,
            hit.TimestampUtc,
            hit.CallId,
            hit.AnchorKind == HistoryAnchorKind.Text
                ? TranscriptNavigationAnchorKind.Text
                : TranscriptNavigationAnchorKind.Activity));

    internal static HistorySearchNavigationTarget? Parse(
        IReadOnlyDictionary<string, string?> parameters)
    {
        if (TranscriptAnchorNavigation.Parse(parameters, requireWorkspace: true) is not { } target)
        {
            return null;
        }
        return new HistorySearchNavigationTarget(
            target.WorkspaceId!,
            target.SessionId,
            target.TurnId,
            target.ItemId,
            target.CallId,
            target.AnchorKind == TranscriptNavigationAnchorKind.Text
                ? HistoryAnchorKind.Text
                : HistoryAnchorKind.Activity,
            target.CreatedAtUtc);
    }
}
