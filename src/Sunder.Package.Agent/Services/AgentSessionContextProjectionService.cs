using System.Text;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Services;

public sealed class AgentSessionContextProjectionService
{
    public const int DefaultHistoricalTailTurnCount = 16;

    private const int DefaultContextWindowTokens = 128_000;
    private const int DefaultOutputReserveTokens = 8_192;
    private const int SystemPromptReserveTokens = 4_096;
    private const int MinimumPromptBudgetTokens = 4_096;
    private const int MaxSummaryTurnChars = 520;
    private const int MaxCompactedToolResultChars = 2_000;
    private const int MaxCompactedHistoricalTextChars = 1_200;
    private const int MaxCompactedActiveTextChars = 4_000;

    private readonly AgentSessionService _sessionService;
    private readonly IAgentSessionContinuityModelRefiner? _modelRefiner;

    public AgentSessionContextProjectionService(AgentSessionService sessionService)
        : this(sessionService, modelRefiner: null)
    {
    }

    internal AgentSessionContextProjectionService(
        AgentSessionService sessionService,
        IAgentSessionContinuityModelRefiner? modelRefiner)
    {
        _sessionService = sessionService;
        _modelRefiner = modelRefiner;
    }

    public AgentSessionPromptProjection BuildProjection(
        Guid sessionId,
        IReadOnlyList<AgentTurnRecord> turns,
        Guid activeUserTurnId,
        AgentProviderRunCapabilities runCapabilities,
        Guid? excludedTurnId = null,
        int promptOverheadTokens = 0)
    {
        turns = OrderTurns(turns.Where(turn => excludedTurnId is null || turn.TurnId != excludedTurnId.Value));
        if (turns.Count == 0)
        {
            return new AgentSessionPromptProjection([], SummaryUpdated: false, OmittedHistoricalTurnCount: 0);
        }

        var activeCheckpoint = _sessionService.GetActiveAnchoredSessionContextCheckpoint(sessionId);
        var boundary = SelectOmittedPrefixCount(
            turns,
            activeUserTurnId,
            EstimatePromptBudgetTokens(runCapabilities, promptOverheadTokens),
            activeCheckpoint);
        if (!IsExactCheckpointForBoundary(activeCheckpoint, turns, boundary))
        {
            activeCheckpoint = null;
            boundary = 0;
        }
        return BuildProjectionForBoundary(
            turns,
            activeUserTurnId,
            EstimatePromptBudgetTokens(runCapabilities, promptOverheadTokens),
            boundary,
            activeCheckpoint?.Record,
            summaryUpdated: false);
    }

