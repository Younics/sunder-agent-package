using System.Globalization;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal enum TranscriptNavigationAnchorKind
{
    Text,
    Activity,
}

internal sealed record TranscriptNavigationTarget(
    string? WorkspaceId,
    Guid SessionId,
    Guid TurnId,
    Guid ItemId,
    DateTimeOffset CreatedAtUtc,
    string? CallId,
    TranscriptNavigationAnchorKind AnchorKind);

internal static class TranscriptAnchorNavigation
{
    internal const string WorkspaceIdKey = "workspaceId";
    internal const string SessionIdKey = "sessionId";
    internal const string TurnIdKey = "turnId";
    internal const string ItemIdKey = "itemId";
    internal const string CallIdKey = "callId";
    internal const string AnchorKindKey = "anchorKind";
    internal const string CreatedAtUtcKey = "createdAtUtc";
    private const int MaximumValueCharacters = 512;

    internal static IReadOnlyDictionary<string, string?> ToParameters(TranscriptNavigationTarget target)
        => new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [WorkspaceIdKey] = target.WorkspaceId,
            [SessionIdKey] = target.SessionId.ToString("D"),
            [TurnIdKey] = target.TurnId.ToString("D"),
            [ItemIdKey] = target.ItemId.ToString("D"),
            [CallIdKey] = target.CallId,
            [AnchorKindKey] = target.AnchorKind.ToString(),
            [CreatedAtUtcKey] = target.CreatedAtUtc.ToUniversalTime().ToString("O"),
        };

    internal static TranscriptNavigationTarget? Parse(
        IReadOnlyDictionary<string, string?> parameters,
        bool requireWorkspace)
    {
        parameters.TryGetValue(WorkspaceIdKey, out var workspaceId);
        if (requireWorkspace && string.IsNullOrWhiteSpace(workspaceId)
            || workspaceId?.Length > MaximumValueCharacters
            || !TryGuid(parameters, SessionIdKey, out var sessionId)
            || !TryGuid(parameters, TurnIdKey, out var turnId)
            || !TryGuid(parameters, ItemIdKey, out var itemId)
            || !parameters.TryGetValue(CreatedAtUtcKey, out var createdAtValue)
            || !DateTimeOffset.TryParse(
                createdAtValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var createdAtUtc)
            || !parameters.TryGetValue(AnchorKindKey, out var anchorKindValue)
            || !Enum.TryParse<TranscriptNavigationAnchorKind>(
                anchorKindValue,
                ignoreCase: true,
                out var anchorKind))
        {
            return null;
        }
        parameters.TryGetValue(CallIdKey, out var callId);
        return new TranscriptNavigationTarget(
            string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId.Trim(),
            sessionId,
            turnId,
            itemId,
            createdAtUtc.ToUniversalTime(),
            Normalize(callId),
            anchorKind);
    }

    internal static object ResolveAnchorKey(
        TranscriptNavigationTarget target,
        IReadOnlyList<AgentTurnRecord> turns)
    {
        if (target.AnchorKind == TranscriptNavigationAnchorKind.Text)
        {
            return TranscriptRowAnchorKey.Text(target.TurnId);
        }
        var turn = turns.FirstOrDefault(candidate => candidate.TurnId == target.TurnId)
                   ?? throw new InvalidOperationException("The activity anchor is no longer available.");
        var item = turn.Items.FirstOrDefault(candidate => candidate.ItemId == target.ItemId)
                   ?? turn.Items.FirstOrDefault(candidate => target.CallId is not null
                                                            && string.Equals(
                                                                candidate.CallId,
                                                                target.CallId,
                                                                StringComparison.Ordinal))
                   ?? throw new InvalidOperationException("The activity anchor is no longer available.");
        return TranscriptRowAnchorKey.Tool(turn, item);
    }

    private static bool TryGuid(
        IReadOnlyDictionary<string, string?> parameters,
        string key,
        out Guid value)
    {
        value = default;
        return parameters.TryGetValue(key, out var text)
               && text?.Length <= 36
               && Guid.TryParseExact(text, "D", out value);
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        value = value.Trim();
        return value.Length <= MaximumValueCharacters ? value : value[..MaximumValueCharacters];
    }
}
