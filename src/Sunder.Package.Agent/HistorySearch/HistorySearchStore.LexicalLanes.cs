using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Sunder.Package.Agent.HistorySearch;

internal sealed partial class HistorySearchStore
{
    private static void AddFacetLane(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long generationId,
        HistorySearchRequest request,
        HistorySessionScope? sessionScope,
        HistoryFtsQueryPlan plan,
        int candidateLimit,
        HistoryLexicalLaneKind lane,
        IDictionary<string, CandidateAccumulator> candidates)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var predicate = lane == HistoryLexicalLaneKind.FacetExact
            ? "f.NormalizedValue = $query"
            : "f.NormalizedValue LIKE $prefix ESCAPE '\\'";
        var requiredPhrasePredicate = plan.RequiredPhraseMatch is null
            ? "1 = 1"
            : """
                EXISTS (
                    SELECT 1
                    FROM HistoryDocumentsFts
                    WHERE HistoryDocumentsFts.DocumentId = f.DocumentId
                      AND CAST(HistoryDocumentsFts.GenerationId AS INTEGER) = f.GenerationId
                      AND HistoryDocumentsFts MATCH $requiredPhraseMatch
                )
                """;
        command.CommandText = $"""
            SELECT f.DocumentId, d.SourceContentRevision, d.ProjectionHash, d.CreatedAtUtc,
                   group_concat(DISTINCT f.Kind) AS Kinds,
                   MIN(CASE f.Kind
                       WHEN 'path' THEN 0 WHEN 'basename' THEN 1 WHEN 'symbol' THEN 2
                       WHEN 'activity' THEN 3 WHEN 'operation' THEN 4 ELSE 5 END) AS KindRank
            FROM HistoryDocumentFacets AS f
            INNER JOIN HistoryDocuments AS d
              ON d.GenerationId = f.GenerationId AND d.DocumentId = f.DocumentId
            WHERE f.GenerationId = $generationId
              AND {predicate}
              AND {requiredPhrasePredicate}
              AND {BuildScopePredicate("d")}
            GROUP BY f.DocumentId
            ORDER BY KindRank,
                     d.CreatedAtUtc COLLATE BINARY DESC,
                     f.DocumentId COLLATE BINARY
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$generationId", generationId);
        command.Parameters.AddWithValue("$query", plan.FacetQuery);
        command.Parameters.AddWithValue("$prefix", EscapeLike(plan.FacetQuery) + "%");
        if (plan.RequiredPhraseMatch is not null)
        {
            command.Parameters.AddWithValue("$requiredPhraseMatch", plan.RequiredPhraseMatch);
        }
        command.Parameters.AddWithValue("$limit", candidateLimit);
        AddScopeParameters(command, request, sessionScope);
        using var reader = command.ExecuteReader();
        var rank = 0;
        while (reader.Read())
        {
            var reasons = reader.GetString(4)
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .OrderBy(static kind => FacetKindOrder(kind))
                .ThenBy(static kind => kind, StringComparer.Ordinal)
                .Select(kind => MatchReasonForFacet(kind, lane))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            AddCandidate(
                candidates,
                reader.GetString(0),
                lane,
                ++rank,
                reader.GetInt64(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                reasons);
        }
    }

    private static void AddFtsLane(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long generationId,
        HistorySearchRequest request,
        HistorySessionScope? sessionScope,
        HistoryFtsLane lane,
        int candidateLimit,
        IDictionary<string, CandidateAccumulator> candidates)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT f.DocumentId, d.SourceContentRevision, d.ProjectionHash, d.CreatedAtUtc,
                   bm25(HistoryDocumentsFts, 0.0, 0.0, 1.0, 10.0, 12.0, 9.0, 7.0) AS Score
            FROM HistoryDocumentsFts AS f
            INNER JOIN HistoryDocuments AS d
              ON d.GenerationId = CAST(f.GenerationId AS INTEGER) AND d.DocumentId = f.DocumentId
            WHERE HistoryDocumentsFts MATCH $match
              AND d.GenerationId = $generationId
              AND {BuildScopePredicate("d")}
            ORDER BY Score, d.CreatedAtUtc COLLATE BINARY DESC, f.DocumentId COLLATE BINARY
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$match", lane.Match);
        command.Parameters.AddWithValue("$generationId", generationId);
        command.Parameters.AddWithValue("$limit", candidateLimit);
        AddScopeParameters(command, request, sessionScope);
        using var reader = command.ExecuteReader();
        var rank = 0;
        while (reader.Read())
        {
            AddCandidate(
                candidates,
                reader.GetString(0),
                lane.Kind,
                ++rank,
                reader.GetInt64(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                [HistorySearchRankingPolicy.GetReason(lane.Kind)]);
        }
    }

    private static void AddRecentCandidates(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long generationId,
        HistorySearchRequest request,
        HistorySessionScope? sessionScope,
        int candidateLimit,
        IDictionary<string, CandidateAccumulator> candidates)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT d.DocumentId, d.SourceContentRevision, d.ProjectionHash, d.CreatedAtUtc
            FROM HistoryDocuments AS d
            WHERE d.GenerationId = $generationId AND {BuildScopePredicate("d")}
            ORDER BY d.CreatedAtUtc COLLATE BINARY DESC, d.DocumentId COLLATE BINARY
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$generationId", generationId);
        command.Parameters.AddWithValue("$limit", candidateLimit);
        AddScopeParameters(command, request, sessionScope);
        using var reader = command.ExecuteReader();
        var rank = 0;
        while (reader.Read())
        {
            AddCandidate(
                candidates,
                reader.GetString(0),
                HistoryLexicalLaneKind.Recent,
                ++rank,
                reader.GetInt64(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                [HistorySearchRankingPolicy.GetReason(HistoryLexicalLaneKind.Recent)]);
        }
    }

    internal IReadOnlyDictionary<string, string> LoadBodySnippets(
        long generationId,
        HistoryFtsQueryPlan plan,
        IReadOnlyList<string> documentIds)
    {
        EnsureAvailable();
        if (plan.BroadBodyMatch is null || documentIds.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameters = AddInParameters(command, "$snippetDocument", documentIds);
        command.CommandText = $"""
            SELECT DocumentId,
                   snippet(HistoryDocumentsFts, 2, '', '', ' ... ', 64)
            FROM HistoryDocumentsFts
            WHERE HistoryDocumentsFts MATCH $match
              AND CAST(GenerationId AS INTEGER) = $generationId
              AND DocumentId IN ({string.Join(", ", parameters)})
            ORDER BY DocumentId COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$match", plan.BroadBodyMatch);
        command.Parameters.AddWithValue("$generationId", generationId);
        using var reader = command.ExecuteReader();
        var snippets = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var snippet = HistorySearchText.SanitizeSnippet(reader.GetString(1));
            if (snippet.Length > 0)
            {
                snippets[reader.GetString(0)] = snippet;
            }
        }
        transaction.Commit();
        return snippets;
    }

    private static IReadOnlyList<HistoryLexicalCandidate> RankCandidates(
        IEnumerable<CandidateAccumulator> candidates,
        int candidateLimit)
        => HistorySearchRankingPolicy.OrderCandidates(
            candidates.Select(static candidate => candidate.ToRankedCandidate()),
            candidateLimit);

    private static void AddCandidate(
        IDictionary<string, CandidateAccumulator> candidates,
        string documentId,
        HistoryLexicalLaneKind lane,
        int rank,
        long sourceContentRevision,
        string projectionHash,
        DateTimeOffset timestampUtc,
        IReadOnlyList<string> reasons)
    {
        if (!candidates.TryGetValue(documentId, out var candidate))
        {
            candidate = new CandidateAccumulator(
                documentId,
                sourceContentRevision,
                projectionHash,
                timestampUtc);
            candidates[documentId] = candidate;
        }
        else if (candidate.SourceContentRevision != sourceContentRevision
                 || !string.Equals(candidate.ProjectionHash, projectionHash, StringComparison.Ordinal)
                 || candidate.TimestampUtc != timestampUtc)
        {
            throw new InvalidDataException("A history candidate changed inside one read transaction.");
        }
        candidate.AddEvidence(lane, rank, reasons);
    }

    private static string MatchReasonForFacet(string kind, HistoryLexicalLaneKind lane)
    {
        var subject = kind.ToLowerInvariant() switch
        {
            "path" or "basename" or "working-directory" => "Path",
            "symbol" => "Symbol",
            "activity" or "operation" => "Activity",
            "tool" => "Tool",
            "url" => "URL",
            _ => "Metadata",
        };
        var match = lane == HistoryLexicalLaneKind.FacetExact ? "exact" : "prefix";
        return $"{subject} {match} match";
    }

    private static int FacetKindOrder(string kind)
        => kind.ToLowerInvariant() switch
        {
            "path" => 0,
            "basename" => 1,
            "symbol" => 2,
            "activity" => 3,
            "operation" => 4,
            "working-directory" => 5,
            "tool" => 6,
            "url" => 7,
            _ => 8,
        };

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private sealed class CandidateAccumulator(
        string documentId,
        long sourceContentRevision,
        string projectionHash,
        DateTimeOffset timestampUtc)
    {
        private readonly Dictionary<HistoryLexicalLaneKind, int> _laneRanks = [];
        private readonly List<string> _reasons = [];

        internal string DocumentId { get; } = documentId;
        internal long SourceContentRevision { get; } = sourceContentRevision;
        internal string ProjectionHash { get; } = projectionHash;
        internal DateTimeOffset TimestampUtc { get; } = timestampUtc;

        internal void AddEvidence(
            HistoryLexicalLaneKind lane,
            int rank,
            IReadOnlyList<string> reasons)
        {
            if (_laneRanks.TryGetValue(lane, out var currentRank))
            {
                _laneRanks[lane] = Math.Min(currentRank, rank);
            }
            else
            {
                _laneRanks[lane] = rank;
            }
            foreach (var reason in reasons)
            {
                if (!_reasons.Contains(reason, StringComparer.Ordinal))
                {
                    _reasons.Add(reason);
                }
            }
        }

        internal HistoryLexicalCandidate ToRankedCandidate()
        {
            var laneRanks = _laneRanks
                .OrderBy(static pair => HistorySearchRankingPolicy.GetTier(pair.Key))
                .ThenBy(static pair => pair.Key)
                .Select(static pair => new HistoryLexicalLaneRank(pair.Key, pair.Value))
                .ToArray();
            return new HistoryLexicalCandidate(
                DocumentId,
                Rank: 0,
                laneRanks.Min(static lane => HistorySearchRankingPolicy.GetTier(lane.Lane)),
                laneRanks.Sum(static lane => HistorySearchRankingPolicy.GetEvidenceScore(lane.Lane, lane.Rank)),
                TimestampUtc,
                SourceContentRevision,
                ProjectionHash,
                _reasons.Take(HistorySearchLimits.MaximumMatchReasons).ToArray(),
                laneRanks);
        }
    }
}