    internal async Task<AgentSessionPromptProjection> BuildProjectionAsync(
        Guid sessionId,
        Guid activeUserTurnId,
        AgentProviderRunCapabilities runCapabilities,
        AgentProfileRecord profile,
        Guid sourceRunId,
        long sourceRunRevision,
        int promptOverheadTokens,
        CancellationToken cancellationToken)
    {
        var snapshot = _sessionService.ReadSessionContinuitySnapshot(
            sessionId,
            sourceRunId,
            sourceRunRevision);
        if (snapshot is null || snapshot.Turns.Count == 0)
        {
            return new AgentSessionPromptProjection([], SummaryUpdated: false, OmittedHistoricalTurnCount: 0);
        }

        var turns = OrderTurns(snapshot.Turns);
        var promptBudgetTokens = EstimatePromptBudgetTokens(runCapabilities, promptOverheadTokens);
        var activeCheckpoint = snapshot.ActiveCheckpoint;
        var boundary = SelectOmittedPrefixCount(
            turns,
            activeUserTurnId,
            promptBudgetTokens,
            activeCheckpoint);
        if (boundary == 0)
        {
            return BuildProjectionForBoundary(
                turns,
                activeUserTurnId,
                promptBudgetTokens,
                0,
                checkpoint: null,
                summaryUpdated: false);
        }

        if (IsExactCheckpointForBoundary(activeCheckpoint, turns, boundary))
        {
            return BuildProjectionForBoundary(
                turns,
                activeUserTurnId,
                promptBudgetTokens,
                boundary,
                activeCheckpoint!.Record,
                summaryUpdated: false);
        }

        var priorCount = activeCheckpoint?.Record.OmittedTurnCount ?? 0;
        if (priorCount > boundary || !IsExactCheckpointForBoundary(activeCheckpoint, turns, priorCount))
        {
            activeCheckpoint = null;
            priorCount = 0;
        }

        var newlyCoveredTurns = turns.Skip(priorCount).Take(boundary - priorCount).ToArray();
        var priorDetails = AgentSessionContinuitySummaryBuilder.TryReadDetails(activeCheckpoint?.Record.DetailsJson);
        var deterministicDetails = AgentSessionContinuitySummaryBuilder.BuildDeterministic(
            priorDetails,
            newlyCoveredTurns);
        var anchor = turns[boundary - 1];
        var deterministic = _sessionService.TrySaveAnchoredSessionContextCheckpoint(
            CreateSaveRequest(
                snapshot,
                boundary,
                turns[0],
                anchor,
                AgentSessionContextCheckpointKind.Deterministic,
                deterministicDetails,
                providerId: null,
                modelId: null));
        if (deterministic is null)
        {
            return BuildSafeFallbackProjection(
                turns,
                activeUserTurnId,
                promptBudgetTokens,
                activeCheckpoint);
        }

        var selectedCheckpoint = deterministic;
        if (_modelRefiner is not null)
        {
            AgentContinuityModelRefinement? refinement = null;
            try
            {
                refinement = await _modelRefiner.RefineAsync(
                    profile,
                    activeCheckpoint?.Record.SummaryText,
                    deterministicDetails,
                    newlyCoveredTurns,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // The deterministic generation is already durable; refinement is optional.
            }

            if (refinement is not null)
            {
                var refinedSnapshot = snapshot with
                {
                    ActiveContextCheckpointId = deterministic.Record.ContextCheckpointId,
                    ActiveContextGeneration = deterministic.Generation,
                };
                var refined = _sessionService.TrySaveAnchoredSessionContextCheckpoint(
                    CreateSaveRequest(
                        refinedSnapshot,
                        boundary,
                        turns[0],
                        anchor,
                        AgentSessionContextCheckpointKind.ModelRefined,
                        refinement.Summary,
                        refinement.ProviderId,
                        refinement.ModelId));
                if (refined is not null)
                {
                    selectedCheckpoint = refined;
                }
                else
                {
                    var current = _sessionService.GetActiveAnchoredSessionContextCheckpoint(sessionId);
                    if (!IsExactCheckpointForBoundary(current, turns, boundary))
                    {
                        return BuildSafeFallbackProjection(
                            turns,
                            activeUserTurnId,
                            promptBudgetTokens,
                            current);
                    }
                    selectedCheckpoint = current!;
                }
            }
        }

        var latestSnapshot = _sessionService.ReadSessionContinuitySnapshot(
            sessionId,
            sourceRunId,
            sourceRunRevision);
        if (latestSnapshot is not null)
        {
            var latestTurns = OrderTurns(latestSnapshot.Turns);
            var latestCheckpoint = latestSnapshot.ActiveCheckpoint;
            var latestBoundary = latestCheckpoint?.Record.OmittedTurnCount ?? 0;
            if (IsExactCheckpointForBoundary(latestCheckpoint, latestTurns, latestBoundary)
                && latestBoundary <= FindTurnIndex(latestTurns, activeUserTurnId))
            {
                return BuildProjectionForBoundary(
                    latestTurns,
                    activeUserTurnId,
                    promptBudgetTokens,
                    latestBoundary,
                    latestCheckpoint!.Record,
                    summaryUpdated: true);
            }

            return BuildSafeFallbackProjection(
                latestTurns,
                activeUserTurnId,
                promptBudgetTokens,
                latestCheckpoint);
        }

        return BuildProjectionForBoundary(
            turns,
            activeUserTurnId,
            promptBudgetTokens,
            boundary,
            selectedCheckpoint.Record,
            summaryUpdated: true);
    }

    private static AgentSessionContextCheckpointSaveRequest CreateSaveRequest(
        AgentSessionContinuitySnapshot snapshot,
        int omittedTurnCount,
        AgentTurnRecord first,
        AgentTurnRecord anchor,
        AgentSessionContextCheckpointKind kind,
        AgentContinuitySummaryDocument details,
        string? providerId,
        string? modelId)
        => new(
            snapshot.SessionId,
            snapshot.TranscriptEpoch,
            snapshot.ActiveContextCheckpointId,
            snapshot.ActiveContextGeneration,
            snapshot.SourceRun,
            snapshot.SourceRunEpoch,
            first.TurnId,
            anchor.TurnId,
            omittedTurnCount,
            anchor.CreatedAtUtc,
            anchor.ContentRevision,
            kind,
            AgentSessionContinuitySummaryBuilder.RenderSummary(details, omittedTurnCount, anchor),
            AgentSessionContinuitySummaryBuilder.SerializeDetails(details),
            AgentSessionContinuitySummaryBuilder.GeneratorVersion,
            providerId,
            modelId);

    private static AgentSessionPromptProjection BuildSafeFallbackProjection(
        IReadOnlyList<AgentTurnRecord> turns,
        Guid activeUserTurnId,
        int promptBudgetTokens,
        AgentAnchoredSessionContextCheckpoint? checkpoint)
    {
        var boundary = checkpoint?.Record.OmittedTurnCount ?? 0;
        if (!IsExactCheckpointForBoundary(checkpoint, turns, boundary)
            || boundary > FindTurnIndex(turns, activeUserTurnId))
        {
            boundary = 0;
            checkpoint = null;
        }

        return BuildProjectionForBoundary(
            turns,
            activeUserTurnId,
            promptBudgetTokens,
            boundary,
            checkpoint?.Record,
            summaryUpdated: false);
    }

    private static AgentSessionPromptProjection BuildProjectionForBoundary(
        IReadOnlyList<AgentTurnRecord> turns,
        Guid activeUserTurnId,
        int promptBudgetTokens,
        int omittedPrefixCount,
        AgentSessionContextCheckpointRecord? checkpoint,
        bool summaryUpdated)
    {
        var rawTail = turns.Skip(omittedPrefixCount).ToArray();
        var pairedToolCallIds = CollectToolCallIds(rawTail, Enumerable.Range(0, rawTail.Length));
        pairedToolCallIds.IntersectWith(CollectToolResultIds(rawTail, Enumerable.Range(0, rawTail.Length)));
        var promptTurns = rawTail
            .Select(turn => RemoveOrphanToolItems(turn, pairedToolCallIds))
            .Where(turn => turn.Items.Count > 0)
            .ToArray();
        promptTurns = ReduceOversizedPromptTurns(promptTurns, activeUserTurnId, promptBudgetTokens).ToArray();
        return new AgentSessionPromptProjection(
            promptTurns,
            summaryUpdated,
            omittedPrefixCount,
            checkpoint);
    }

    private static int SelectOmittedPrefixCount(
        IReadOnlyList<AgentTurnRecord> turns,
        Guid activeUserTurnId,
        int promptBudgetTokens,
        AgentAnchoredSessionContextCheckpoint? activeCheckpoint)
    {
        var activeTurnIndex = FindTurnIndex(turns, activeUserTurnId);
        if (activeTurnIndex <= 0)
        {
            return 0;
        }

        var activeBoundary = IsExactCheckpointForBoundary(
            activeCheckpoint,
            turns,
            activeCheckpoint?.Record.OmittedTurnCount ?? 0)
            ? activeCheckpoint!.Record.OmittedTurnCount
            : 0;
        if (activeBoundary > activeTurnIndex)
        {
            activeBoundary = 0;
        }

        var boundary = Math.Max(
            activeBoundary,
            Math.Max(0, activeTurnIndex - DefaultHistoricalTailTurnCount));
        while (boundary < activeTurnIndex
               && EstimateTokens(turns, Enumerable.Range(boundary, turns.Count - boundary)) > promptBudgetTokens)
        {
            boundary++;
        }

        return MoveBoundaryOutsideHistoricalToolExchanges(
            turns,
            boundary,
            activeTurnIndex,
            activeBoundary);
    }

    private static int MoveBoundaryOutsideHistoricalToolExchanges(
        IReadOnlyList<AgentTurnRecord> turns,
        int boundary,
        int activeTurnIndex,
        int minimumBoundary)
    {
        var callIndexes = CollectToolItemIndexes(turns, AgentTurnItemKind.ToolCall);
        var resultIndexes = CollectToolItemIndexes(turns, AgentTurnItemKind.ToolResult);
        for (var pass = 0; pass < turns.Count; pass++)
        {
            var changed = false;
            foreach (var callId in callIndexes.Keys.Intersect(resultIndexes.Keys, StringComparer.Ordinal))
            {
                var first = Math.Min(callIndexes[callId].Min(), resultIndexes[callId].Min());
                var last = Math.Max(callIndexes[callId].Max(), resultIndexes[callId].Max());
                if (first >= boundary || last < boundary)
                {
                    continue;
                }

                var adjusted = last < activeTurnIndex
                    ? last + 1
                    : first >= minimumBoundary
                        ? first
                        : minimumBoundary;
                if (adjusted != boundary)
                {
                    boundary = adjusted;
                    changed = true;
                }
            }

            var lastUnpairedHistoricalToolTurn = Enumerable.Range(boundary, activeTurnIndex - boundary)
                .Where(index => turns[index].Items.Any(item =>
                    (item.Kind is AgentTurnItemKind.ToolCall or AgentTurnItemKind.ToolResult)
                    && (string.IsNullOrWhiteSpace(item.CallId)
                        || !callIndexes.ContainsKey(item.CallId)
                        || !resultIndexes.ContainsKey(item.CallId))))
                .DefaultIfEmpty(-1)
                .Max();
            if (lastUnpairedHistoricalToolTurn >= boundary)
            {
                boundary = lastUnpairedHistoricalToolTurn + 1;
                changed = true;
            }

            boundary = Math.Clamp(boundary, minimumBoundary, activeTurnIndex);
            if (!changed)
            {
                break;
            }
        }

        return boundary;
    }

    private static Dictionary<string, List<int>> CollectToolItemIndexes(
        IReadOnlyList<AgentTurnRecord> turns,
        AgentTurnItemKind kind)
    {
        var indexes = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var index = 0; index < turns.Count; index++)
        {
            foreach (var item in turns[index].Items.Where(item => item.Kind == kind && !string.IsNullOrWhiteSpace(item.CallId)))
            {
                if (!indexes.TryGetValue(item.CallId!, out var values))
                {
                    values = [];
                    indexes[item.CallId!] = values;
                }
                values.Add(index);
            }
        }
        return indexes;
    }

