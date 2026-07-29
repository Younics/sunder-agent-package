namespace Sunder.Package.Agent.HistorySearch;

internal sealed partial class HistorySearchService
{
    private static IReadOnlyList<FusedCandidate> Fuse(
        IReadOnlyList<HistoryLexicalCandidate> lexical,
        IReadOnlyList<HistorySemanticCandidate> semantic)
    {
        // Semantic History Search is intentionally dormant. Keep the parameter so the dormant
        // contracts remain source-compatible without allowing semantic evidence into ranking.
        _ = semantic;
        return lexical
            .Take(HistorySearchLimits.CandidateLimit)
            .Select(static candidate => new FusedCandidate(
                candidate.DocumentId,
                candidate.SourceContentRevision,
                candidate.ProjectionHash,
                candidate.Tier,
                candidate.Score,
                candidate.TimestampUtc,
                IsConsistent: true,
                candidate.Reasons,
                candidate.LaneRanks))
            .ToArray();
    }

    private sealed record FusedCandidate(
        string DocumentId,
        long SourceContentRevision,
        string ProjectionHash,
        int Tier,
        double Score,
        DateTimeOffset TimestampUtc,
        bool IsConsistent,
        IReadOnlyList<string> Reasons,
        IReadOnlyList<HistoryLexicalLaneRank> LaneRanks);

    private sealed record RankedDocument(
        HistoryStoredDocument Document,
        FusedCandidate Ranking,
        string Snippet);
}
