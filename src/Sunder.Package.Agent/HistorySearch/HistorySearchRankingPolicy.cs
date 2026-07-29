namespace Sunder.Package.Agent.HistorySearch;

internal static class HistorySearchRankingPolicy
{
    private const double ReciprocalRankConstant = 60;

    internal static int GetTier(HistoryLexicalLaneKind lane)
        => lane switch
        {
            HistoryLexicalLaneKind.FacetExact or HistoryLexicalLaneKind.ExactPhrase => 0,
            HistoryLexicalLaneKind.AllExact
                or HistoryLexicalLaneKind.FacetPrefix
                or HistoryLexicalLaneKind.AllPrefix => 1,
            HistoryLexicalLaneKind.AnyExact or HistoryLexicalLaneKind.AnyPrefix => 2,
            HistoryLexicalLaneKind.Recent => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(lane)),
        };

    internal static double GetEvidenceScore(HistoryLexicalLaneKind lane, int rank)
        => GetWeight(lane) / (ReciprocalRankConstant + Math.Max(1, rank));

    internal static string GetReason(HistoryLexicalLaneKind lane)
        => lane switch
        {
            HistoryLexicalLaneKind.ExactPhrase => "Phrase match",
            HistoryLexicalLaneKind.AllExact => "All words match",
            HistoryLexicalLaneKind.AllPrefix => "All word prefixes match",
            HistoryLexicalLaneKind.AnyExact => "Any word match",
            HistoryLexicalLaneKind.AnyPrefix => "Any word prefix match",
            HistoryLexicalLaneKind.Recent => "Recent history",
            _ => throw new ArgumentOutOfRangeException(nameof(lane)),
        };

    internal static IReadOnlyList<HistoryLexicalCandidate> OrderCandidates(
        IEnumerable<HistoryLexicalCandidate> candidates,
        int limit)
        => candidates
            .OrderBy(static candidate => candidate.Tier)
            .ThenByDescending(static candidate => candidate.Score)
            .ThenByDescending(static candidate => candidate.TimestampUtc)
            .ThenBy(static candidate => candidate.DocumentId, StringComparer.Ordinal)
            .Take(limit)
            .Select(static (candidate, index) => candidate with { Rank = index + 1 })
            .ToArray();

    private static double GetWeight(HistoryLexicalLaneKind lane)
        => lane switch
        {
            HistoryLexicalLaneKind.FacetExact => 9,
            HistoryLexicalLaneKind.ExactPhrase => 8,
            HistoryLexicalLaneKind.AllExact => 5,
            HistoryLexicalLaneKind.FacetPrefix => 4,
            HistoryLexicalLaneKind.AllPrefix => 3,
            HistoryLexicalLaneKind.AnyExact => 2,
            HistoryLexicalLaneKind.AnyPrefix => 1,
            HistoryLexicalLaneKind.Recent => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(lane)),
        };
}
