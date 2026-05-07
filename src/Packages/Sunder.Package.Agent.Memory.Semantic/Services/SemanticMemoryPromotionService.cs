using System.Text.RegularExpressions;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Memory.Semantic.Services;

public sealed partial class SemanticMemoryPromotionService(
    MemoryLocalStore store,
    SemanticMemoryIndexingBackgroundService indexingBackgroundService,
    SemanticMemoryMetricsService metricsService)
{
    private const int MinMergeTokenOverlap = 3;
    private const double MinMergeOverlapRatio = 0.6;

    private readonly MemoryLocalStore _store = store;
    private readonly SemanticMemoryIndexingBackgroundService _indexingBackgroundService = indexingBackgroundService;
    private readonly SemanticMemoryMetricsService _metricsService = metricsService;

    public async Task PromoteDurableMemoriesAsync(AgentLifecycleEvent lifecycleEvent, CancellationToken cancellationToken)
    {
        var candidates = ExtractPromotionCandidates(lifecycleEvent);
        if (candidates.Count == 0)
        {
            return;
        }

        var activeMemories = _store.ListActiveMemories(lifecycleEvent.Session.SessionId).ToList();
        var committedCount = 0;
        foreach (var candidate in candidates)
        {
            var mergeTarget = FindMergeCandidate(candidate, activeMemories);
            var mergedCandidate = mergeTarget is null
                ? candidate
                : MergeCandidate(candidate, mergeTarget);

            var upserted = _store.UpsertMemory(
                new MemoryUpsertRequest(
                    lifecycleEvent.Session.SessionId,
                    mergedCandidate.Category,
                    mergedCandidate.Content,
                    SemanticMemoryTextHelpers.Normalize(mergedCandidate.Content),
                    mergedCandidate.EvidenceText,
                    mergedCandidate.SourceTurnId,
                    mergedCandidate.IsPinned,
                    mergedCandidate.Importance,
                    mergedCandidate.Confidence),
                mergeTarget?.MemoryId);

            var existingIndex = activeMemories.FindIndex(memory => memory.MemoryId == upserted.MemoryId);
            if (existingIndex >= 0)
            {
                activeMemories[existingIndex] = upserted;
            }
            else
            {
                activeMemories.Add(upserted);
            }

            _indexingBackgroundService.QueueMemoryIndex(upserted.MemoryId, lifecycleEvent.Session.ProfileId);
            committedCount++;
        }

        _metricsService.RecordPromotion(candidates.Count, committedCount);
    }

    private static IReadOnlyList<MemoryCandidate> ExtractPromotionCandidates(AgentLifecycleEvent lifecycleEvent)
    {
        var candidates = new List<MemoryCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        switch (lifecycleEvent.Kind)
        {
            case AgentLifecycleEventKind.UserTurnAdded when lifecycleEvent.TriggerTurn is not null:
                AddCandidates(candidates, seen, ExtractUserCandidates(lifecycleEvent.TriggerTurn));
                break;

            case AgentLifecycleEventKind.AssistantTurnCompleted when lifecycleEvent.TriggerTurn is not null:
                AddCandidates(candidates, seen, ExtractAssistantCandidates(lifecycleEvent.TriggerTurn));
                break;

            case AgentLifecycleEventKind.ToolResultRecorded when lifecycleEvent.TriggerTurn is not null:
                AddCandidates(candidates, seen, ExtractToolResultCandidates(lifecycleEvent.TriggerTurn));
                break;

            case AgentLifecycleEventKind.RunInterrupted:
            case AgentLifecycleEventKind.RunStopped:
            case AgentLifecycleEventKind.RunFailed:
                if (SemanticMemoryTextHelpers.GetLatestTurn(lifecycleEvent.RecentLiveBufferTurns, AgentTurnKind.ToolResult) is { } recentToolResult)
                {
                    AddCandidates(candidates, seen, ExtractToolResultCandidates(recentToolResult));
                }
                break;
        }

        return candidates;
    }

    private static IReadOnlyList<MemoryCandidate> ExtractUserCandidates(AgentTurnRecord turn)
    {
        if (turn.Role != AgentMessageRole.User || turn.Kind != AgentTurnKind.Message)
        {
            return [];
        }

        var text = SemanticMemoryTextHelpers.RenderTurnText(turn);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var candidates = new List<MemoryCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var explicitRemember = RememberPattern().Match(text);
        if (explicitRemember.Success)
        {
            AddCandidate(candidates, seen, "remembered-fact", explicitRemember.Groups[1].Value, text, turn.TurnId, isPinned: true, importance: 1.0f, confidence: 0.95f);
        }

        if (PreferencePattern().IsMatch(text))
        {
            AddCandidate(candidates, seen, "preference", text, text, turn.TurnId, isPinned: false, importance: 0.85f, confidence: 0.9f);
        }

        if (StandingInstructionPattern().IsMatch(text))
        {
            AddCandidate(candidates, seen, "standing-instruction", text, text, turn.TurnId, isPinned: false, importance: 0.9f, confidence: 0.92f);
        }

        if (ProjectFactPattern().IsMatch(text))
        {
            AddCandidate(candidates, seen, "project-fact", text, text, turn.TurnId, isPinned: false, importance: 0.82f, confidence: 0.86f);
        }

        if (EnvironmentFactPattern().IsMatch(text))
        {
            AddCandidate(candidates, seen, "environment-fact", text, text, turn.TurnId, isPinned: false, importance: 0.8f, confidence: 0.84f);
        }

        if (ParticipantFactPattern().IsMatch(text))
        {
            AddCandidate(candidates, seen, "participant-fact", text, text, turn.TurnId, isPinned: false, importance: 0.8f, confidence: 0.84f);
        }

        return candidates;
    }

    private static IReadOnlyList<MemoryCandidate> ExtractAssistantCandidates(AgentTurnRecord turn)
    {
        if (turn.Role != AgentMessageRole.Assistant || turn.Kind != AgentTurnKind.Message)
        {
            return [];
        }

        var text = SemanticMemoryTextHelpers.RenderTurnText(turn);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var candidates = new List<MemoryCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddFactCandidateFromMatch(candidates, seen, "preference", text, AssistantPreferencePattern(), turn.TurnId, 0.7f, 0.68f);
        AddFactCandidateFromMatch(candidates, seen, "standing-instruction", text, AssistantStandingInstructionPattern(), turn.TurnId, 0.74f, 0.7f);
        AddFactCandidateFromMatch(candidates, seen, "project-fact", text, AssistantProjectFactPattern(), turn.TurnId, 0.72f, 0.66f);
        AddFactCandidateFromMatch(candidates, seen, "environment-fact", text, AssistantEnvironmentFactPattern(), turn.TurnId, 0.7f, 0.64f);

        return candidates;
    }

    private static IReadOnlyList<MemoryCandidate> ExtractToolResultCandidates(AgentTurnRecord turn)
    {
        if (turn.Kind != AgentTurnKind.ToolResult)
        {
            return [];
        }

        var candidates = new List<MemoryCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in turn.Items.Where(item => item.Kind == AgentTurnItemKind.ToolResult))
        {
            var evidenceText = SemanticMemoryTextHelpers.BuildToolEvidenceText(item);
            if (string.IsNullOrWhiteSpace(evidenceText))
            {
                continue;
            }

            foreach (var line in SemanticMemoryTextHelpers.EnumerateFactLines(evidenceText))
            {
                if (ToolProjectFactPattern().IsMatch(line))
                {
                    AddCandidate(candidates, seen, "project-fact", line, evidenceText, turn.TurnId, isPinned: false, importance: 0.78f, confidence: 0.74f);
                }

                if (ToolEnvironmentFactPattern().IsMatch(line))
                {
                    AddCandidate(candidates, seen, "environment-fact", line, evidenceText, turn.TurnId, isPinned: false, importance: 0.76f, confidence: 0.72f);
                }
            }
        }

        return candidates;
    }

    private static void AddCandidates(
        ICollection<MemoryCandidate> target,
        ISet<string> seen,
        IReadOnlyList<MemoryCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (seen.Add($"{candidate.Category}:{SemanticMemoryTextHelpers.Normalize(candidate.Content)}"))
            {
                target.Add(candidate);
            }
        }
    }

    private static void AddFactCandidateFromMatch(
        ICollection<MemoryCandidate> candidates,
        ISet<string> seen,
        string category,
        string evidenceText,
        Regex matcher,
        Guid sourceTurnId,
        float importance,
        float confidence)
    {
        foreach (Match match in matcher.Matches(evidenceText))
        {
            var content = SemanticMemoryTextHelpers.BuildFactContent(match.Value);
            AddCandidate(candidates, seen, category, content, evidenceText, sourceTurnId, isPinned: false, importance, confidence);
        }
    }

    private static void AddCandidate(
        ICollection<MemoryCandidate> candidates,
        ISet<string> seen,
        string category,
        string content,
        string evidenceText,
        Guid? sourceTurnId,
        bool isPinned,
        float importance,
        float confidence)
    {
        var cleanedContent = SemanticMemoryTextHelpers.Truncate(SemanticMemoryTextHelpers.BuildFactContent(content), 320);
        if (string.IsNullOrWhiteSpace(cleanedContent))
        {
            return;
        }

        var normalized = SemanticMemoryTextHelpers.Normalize(cleanedContent);
        if (string.IsNullOrWhiteSpace(normalized) || !seen.Add($"{category}:{normalized}"))
        {
            return;
        }

        candidates.Add(new MemoryCandidate(category, cleanedContent, SemanticMemoryTextHelpers.Truncate(evidenceText.Trim(), 220), sourceTurnId, isPinned, importance, confidence));
    }

    private static StoredMemoryRecord? FindMergeCandidate(MemoryCandidate candidate, IReadOnlyList<StoredMemoryRecord> activeMemories)
    {
        var candidateNormalized = SemanticMemoryTextHelpers.Normalize(candidate.Content);
        var candidateTokens = SemanticMemoryTextHelpers.Tokenize(candidate.Content);

        return activeMemories
            .Where(memory => string.Equals(memory.Category, candidate.Category, StringComparison.OrdinalIgnoreCase))
            .Select(memory => new
            {
                Memory = memory,
                NormalizedContent = SemanticMemoryTextHelpers.Normalize(memory.Content),
                OverlapCount = candidateTokens.Intersect(SemanticMemoryTextHelpers.Tokenize(memory.Content)).Count(),
                ExistingTokenCount = SemanticMemoryTextHelpers.Tokenize(memory.Content).Count
            })
            .FirstOrDefault(item =>
                string.Equals(item.NormalizedContent, candidateNormalized, StringComparison.Ordinal)
                || item.NormalizedContent.Contains(candidateNormalized, StringComparison.Ordinal)
                || candidateNormalized.Contains(item.NormalizedContent, StringComparison.Ordinal)
                || (candidateTokens.Count >= MinMergeTokenOverlap
                    && item.ExistingTokenCount >= MinMergeTokenOverlap
                    && item.OverlapCount >= MinMergeTokenOverlap
                    && item.OverlapCount / (double)Math.Min(candidateTokens.Count, item.ExistingTokenCount) >= MinMergeOverlapRatio))
            ?.Memory;
    }

    private static MemoryCandidate MergeCandidate(MemoryCandidate candidate, StoredMemoryRecord existing)
    {
        var mergedContent = SemanticMemoryTextHelpers.ChooseRicherText(existing.Content, candidate.Content) ?? candidate.Content;
        var mergedEvidence = SemanticMemoryTextHelpers.ChooseRicherText(existing.EvidenceText, candidate.EvidenceText);

        return candidate with
        {
            Content = mergedContent,
            EvidenceText = mergedEvidence ?? candidate.EvidenceText,
            SourceTurnId = candidate.SourceTurnId ?? existing.SourceTurnId,
            IsPinned = candidate.IsPinned || existing.IsPinned,
            Importance = Math.Max(candidate.Importance, existing.Importance),
            Confidence = Math.Max(candidate.Confidence, existing.Confidence),
        };
    }

    [GeneratedRegex(@"\bremember(?:\s+that|\s+this)?\b[:\s,-]*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RememberPattern();

    [GeneratedRegex(@"\b(i prefer|i like|i dislike|i don't like|please prefer)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PreferencePattern();

    [GeneratedRegex(@"\b(always|never|please|do not|don't|make sure|use)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StandingInstructionPattern();

    [GeneratedRegex(@"\b(my name is|i am |i'm |call me )\b", RegexOptions.IgnoreCase)]
    private static partial Regex ParticipantFactPattern();

    [GeneratedRegex(@"\b(user prefers|user asked for|preferred style|please keep|prefer concise|prefer detailed)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AssistantPreferencePattern();

    [GeneratedRegex(@"\b(use |avoid |make sure |do not |don't |always |never )\b", RegexOptions.IgnoreCase)]
    private static partial Regex AssistantStandingInstructionPattern();

    [GeneratedRegex(@"\b(project uses|repository uses|this project uses|stack includes|framework is|target framework|dependencies include)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AssistantProjectFactPattern();

    [GeneratedRegex(@"\b(working directory is|running on|operating system|environment uses|path is)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AssistantEnvironmentFactPattern();

    [GeneratedRegex(@"\b(this project|the project|repo|repository|stack|we use|we are using|architecture)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectFactPattern();

    [GeneratedRegex(@"\b(environment|machine|os|working directory|path|running on|local)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EnvironmentFactPattern();

    [GeneratedRegex(@"\b(project|repository|repo|framework|targetframework|dependency|dependencies|packagereference|package.json|csproj|docker|blazor|react|asp.net|dotnet|postgres|solution)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ToolProjectFactPattern();

    [GeneratedRegex(@"\b(environment|machine|os|working directory|current directory|path|windows|linux|macos|localhost|port|container)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ToolEnvironmentFactPattern();
}

internal sealed record MemoryCandidate(
    string Category,
    string Content,
    string EvidenceText,
    Guid? SourceTurnId,
    bool IsPinned,
    float Importance,
    float Confidence);
