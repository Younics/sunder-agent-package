using System.Text;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services.BehaviorLoops;

namespace Sunder.Package.Agent.Services;

public sealed class AgentSessionContextProjectionService
{
    public const int DefaultHistoricalTailTurnCount = 16;

    private const string CompactionRunningActivity = "Running session compaction";
    private const string CompactionCompletedActivity = "Session compaction completed";
    private const string CompactionInterruptedActivity = "Session compaction interrupted";
    private const int ActiveRunTailHighWaterTurnCount = 24;
    private const int ActiveRunTailTargetTurnCount = 16;
    private const int MaxSummaryTurnChars = 520;
    private const int MaxCompactedToolResultChars = 2_000;
    private const int MaxCompactedHistoricalTextChars = 1_200;

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
        CancellationToken cancellationToken,
        int minimumOmittedTurnCount = 0)
    {
        var snapshot = _sessionService.ReadSessionContinuitySnapshot(
            sessionId,
            sourceRunId,
            sourceRunRevision);
        if (snapshot is null || snapshot.Turns.Count == 0)
        {
            throw new AgentRunTranscriptWriteRejectedException();
        }

        var turns = OrderTurns(snapshot.Turns);
        EnsureActiveUserTurn(turns, activeUserTurnId, snapshot.SourceRun, snapshot.SourceUserTurnId);
        var promptBudgetTokens = EstimatePromptBudgetTokens(runCapabilities, promptOverheadTokens);
        var activeCheckpoint = snapshot.ActiveCheckpoint;
        var boundary = SelectOmittedPrefixCount(
            turns,
            activeUserTurnId,
            promptBudgetTokens,
            activeCheckpoint,
            minimumOmittedTurnCount);
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
            return BuildFreshSafeProjection(
                sessionId,
                sourceRunId,
                sourceRunRevision,
                activeUserTurnId,
                promptBudgetTokens);
        }

        ReportCompactionActivity(sessionId, sourceRunRevision, CompactionRunningActivity);
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
                ReportCompactionActivity(sessionId, sourceRunRevision, CompactionInterruptedActivity);
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
                        ReportCompactionActivity(sessionId, sourceRunRevision, CompactionInterruptedActivity);
                        return BuildFreshSafeProjection(
                            sessionId,
                            sourceRunId,
                            sourceRunRevision,
                            activeUserTurnId,
                            promptBudgetTokens);
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
            EnsureActiveUserTurn(
                latestTurns,
                activeUserTurnId,
                latestSnapshot.SourceRun,
                latestSnapshot.SourceUserTurnId);
            var latestCheckpoint = latestSnapshot.ActiveCheckpoint;
            var latestBoundary = latestCheckpoint?.Record.OmittedTurnCount ?? 0;
            if (IsExactCheckpointForBoundary(latestCheckpoint, latestTurns, latestBoundary)
                && latestBoundary <= latestTurns.Count)
            {
                ReportCompactionActivity(sessionId, sourceRunRevision, CompactionCompletedActivity);
                return BuildProjectionForBoundary(
                    latestTurns,
                    activeUserTurnId,
                    promptBudgetTokens,
                    latestBoundary,
                    latestCheckpoint!.Record,
                    summaryUpdated: true);
            }

            ReportCompactionActivity(sessionId, sourceRunRevision, CompactionInterruptedActivity);
            return BuildSafeFallbackProjection(
                latestTurns,
                activeUserTurnId,
                promptBudgetTokens,
                latestCheckpoint);
        }

        ReportCompactionActivity(sessionId, sourceRunRevision, CompactionInterruptedActivity);
        throw new AgentRunTranscriptWriteRejectedException();
    }

    private AgentSessionPromptProjection BuildFreshSafeProjection(
        Guid sessionId,
        Guid sourceRunId,
        long sourceRunRevision,
        Guid activeUserTurnId,
        int promptBudgetTokens)
    {
        var snapshot = _sessionService.ReadSessionContinuitySnapshot(
            sessionId,
            sourceRunId,
            sourceRunRevision);
        if (snapshot is null || snapshot.Turns.Count == 0)
        {
            throw new AgentRunTranscriptWriteRejectedException();
        }

        var turns = OrderTurns(snapshot.Turns);
        EnsureActiveUserTurn(turns, activeUserTurnId, snapshot.SourceRun, snapshot.SourceUserTurnId);
        return BuildSafeFallbackProjection(
            turns,
            activeUserTurnId,
            promptBudgetTokens,
            snapshot.ActiveCheckpoint);
    }

    private void ReportCompactionActivity(Guid sessionId, long sourceRunRevision, string text)
        => _sessionService.ReportRunActivity(
            sessionId,
            sourceRunRevision,
            AgentRunActivityKind.Processing,
            text);

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
            || boundary > turns.Count)
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
        var activeUserTurnIndex = FindTurnIndex(turns, activeUserTurnId);
        if (activeUserTurnIndex < 0)
        {
            throw new AgentRunTranscriptWriteRejectedException();
        }

        var rawTail = turns.Skip(omittedPrefixCount).ToArray();
        var selectedTurns = omittedPrefixCount > activeUserTurnIndex
            ? new[] { turns[activeUserTurnIndex] }.Concat(rawTail).ToArray()
            : rawTail;
        var toolExchanges = AgentToolExchangeAnalyzer.Analyze(selectedTurns);
        var promptTurns = selectedTurns
            .Select(turn => RemoveOrphanToolItems(turn, toolExchanges.PairedItemIds))
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
        AgentAnchoredSessionContextCheckpoint? activeCheckpoint,
        int minimumOmittedTurnCount = 0)
    {
        var activeTurnIndex = FindTurnIndex(turns, activeUserTurnId);
        if (activeTurnIndex < 0)
        {
            return 0;
        }

        var activeBoundary = IsExactCheckpointForBoundary(
            activeCheckpoint,
            turns,
            activeCheckpoint?.Record.OmittedTurnCount ?? 0)
            ? activeCheckpoint!.Record.OmittedTurnCount
            : 0;
        if (activeBoundary > turns.Count)
        {
            activeBoundary = 0;
        }

        var boundary = Math.Max(
            activeBoundary,
            Math.Max(0, activeTurnIndex - DefaultHistoricalTailTurnCount));
        var maximumBoundary = FindMaximumStableBoundary(turns, activeTurnIndex);
        boundary = Math.Max(
            boundary,
            Math.Min(Math.Max(0, minimumOmittedTurnCount), maximumBoundary));
        if (maximumBoundary > activeTurnIndex
            && turns.Count - boundary > ActiveRunTailHighWaterTurnCount)
        {
            boundary = Math.Min(
                maximumBoundary,
                Math.Max(boundary, turns.Count - ActiveRunTailTargetTurnCount));
        }

        while (boundary < maximumBoundary
               && EstimateTokens(turns, Enumerable.Range(boundary, turns.Count - boundary)) > promptBudgetTokens)
        {
            boundary++;
        }

        return MoveBoundaryOutsideHistoricalToolExchanges(
            turns,
            boundary,
            maximumBoundary,
            activeBoundary);
    }

    private static int FindMaximumStableBoundary(
        IReadOnlyList<AgentTurnRecord> turns,
        int activeUserTurnIndex)
    {
        var toolExchanges = AgentToolExchangeAnalyzer.Analyze(turns);
        var hasCompletedActiveExchange = false;
        var latestCompletedExchangeStart = activeUserTurnIndex;
        for (var index = activeUserTurnIndex + 1; index < turns.Count; index++)
        {
            var turn = turns[index];
            if (turn.IsStreaming
                || turn.Items.Any(item => string.Equals(
                    item.ErrorCode,
                    AgentToolResultErrorCodes.ChildWaitingForApproval,
                    StringComparison.Ordinal)))
            {
                return hasCompletedActiveExchange ? latestCompletedExchangeStart : activeUserTurnIndex;
            }

            foreach (var item in turn.Items.Where(item =>
                         item.Kind is AgentTurnItemKind.ToolCall or AgentTurnItemKind.ToolResult))
            {
                if (!toolExchanges.TryGetPair(item.ItemId, out var pair))
                {
                    return hasCompletedActiveExchange ? latestCompletedExchangeStart : activeUserTurnIndex;
                }

                if (item.Kind == AgentTurnItemKind.ToolResult)
                {
                    hasCompletedActiveExchange = true;
                    latestCompletedExchangeStart = Math.Min(index, pair.CallTurnIndex);
                }
            }
        }

        return hasCompletedActiveExchange ? latestCompletedExchangeStart : activeUserTurnIndex;
    }

    private static int MoveBoundaryOutsideHistoricalToolExchanges(
        IReadOnlyList<AgentTurnRecord> turns,
        int boundary,
        int activeTurnIndex,
        int minimumBoundary)
    {
        var toolExchanges = AgentToolExchangeAnalyzer.Analyze(turns);
        for (var pass = 0; pass < turns.Count; pass++)
        {
            var changed = false;
            foreach (var pair in toolExchanges.Pairs)
            {
                var first = pair.CallTurnIndex;
                var last = pair.ResultTurnIndex;
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
                    && toolExchanges.UnpairedItemIds.Contains(item.ItemId)))
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

        return compactedNonUserActiveText;
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
        var limits = AgentProviderRequestLimits.Resolve(runCapabilities);
        return Math.Max(1, limits.ProactiveInputLimitTokens - Math.Max(0, promptOverheadTokens));
    }

    private static int EstimateTokens(IReadOnlyList<AgentTurnRecord> turns, IEnumerable<int> indexes)
        => indexes.Sum(index => EstimateTokens(turns[index]));

    private static int EstimateTokens(IReadOnlyList<AgentTurnRecord> turns)
        => turns.Sum(EstimateTokens);

    private static int EstimateTokens(AgentTurnRecord turn)
        => AgentProviderRequestBudget.EstimateTurnTokens(turn);

    private static AgentTurnRecord RemoveOrphanToolItems(
        AgentTurnRecord turn,
        IReadOnlySet<Guid> pairedToolItemIds)
    {
        var filteredItems = turn.Items
            .Where(item => item.Kind switch
            {
                AgentTurnItemKind.ToolCall or AgentTurnItemKind.ToolResult => pairedToolItemIds.Contains(item.ItemId),
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

    private static void EnsureActiveUserTurn(
        IReadOnlyList<AgentTurnRecord> turns,
        Guid activeUserTurnId,
        AgentDurableRunKey sourceRun,
        Guid sourceUserTurnId)
    {
        var activeUserTurn = turns.FirstOrDefault(turn => turn.TurnId == activeUserTurnId);
        var hasRunStamp = activeUserTurn?.RunId is not null || activeUserTurn?.RunRevision is not null;
        if (sourceUserTurnId != Guid.Empty && sourceUserTurnId != activeUserTurnId
            || activeUserTurn is null
            || activeUserTurn.SessionId != sourceRun.SessionId
            || activeUserTurn.Role != AgentMessageRole.User
            || hasRunStamp
            && (activeUserTurn.RunId != sourceRun.RunId
                || activeUserTurn.RunRevision != sourceRun.RunRevision))
        {
            throw new AgentRunTranscriptWriteRejectedException();
        }
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
