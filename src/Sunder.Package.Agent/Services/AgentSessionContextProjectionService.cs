using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services;

public sealed class AgentSessionContextProjectionService(AgentSessionService sessionService)
{
    public const int DefaultHistoricalTailTurnCount = 16;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int DefaultContextWindowTokens = 128_000;
    private const int DefaultOutputReserveTokens = 8_192;
    private const int SystemPromptReserveTokens = 4_096;
    private const int MinimumPromptBudgetTokens = 4_096;
    private const int MaxSessionContextSummaryChars = 12_000;
    private const int MaxSummarizedTurns = 48;
    private const int MaxSummaryTurnChars = 520;
    private const int MaxCompactedToolResultChars = 2_000;
    private const int MaxCompactedHistoricalTextChars = 1_200;
    private const int MaxCompactedActiveTextChars = 4_000;

    private readonly AgentSessionService _sessionService = sessionService;

    public AgentSessionPromptProjection BuildProjection(
        Guid sessionId,
        IReadOnlyList<AgentTurnRecord> turns,
        Guid activeUserTurnId,
        AgentProviderRunCapabilities runCapabilities,
        Guid? excludedTurnId = null,
        int promptOverheadTokens = 0)
    {
        if (excludedTurnId is not null)
        {
            turns = turns.Where(turn => turn.TurnId != excludedTurnId.Value).ToArray();
        }

        if (turns.Count == 0)
        {
            return new AgentSessionPromptProjection([], SummaryUpdated: false, OmittedHistoricalTurnCount: 0);
        }

        var promptBudgetTokens = EstimatePromptBudgetTokens(runCapabilities, promptOverheadTokens);
        var promptTurns = BuildPromptTurns(turns, activeUserTurnId, promptBudgetTokens);
        var omittedHistoricalTurns = ResolveOmittedHistoricalTurns(turns, promptTurns, activeUserTurnId);
        var contextCheckpoint = UpdateSessionContextCheckpoint(sessionId, omittedHistoricalTurns);
        return new AgentSessionPromptProjection(promptTurns, contextCheckpoint is not null, omittedHistoricalTurns.Count, contextCheckpoint);
    }

    private AgentSessionContextCheckpointRecord? UpdateSessionContextCheckpoint(Guid sessionId, IReadOnlyList<AgentTurnRecord> omittedHistoricalTurns)
    {
        if (omittedHistoricalTurns.Count == 0)
        {
            return null;
        }

        var details = BuildCheckpointDetails(omittedHistoricalTurns);
        var summary = RenderCheckpointSummary(details);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        var detailsJson = JsonSerializer.Serialize(details, JsonOptions);
        var existing = _sessionService.GetLatestSessionContextCheckpoint(sessionId);
        if (existing is not null
            && existing.OmittedTurnCount == omittedHistoricalTurns.Count
            && existing.LastOmittedTurnId == omittedHistoricalTurns[^1].TurnId
            && string.Equals(existing.SummaryText, summary, StringComparison.Ordinal)
            && string.Equals(existing.DetailsJson, detailsJson, StringComparison.Ordinal))
        {
            return null;
        }

        return _sessionService.SaveSessionContextCheckpoint(
            sessionId,
            omittedHistoricalTurns[0].TurnId,
            omittedHistoricalTurns[^1].TurnId,
            omittedHistoricalTurns.Count,
            summary,
            detailsJson);
    }

    private static IReadOnlyList<AgentTurnRecord> BuildPromptTurns(
        IReadOnlyList<AgentTurnRecord> turns,
        Guid activeUserTurnId,
        int promptBudgetTokens)
    {
        var selectedIndexes = SelectPromptTurnIndexes(turns, activeUserTurnId, promptBudgetTokens);
        var pairedToolCallIds = CollectToolCallIds(turns, selectedIndexes);
        pairedToolCallIds.IntersectWith(CollectToolResultIds(turns, selectedIndexes));
        var promptTurns = selectedIndexes
            .Select(index => RemoveOrphanToolItems(turns[index], pairedToolCallIds))
            .Where(turn => turn.Items.Count > 0)
            .ToArray();
        return ReduceOversizedPromptTurns(promptTurns, activeUserTurnId, promptBudgetTokens);
    }

