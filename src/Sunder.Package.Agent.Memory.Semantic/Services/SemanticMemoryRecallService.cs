using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Memory.Semantic.Services;

public sealed class SemanticMemoryRecallService(
    MemoryLocalStore store,
    SemanticMemoryRetrievalBackend retrievalBackend,
    SemanticMemoryMetricsService metricsService)
{
    private readonly MemoryLocalStore _store = store;
    private readonly SemanticMemoryRetrievalBackend _retrievalBackend = retrievalBackend;
    private readonly SemanticMemoryMetricsService _metricsService = metricsService;

    public async ValueTask<AgentMemoryRecallResult?> RecallAsync(
        AgentMemoryRecallRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!request.RecallPlan.ShouldRecall)
        {
            return null;
        }

        var recallQuery = string.IsNullOrWhiteSpace(request.RecallPlan.QueryText)
            ? request.Turn.UserMessage
            : request.RecallPlan.QueryText;
        var queryTokens = SemanticMemoryTextHelpers.Tokenize(recallQuery);
        var candidateSet = await _retrievalBackend.BuildRecallCandidatesAsync(
            request.Session.ProfileId,
            request.Session.SessionId,
            request.RecallPlan,
            recallQuery,
            cancellationToken);
        if (candidateSet.Memories.Count == 0)
        {
            return null;
        }

        var scored = candidateSet.Memories
            .Select(memory => ScoreMemory(
                memory,
                request.RecallPlan.Intent,
                recallQuery,
                queryTokens,
                candidateSet.TextSearchScores.TryGetValue(memory.MemoryId, out var textSearchScore) ? textSearchScore : null,
                candidateSet.SemanticScores.TryGetValue(memory.MemoryId, out var semanticScore) ? semanticScore : null))
            .Where(item => item.ShouldRecall)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Memory.IsPinned)
            .ThenByDescending(item => item.Memory.Importance)
            .ThenByDescending(item => item.Memory.UpdatedAtUtc)
            .ToList();

        if (scored.Count == 0)
        {
            return null;
        }

        var entries = BuildRecallEntries(scored, request.RecallPlan);
        _store.RecordRecall(entries.Select(entry => Guid.ParseExact(entry.MemoryId, "N")).ToArray());
        _metricsService.RecordRecall(request.RecallPlan, entries.Count);
        return entries.Count == 0 ? null : new AgentMemoryRecallResult(entries);
    }

    private static ScoredMemoryCandidate ScoreMemory(
        StoredMemoryRecord memory,
        AgentMemoryRecallIntent recallIntent,
        string query,
        IReadOnlySet<string> queryTokens,
        float? textSearchScore,
        float? semanticSimilarity)
    {
        var reasons = new List<AgentMemoryMatchReason>();
        var score = 0f;
        if (memory.IsPinned)
        {
            score += 10f;
            reasons.Add(new AgentMemoryMatchReason("pinned", "Pinned memory is always considered for recall."));
        }

        if (IsAlwaysIncludeCategory(memory.Category))
        {
            score += 3f;
            reasons.Add(new AgentMemoryMatchReason("always-include-category", $"Category '{memory.Category}' is always eligible for bounded recall."));
        }

        var queryRelevanceScore = 0f;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var normalizedQuery = SemanticMemoryTextHelpers.Normalize(query);
            var normalizedContent = SemanticMemoryTextHelpers.Normalize(memory.Content);
            if (!string.IsNullOrWhiteSpace(normalizedQuery)
                && normalizedContent.Contains(normalizedQuery, StringComparison.Ordinal))
            {
                queryRelevanceScore += 4f;
                reasons.Add(new AgentMemoryMatchReason("exact-content-match", "Stored content directly matches the current turn."));
            }

            var contentTokens = SemanticMemoryTextHelpers.Tokenize(memory.Content);
            var overlapCount = queryTokens.Intersect(contentTokens).Count();
            if (overlapCount > 0)
            {
                queryRelevanceScore += overlapCount * 1.5f;
                reasons.Add(new AgentMemoryMatchReason("token-overlap", $"Shared {overlapCount} significant query term(s) with the current turn."));
            }
        }

        if (textSearchScore is > 0f)
        {
            score += textSearchScore.Value * 3f;
            reasons.Add(new AgentMemoryMatchReason("full-text-match", $"Full-text search score {textSearchScore.Value:0.00} matched the current turn."));
        }

        if (semanticSimilarity is > 0.55f)
        {
            score += semanticSimilarity.Value * 5f;
            reasons.Add(new AgentMemoryMatchReason("semantic-similarity", $"Semantic similarity score {semanticSimilarity.Value:0.00} matched the current turn."));
        }

        if (string.Equals(memory.State, MemoryLocalStore.ContestedState, StringComparison.OrdinalIgnoreCase))
        {
            score -= 2f;
            reasons.Add(new AgentMemoryMatchReason("contested", "This memory has been marked as contested and is ranked lower."));
        }

        var hasStrongQueryMatch = queryRelevanceScore > 0f
                                  || textSearchScore is > 0f
                                  || semanticSimilarity is > 0.55f;
        var allowPinnedWithoutMatch = recallIntent is AgentMemoryRecallIntent.GeneralFact or AgentMemoryRecallIntent.Continuity;
        var shouldRecall = hasStrongQueryMatch
                           || IsAlwaysIncludeCategory(memory.Category)
                           || (memory.IsPinned && allowPinnedWithoutMatch);
        if (!shouldRecall)
        {
            return new ScoredMemoryCandidate(memory, Score: 0f, ShouldRecall: false, MatchReasons: []);
        }

        score += queryRelevanceScore;
        score += memory.Importance;
        score += memory.Confidence;
        if (memory.LastAccessedAtUtc is not null)
        {
            score += 0.25f;
            reasons.Add(new AgentMemoryMatchReason("recently-accessed", "Memory was recalled previously in this session."));
        }

        var ageDays = Math.Max(0d, (DateTimeOffset.UtcNow - memory.UpdatedAtUtc).TotalDays);
        score += ageDays < 2d ? 1.25f : ageDays < 7d ? 0.75f : 0.15f;
        if (ageDays < 2d)
        {
            reasons.Add(new AgentMemoryMatchReason("recently-updated", "Memory was updated recently in this session."));
        }

        return new ScoredMemoryCandidate(memory, score, shouldRecall, reasons);
    }

    private static IReadOnlyList<AgentMemoryRecallEntry> BuildRecallEntries(
        IReadOnlyList<ScoredMemoryCandidate> scored,
        AgentMemoryRecallPlan recallPlan)
    {
        if (ShouldDiversify(recallPlan.Intent))
        {
            var diversified = TryBuildDiversifiedEntries(scored, recallPlan);
            if (diversified.Count > 0)
            {
                return diversified;
            }
        }

        return BuildSequentialEntries(scored, recallPlan, maxPerCategory: null);
    }

    private static IReadOnlyList<AgentMemoryRecallEntry> TryBuildDiversifiedEntries(
        IReadOnlyList<ScoredMemoryCandidate> scored,
        AgentMemoryRecallPlan recallPlan)
    {
        var firstPass = BuildSequentialEntries(scored, recallPlan, maxPerCategory: 1);
        if (firstPass.Count >= recallPlan.MaxEntryCount || CountDistinctCategories(firstPass) >= Math.Min(3, recallPlan.MaxEntryCount))
        {
            return firstPass;
        }

        var secondPass = BuildSequentialEntries(scored, recallPlan, maxPerCategory: 2);
        return secondPass.Count >= firstPass.Count ? secondPass : firstPass;
    }

    private static IReadOnlyList<AgentMemoryRecallEntry> BuildSequentialEntries(
        IReadOnlyList<ScoredMemoryCandidate> scored,
        AgentMemoryRecallPlan recallPlan,
        int? maxPerCategory)
    {
        var entries = new List<AgentMemoryRecallEntry>();
        var selectedMemoryIds = new HashSet<Guid>();
        var categoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var consumedChars = 0;

        foreach (var item in scored)
        {
            if (entries.Count >= recallPlan.MaxEntryCount)
            {
                break;
            }

            if (!selectedMemoryIds.Add(item.Memory.MemoryId))
            {
                continue;
            }

            if (maxPerCategory is int categoryLimit
                && categoryCounts.TryGetValue(item.Memory.Category, out var categoryCount)
                && categoryCount >= categoryLimit
                && !item.Memory.IsPinned)
            {
                continue;
            }

            var entryLength = item.Memory.Content.Length + (item.Memory.EvidenceText?.Length ?? 0);
            if (entries.Count > 0 && consumedChars + entryLength > recallPlan.MaxChars)
            {
                break;
            }

            consumedChars += entryLength;
            categoryCounts[item.Memory.Category] = categoryCounts.TryGetValue(item.Memory.Category, out categoryCount)
                ? categoryCount + 1
                : 1;

            entries.Add(new AgentMemoryRecallEntry(
                item.Memory.MemoryId.ToString("N"),
                item.Memory.Category,
                item.Memory.Content,
                item.Memory.EvidenceText,
                item.Score,
                item.Memory.IsPinned,
                MapTrustState(item.Memory.State),
                item.Memory.SourceTurnId,
                item.MatchReasons));
        }

        return entries;
    }

    private static bool ShouldDiversify(AgentMemoryRecallIntent intent)
        => intent is AgentMemoryRecallIntent.Continuity or AgentMemoryRecallIntent.GeneralFact or AgentMemoryRecallIntent.Rationale;

    private static int CountDistinctCategories(IReadOnlyList<AgentMemoryRecallEntry> entries)
        => entries.Select(entry => entry.Category).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    private static AgentMemoryTrustState MapTrustState(string state)
        => state switch
        {
            MemoryLocalStore.ContestedState => AgentMemoryTrustState.Contested,
            MemoryLocalStore.ForgottenState => AgentMemoryTrustState.Forgotten,
            MemoryLocalStore.SupersededState => AgentMemoryTrustState.Superseded,
            _ => AgentMemoryTrustState.Active,
        };

    private static bool IsAlwaysIncludeCategory(string category)
        => string.Equals(category, "standing-instruction", StringComparison.OrdinalIgnoreCase)
           || string.Equals(category, "preference", StringComparison.OrdinalIgnoreCase);
}

internal sealed record ScoredMemoryCandidate(
    StoredMemoryRecord Memory,
    float Score,
    bool ShouldRecall,
    IReadOnlyList<AgentMemoryMatchReason> MatchReasons);