    private static bool IsExactCheckpointForBoundary(
        AgentAnchoredSessionContextCheckpoint? checkpoint,
        IReadOnlyList<AgentTurnRecord> turns,
        int boundary)
    {
        if (checkpoint is null
            || checkpoint.Kind == AgentSessionContextCheckpointKind.Legacy
            || boundary <= 0
            || checkpoint.Record.OmittedTurnCount != boundary
            || checkpoint.Record.FirstOmittedTurnId is null
            || checkpoint.Record.LastOmittedTurnId is null
            || checkpoint.CoveredThroughCreatedAtUtc is null
            || checkpoint.CoveredThroughContentRevision is null
            || turns.Count < boundary)
        {
            return false;
        }

        var anchor = turns[boundary - 1];
        return turns[0].TurnId == checkpoint.Record.FirstOmittedTurnId
               && anchor.TurnId == checkpoint.Record.LastOmittedTurnId
               && anchor.CreatedAtUtc == checkpoint.CoveredThroughCreatedAtUtc
               && anchor.ContentRevision == checkpoint.CoveredThroughContentRevision;
    }

    private static IReadOnlyList<AgentTurnRecord> OrderTurns(IEnumerable<AgentTurnRecord> turns)
        => turns
            .OrderBy(turn => turn.CreatedAtUtc)
            .ThenBy(turn => turn.TurnId.ToString("D"), StringComparer.Ordinal)
            .ToArray();

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

    private static string RenderToolResultSummary(AgentTurnItemRecord item)
        => !string.IsNullOrWhiteSpace(item.TextContent)
            ? item.TextContent.Trim()
            : !string.IsNullOrWhiteSpace(item.ResultSummary)
                ? item.ResultSummary.Trim()
                : !string.IsNullOrWhiteSpace(item.StructuredPayloadJson)
                    ? item.StructuredPayloadJson.Trim()
                    : "Tool result recorded.";

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