    private static SortedSet<int> SelectPromptTurnIndexes(
        IReadOnlyList<AgentTurnRecord> turns,
        Guid activeUserTurnId,
        int promptBudgetTokens)
    {
        var activeTurnIndex = FindTurnIndex(turns, activeUserTurnId);
        if (activeTurnIndex < 0)
        {
            var fallbackCount = Math.Min(turns.Count, DefaultHistoricalTailTurnCount);
            return new SortedSet<int>(Enumerable.Range(turns.Count - fallbackCount, fallbackCount));
        }

        var historicalCount = Math.Min(activeTurnIndex, DefaultHistoricalTailTurnCount);
        var selectedIndexes = new SortedSet<int>(Enumerable.Range(activeTurnIndex - historicalCount, historicalCount + (turns.Count - activeTurnIndex)));
        AddToolPairIndexes(turns, selectedIndexes);
        TrimHistoricalTurnsToBudget(turns, selectedIndexes, activeTurnIndex, promptBudgetTokens);
        AddToolPairIndexes(turns, selectedIndexes);
        return selectedIndexes;
    }

    private static void AddToolPairIndexes(IReadOnlyList<AgentTurnRecord> turns, SortedSet<int> selectedIndexes)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            var includedToolCallIds = CollectToolCallIds(turns, selectedIndexes);
            var includedToolResultIds = CollectToolResultIds(turns, selectedIndexes);
            foreach (var index in selectedIndexes.ToArray())
            {
                foreach (var orphanedResult in turns[index].Items.Where(item => item.Kind == AgentTurnItemKind.ToolResult && !string.IsNullOrWhiteSpace(item.CallId)))
                {
                    if (includedToolCallIds.Contains(orphanedResult.CallId!))
                    {
                        continue;
                    }

                    var matchingCallIndex = FindMatchingToolCallIndex(turns, index, orphanedResult.CallId!);
                    if (matchingCallIndex >= 0 && selectedIndexes.Add(matchingCallIndex))
                    {
                        changed = true;
                    }
                }

                foreach (var orphanedCall in turns[index].Items.Where(item => item.Kind == AgentTurnItemKind.ToolCall && !string.IsNullOrWhiteSpace(item.CallId)))
                {
                    if (includedToolResultIds.Contains(orphanedCall.CallId!))
                    {
                        continue;
                    }

                    var matchingResultIndex = FindMatchingToolResultIndex(turns, index, orphanedCall.CallId!);
                    if (matchingResultIndex >= 0 && selectedIndexes.Add(matchingResultIndex))
                    {
                        changed = true;
                    }
                }
            }
        }
    }

    private static void TrimHistoricalTurnsToBudget(
        IReadOnlyList<AgentTurnRecord> turns,
        SortedSet<int> selectedIndexes,
        int activeTurnIndex,
        int promptBudgetTokens)
    {
        while (EstimateTokens(turns, selectedIndexes) > promptBudgetTokens)
        {
            var removableIndex = selectedIndexes.FirstOrDefault(index => index < activeTurnIndex);
            if (removableIndex >= activeTurnIndex || !selectedIndexes.Remove(removableIndex))
            {
                return;
            }
        }
    }

    private static IReadOnlyList<AgentTurnRecord> ReduceOversizedPromptTurns(
        IReadOnlyList<AgentTurnRecord> promptTurns,
        Guid activeUserTurnId,
        int promptBudgetTokens)
    {
        if (EstimateTokens(promptTurns) <= promptBudgetTokens)
        {
            return promptTurns;
        }

        var compactedToolResults = promptTurns.Select(CompactLargeToolResults).ToArray();
        if (EstimateTokens(compactedToolResults) <= promptBudgetTokens)
        {
            return compactedToolResults;
        }

        var activeIndex = FindTurnIndex(compactedToolResults, activeUserTurnId);
        var compactedHistoricalText = compactedToolResults
            .Select((turn, index) => index < activeIndex ? CompactTextItems(turn, MaxCompactedHistoricalTextChars) : turn)
            .ToArray();
        if (EstimateTokens(compactedHistoricalText) <= promptBudgetTokens)
        {
            return compactedHistoricalText;
        }

        var compactedNonUserActiveText = compactedHistoricalText
            .Select(turn => turn.TurnId == activeUserTurnId ? turn : CompactTextItems(turn, MaxCompactedHistoricalTextChars))
            .ToArray();
        if (EstimateTokens(compactedNonUserActiveText) <= promptBudgetTokens)
        {
            return compactedNonUserActiveText;
        }

        return compactedNonUserActiveText
            .Select(turn => turn.TurnId == activeUserTurnId ? CompactTextItems(turn, MaxCompactedActiveTextChars) : turn)
            .ToArray();
    }

    private static AgentTurnRecord CompactLargeToolResults(AgentTurnRecord turn)
    {
        if (turn.Items.All(item => item.Kind != AgentTurnItemKind.ToolResult))
        {
            return turn;
        }

        var items = turn.Items
            .Select(item => item.Kind == AgentTurnItemKind.ToolResult ? CompactToolResultItem(item) : item)
            .ToArray();
        return turn with { Items = items };
    }

    private static AgentTurnItemRecord CompactToolResultItem(AgentTurnItemRecord item)
    {
        var rendered = RenderToolResultSummary(item);
        if (string.IsNullOrWhiteSpace(rendered))
        {
            rendered = "Tool result recorded.";
        }

        var content = rendered.Length <= MaxCompactedToolResultChars
            ? rendered
            : Truncate(rendered, MaxCompactedToolResultChars);
        if (item.WasTruncated && content.Contains("[truncated]", StringComparison.Ordinal))
        {
            content = content.Replace("\n[truncated]", "", StringComparison.Ordinal);
        }

        return item with
        {
            TextContent = content + "\n[compacted for prompt budget]",
            ResultSummary = Truncate(CollapseWhitespace(rendered), Math.Min(MaxSummaryTurnChars, MaxCompactedToolResultChars)),
            StructuredPayloadJson = null,
            SourcesJson = null,
            WasTruncated = true,
        };
    }

    private static AgentTurnRecord CompactTextItems(AgentTurnRecord turn, int maxChars)
    {
        var changed = false;
        var items = turn.Items
            .Select(item =>
            {
                if (item.Kind is not (AgentTurnItemKind.Text or AgentTurnItemKind.Attachment)
                    || string.IsNullOrWhiteSpace(item.TextContent)
                    || item.TextContent.Length <= maxChars)
                {
                    return item;
                }

                changed = true;
                return item with
                {
                    TextContent = Truncate(item.TextContent, maxChars) + "\n[compacted for prompt budget]",
                    WasTruncated = true,
                };
            })
            .ToArray();
        return changed ? turn with { Items = items } : turn;
    }

    private static IReadOnlyList<AgentTurnRecord> ResolveOmittedHistoricalTurns(
        IReadOnlyList<AgentTurnRecord> turns,
        IReadOnlyList<AgentTurnRecord> promptTurns,
        Guid activeUserTurnId)
    {
        var activeTurnIndex = FindTurnIndex(turns, activeUserTurnId);
        if (activeTurnIndex <= 0)
        {
            return [];
        }

        var promptTurnIds = promptTurns.Select(turn => turn.TurnId).ToHashSet();
        return turns
            .Take(activeTurnIndex)
            .Where(turn => !promptTurnIds.Contains(turn.TurnId))
            .ToArray();
    }

    private static SessionContextCheckpointDetails BuildCheckpointDetails(IReadOnlyList<AgentTurnRecord> omittedTurns)
    {
        var files = BuildFileContext(omittedTurns);
        var transcriptExcerpts = BuildTranscriptExcerpts(omittedTurns);
        return new SessionContextCheckpointDetails(
            omittedTurns[0].CreatedAtUtc,
            omittedTurns[^1].CreatedAtUtc,
            omittedTurns.Count,
            ExtractSignals(omittedTurns, SignalKind.Goal),
            ExtractSignals(omittedTurns, SignalKind.Decision),
            ExtractSignals(omittedTurns, SignalKind.Constraint),
            BuildCurrentState(omittedTurns),
            ExtractSignals(omittedTurns, SignalKind.NextStep),
            files.ReadPaths,
            files.ModifiedPaths,
            transcriptExcerpts);
    }

    private static string RenderCheckpointSummary(SessionContextCheckpointDetails details)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Session continuity summary generated from earlier transcript turns omitted from the model prompt.");
        builder.Append("Covered omitted turns: ").Append(details.OmittedTurnCount).Append(" from ")
            .Append(details.StartedAtUtc.ToString("O")).Append(" to ").AppendLine(details.EndedAtUtc.ToString("O"));

        AppendSection(builder, "Goals and User Requests", details.Goals);
        AppendSection(builder, "Decisions", details.Decisions);
        AppendSection(builder, "Constraints and Preferences", details.Constraints);
        AppendSection(builder, "Current State", details.CurrentState);
        AppendSection(builder, "Next Steps", details.NextSteps);
        AppendSection(builder, "Files Read or Searched", details.FilesReadOrSearched);
        AppendSection(builder, "Files Modified", details.FilesModified);
        AppendSection(builder, "Recent Omitted Transcript Excerpts", details.TranscriptExcerpts);
        return Truncate(builder.ToString().Trim(), MaxSessionContextSummaryChars);
    }

    private static void AppendSection(StringBuilder builder, string title, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        builder.AppendLine().Append("## ").AppendLine(title);
        foreach (var item in items)
        {
            builder.Append("- ").AppendLine(item);
        }
    }

    private static IReadOnlyList<string> BuildTranscriptExcerpts(IReadOnlyList<AgentTurnRecord> omittedTurns)
    {
        var summarizedTurns = omittedTurns.TakeLast(MaxSummarizedTurns).ToArray();
        var excerpts = new List<string>();
        if (summarizedTurns.Length < omittedTurns.Count)
        {
            excerpts.Add($"Earlier {omittedTurns.Count - summarizedTurns.Length} omitted turns are represented only by the covered range above.");
        }

        foreach (var turn in summarizedTurns)
        {
            var text = RenderTurnSummaryText(turn);
            if (!string.IsNullOrWhiteSpace(text))
            {
                excerpts.Add($"{RenderRole(turn)}: {Truncate(CollapseWhitespace(text), MaxSummaryTurnChars)}");
            }
        }

        return excerpts;
    }

    private static FileContext BuildFileContext(IReadOnlyList<AgentTurnRecord> turns)
    {
        var readPaths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var modifiedPaths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in turns.SelectMany(turn => turn.Items).Where(item => item.Kind == AgentTurnItemKind.ToolCall))
        {
            CollectFileOperation(item, readPaths, modifiedPaths);
        }

        return new FileContext(readPaths.Take(24).ToArray(), modifiedPaths.Take(24).ToArray());
    }

    private static IReadOnlyList<string> ExtractSignals(IReadOnlyList<AgentTurnRecord> turns, SignalKind kind)
        => turns
            .Where(turn => ShouldInspectForSignal(turn, kind))
            .Select(turn => CollapseWhitespace(RenderTurnSummaryText(turn)))
            .Where(text => !string.IsNullOrWhiteSpace(text) && MatchesSignal(text, kind))
            .TakeLast(kind == SignalKind.Goal ? 6 : 5)
            .Select(text => Truncate(text, 320))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool ShouldInspectForSignal(AgentTurnRecord turn, SignalKind kind)
        => kind switch
        {
            SignalKind.Goal or SignalKind.Constraint or SignalKind.NextStep => turn.Role == AgentMessageRole.User,
            SignalKind.Decision => turn.Role is AgentMessageRole.User or AgentMessageRole.Assistant,
            _ => false,
        };

    private static bool MatchesSignal(string text, SignalKind kind)
    {
        var normalized = text.ToLowerInvariant();
        return kind switch
        {
            SignalKind.Goal => true,
            SignalKind.Decision => ContainsAny(normalized, "decided", "decision", "we will", "we'll", "use ", "implemented", "selected", "chose"),
            SignalKind.Constraint => ContainsAny(normalized, "must", "always", "never", "do not", "don't", "avoid", "prefer", "required", "constraint"),
            SignalKind.NextStep => ContainsAny(normalized, "next", "todo", "follow up", "remaining", "left", "continue", "proceed"),
            _ => false,
        };
    }

    private static IReadOnlyList<string> BuildCurrentState(IReadOnlyList<AgentTurnRecord> omittedTurns)
    {
        var state = new List<string>();
        var latestAssistant = omittedTurns.LastOrDefault(turn => turn.Role == AgentMessageRole.Assistant && turn.Kind == AgentTurnKind.Message);
        if (latestAssistant is not null)
        {
            state.Add("Latest assistant state: " + Truncate(CollapseWhitespace(RenderTurnSummaryText(latestAssistant)), 360));
        }

        var latestToolResult = omittedTurns.LastOrDefault(turn => turn.Kind == AgentTurnKind.ToolResult);
        if (latestToolResult is not null)
        {
            state.Add("Latest tool outcome: " + Truncate(CollapseWhitespace(RenderTurnSummaryText(latestToolResult)), 300));
        }

        return state;
    }

    private static bool ContainsAny(string text, params string[] values)
        => values.Any(value => text.Contains(value, StringComparison.Ordinal));

    private static void CollectFileOperation(
        AgentTurnItemRecord item,
        ISet<string> readPaths,
        ISet<string> modifiedPaths)
    {
        if (string.IsNullOrWhiteSpace(item.ToolId) || string.IsNullOrWhiteSpace(item.ArgumentsJson))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(item.ArgumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            switch (item.ToolId.Trim().ToLowerInvariant())
            {
                case "read":
                case "grep":
                case "glob":
                    AddStringProperty(document.RootElement, "path", readPaths);
                    break;

                case "write":
                case "edit":
                    AddStringProperty(document.RootElement, "path", modifiedPaths);
                    break;

                case "apply_patch":
                    if (TryGetStringProperty(document.RootElement, "patchText", out var patchText))
                    {
                        CollectPatchPaths(patchText, modifiedPaths);
                    }
                    break;
            }
        }
        catch (JsonException)
        {
        }
    }

    private static void CollectPatchPaths(string patchText, ISet<string> modifiedPaths)
    {
        foreach (var rawLine in patchText.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            const string addPrefix = "*** Add File: ";
            const string updatePrefix = "*** Update File: ";
            const string deletePrefix = "*** Delete File: ";
            if (line.StartsWith(addPrefix, StringComparison.Ordinal))
            {
                AddPath(line[addPrefix.Length..], modifiedPaths);
            }
            else if (line.StartsWith(updatePrefix, StringComparison.Ordinal))
            {
                AddPath(line[updatePrefix.Length..], modifiedPaths);
            }
            else if (line.StartsWith(deletePrefix, StringComparison.Ordinal))
            {
                AddPath(line[deletePrefix.Length..], modifiedPaths);
            }
        }
    }

    private static void AddStringProperty(JsonElement element, string propertyName, ISet<string> paths)
    {
        if (TryGetStringProperty(element, propertyName, out var value))
        {
            AddPath(value, paths);
        }
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string value)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        value = string.Empty;
        return false;
    }

    private static void AddPath(string path, ISet<string> paths)
    {
        path = path.Trim();
        if (!string.IsNullOrWhiteSpace(path))
        {
            paths.Add(path);
        }
    }

    private static string RenderTurnSummaryText(AgentTurnRecord turn)
    {
        var parts = new List<string>();
        foreach (var item in turn.Items.OrderBy(item => item.SequenceNumber))
        {
            switch (item.Kind)
            {
                case AgentTurnItemKind.Text when !string.IsNullOrWhiteSpace(item.TextContent):
                    parts.Add(item.TextContent.Trim());
                    break;

                case AgentTurnItemKind.ToolCall:
                    parts.Add(RenderToolCallSummary(item));
                    break;

                case AgentTurnItemKind.ToolResult:
                    parts.Add(RenderToolResultSummary(item));
                    break;

                case AgentTurnItemKind.Attachment:
                    parts.Add("Attachment provided.");
                    break;
            }
        }

        return string.Join("; ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string RenderToolCallSummary(AgentTurnItemRecord item)
        => string.IsNullOrWhiteSpace(item.ToolId)
            ? "Tool call requested."
            : $"Tool call `{item.ToolId}` requested.";

    private static string RenderToolResultSummary(AgentTurnItemRecord item)
        => !string.IsNullOrWhiteSpace(item.TextContent)
            ? item.TextContent.Trim()
            : !string.IsNullOrWhiteSpace(item.ResultSummary)
                ? item.ResultSummary.Trim()
                : !string.IsNullOrWhiteSpace(item.StructuredPayloadJson)
                    ? item.StructuredPayloadJson.Trim()
                    : "Tool result recorded.";

    private static string RenderRole(AgentTurnRecord turn)
        => turn.Role switch
        {
            AgentMessageRole.Assistant => "Assistant",
            AgentMessageRole.Tool => "Tool",
            AgentMessageRole.System => "System",
            _ => "User",
        };

    private static int EstimatePromptBudgetTokens(AgentProviderRunCapabilities runCapabilities, int promptOverheadTokens)
    {
        var contextWindow = runCapabilities.ContextWindowTokens is > 0
            ? runCapabilities.ContextWindowTokens.Value
            : DefaultContextWindowTokens;
        var outputReserve = runCapabilities.MaxOutputTokens is > 0
            ? Math.Min(runCapabilities.MaxOutputTokens.Value, contextWindow / 2)
            : Math.Min(DefaultOutputReserveTokens, contextWindow / 4);
        return Math.Max(MinimumPromptBudgetTokens, contextWindow - outputReserve - SystemPromptReserveTokens - Math.Max(0, promptOverheadTokens));
    }

    private static int EstimateTokens(IReadOnlyList<AgentTurnRecord> turns, IEnumerable<int> indexes)
        => indexes.Sum(index => EstimateTokens(turns[index]));

    private static int EstimateTokens(IReadOnlyList<AgentTurnRecord> turns)
        => turns.Sum(EstimateTokens);

    private static int EstimateTokens(AgentTurnRecord turn)
    {
        var chars = 0;
        foreach (var item in turn.Items)
        {
            chars += item.TextContent?.Length ?? 0;
            chars += item.ArgumentsJson?.Length ?? 0;
            chars += item.ResultSummary?.Length ?? 0;
            chars += item.StructuredPayloadJson?.Length ?? 0;
            chars += item.SourcesJson?.Length ?? 0;
        }

        return 16 + (chars / 4);
    }

    private static HashSet<string> CollectToolCallIds(IReadOnlyList<AgentTurnRecord> turns, IEnumerable<int> indexes)
        => indexes
            .SelectMany(index => turns[index].Items)
            .Where(item => item.Kind == AgentTurnItemKind.ToolCall && !string.IsNullOrWhiteSpace(item.CallId))
            .Select(item => item.CallId!)
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> CollectToolResultIds(IReadOnlyList<AgentTurnRecord> turns, IEnumerable<int> indexes)
        => indexes
            .SelectMany(index => turns[index].Items)
            .Where(item => item.Kind == AgentTurnItemKind.ToolResult && !string.IsNullOrWhiteSpace(item.CallId))
            .Select(item => item.CallId!)
            .ToHashSet(StringComparer.Ordinal);

    private static int FindMatchingToolCallIndex(IReadOnlyList<AgentTurnRecord> turns, int resultIndex, string callId)
    {
        for (var index = resultIndex - 1; index >= 0; index--)
        {
            if (turns[index].Items.Any(item => item.Kind == AgentTurnItemKind.ToolCall && string.Equals(item.CallId, callId, StringComparison.Ordinal)))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindMatchingToolResultIndex(IReadOnlyList<AgentTurnRecord> turns, int callIndex, string callId)
    {
        for (var index = callIndex + 1; index < turns.Count; index++)
        {
            if (turns[index].Items.Any(item => item.Kind == AgentTurnItemKind.ToolResult && string.Equals(item.CallId, callId, StringComparison.Ordinal)))
            {
                return index;
            }
        }

        return -1;
    }

    private static AgentTurnRecord RemoveOrphanToolItems(AgentTurnRecord turn, ISet<string> pairedToolCallIds)
    {
        var filteredItems = turn.Items
            .Where(item => item.Kind switch
            {
                AgentTurnItemKind.ToolCall or AgentTurnItemKind.ToolResult => !string.IsNullOrWhiteSpace(item.CallId) && pairedToolCallIds.Contains(item.CallId!),
                _ => true,
            })
            .ToArray();
        return filteredItems.Length == turn.Items.Count ? turn : turn with { Items = filteredItems };
    }

    private static int FindTurnIndex(IReadOnlyList<AgentTurnRecord> turns, Guid turnId)
    {
        for (var index = 0; index < turns.Count; index++)
        {
            if (turns[index].TurnId == turnId)
            {
                return index;
            }
        }

        return -1;
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWasWhitespace = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!lastWasWhitespace)
                {
                    builder.Append(' ');
                }

                lastWasWhitespace = true;
            }
            else
            {
                builder.Append(character);
                lastWasWhitespace = false;
            }
        }

        return builder.ToString().Trim();
    }

    private static string Truncate(string text, int maxChars)
        => text.Length <= maxChars
            ? text
            : text[..maxChars].TrimEnd() + "\n[truncated]";
}

public sealed record AgentSessionPromptProjection(
    IReadOnlyList<AgentTurnRecord> PromptTurns,
    bool SummaryUpdated,
    int OmittedHistoricalTurnCount,
    AgentSessionContextCheckpointRecord? ContextCheckpoint = null);

internal sealed record SessionContextCheckpointDetails(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    int OmittedTurnCount,
    IReadOnlyList<string> Goals,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> CurrentState,
    IReadOnlyList<string> NextSteps,
    IReadOnlyList<string> FilesReadOrSearched,
    IReadOnlyList<string> FilesModified,
    IReadOnlyList<string> TranscriptExcerpts);

internal sealed record FileContext(
    IReadOnlyList<string> ReadPaths,
    IReadOnlyList<string> ModifiedPaths);

internal enum SignalKind
{
    Goal,
    Decision,
    Constraint,
    NextStep,
}
