using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.HistorySearch;

internal sealed partial class HistorySearchStore
{
    internal HistoryProjectionPin? TryPinActiveProjection()
    {
        if (!IsAvailable || _requiresSecureRecreation)
        {
            return null;
        }
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.ActiveTextGenerationId, s.ActiveEmbeddingGenerationId,
                   c.Revision, s.SemanticReady, c.EmbeddingSpaceFingerprint
            FROM HistoryProjectionState AS s
            CROSS JOIN HistoryConfiguration AS c
            INNER JOIN HistoryProjectionGenerations AS textGeneration
              ON textGeneration.GenerationId = s.ActiveTextGenerationId
            LEFT JOIN HistoryProjectionGenerations AS embeddingGeneration
              ON embeddingGeneration.GenerationId = s.ActiveEmbeddingGenerationId
            WHERE s.Id = 1 AND c.Id = 1
              AND textGeneration.ProjectionKind = 'Text'
              AND textGeneration.State = 'Active'
              AND textGeneration.RuntimeEpoch IS s.RuntimeEpoch
              AND textGeneration.ExtractorVersion = $extractorVersion
              AND textGeneration.RedactionVersion = $redactionVersion
              AND (s.ActiveEmbeddingGenerationId IS NULL OR (
                   embeddingGeneration.ProjectionKind = 'Embedding'
                    AND embeddingGeneration.State = 'Active'
                    AND embeddingGeneration.RuntimeEpoch IS s.RuntimeEpoch
                   AND embeddingGeneration.ParentTextGenerationId = s.ActiveTextGenerationId
                   AND embeddingGeneration.ExtractorVersion = $extractorVersion
                   AND embeddingGeneration.RedactionVersion = $redactionVersion));
            """;
        command.Parameters.AddWithValue("$extractorVersion", HistorySearchVersions.Extractor);
        command.Parameters.AddWithValue("$redactionVersion", HistorySearchVersions.Redaction);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new HistoryProjectionPin(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3) != 0,
                reader.IsDBNull(4) ? null : reader.GetString(4))
            : null;
    }

    internal bool IsPinCurrent(HistoryProjectionPin pin)
        => TryPinActiveProjection() == pin;

    internal IReadOnlyList<HistoryLexicalCandidate> SearchLexical(
        long generationId,
        HistorySearchRequest request,
        int candidateLimit,
        HistorySessionScope? sessionScope = null)
        => SearchLexical(
            generationId,
            request,
            HistoryFtsQueryPlan.Create(request.Query),
            candidateLimit,
            sessionScope);

    internal IReadOnlyList<HistoryLexicalCandidate> SearchLexical(
        long generationId,
        HistorySearchRequest request,
        HistoryFtsQueryPlan plan,
        int candidateLimit,
        HistorySessionScope? sessionScope = null)
    {
        EnsureAvailable();
        candidateLimit = Math.Clamp(candidateLimit, 1, HistorySearchLimits.CandidateLimit);
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var candidates = new Dictionary<string, CandidateAccumulator>(StringComparer.Ordinal);
        if (plan.IsRecentRequest)
        {
            AddRecentCandidates(
                connection,
                transaction,
                generationId,
                request,
                sessionScope,
                candidateLimit,
                candidates);
        }
        else if (plan.HasTokens)
        {
            if (plan.FacetQuery.Length > 0)
            {
                AddFacetLane(
                    connection,
                    transaction,
                    generationId,
                    request,
                    sessionScope,
                    plan,
                    candidateLimit,
                    HistoryLexicalLaneKind.FacetExact,
                    candidates);
            }
            foreach (var lane in plan.Lanes)
            {
                AddFtsLane(
                    connection,
                    transaction,
                    generationId,
                    request,
                    sessionScope,
                    lane,
                    candidateLimit,
                    candidates);
            }
            if (plan.FacetQuery.Length > 0 && plan.FacetPrefixEligible)
            {
                AddFacetLane(
                    connection,
                    transaction,
                    generationId,
                    request,
                    sessionScope,
                    plan,
                    candidateLimit,
                    HistoryLexicalLaneKind.FacetPrefix,
                    candidates);
            }
        }

        var result = RankCandidates(candidates.Values, candidateLimit);
        transaction.Commit();
        return result;
    }

    internal IReadOnlyList<HistoryStoredDocument> LoadDocuments(
        long generationId,
        IReadOnlyList<string> documentIds)
    {
        EnsureAvailable();
        if (documentIds.Count == 0)
        {
            return [];
        }

        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameters = AddInParameters(command, "$document", documentIds);
        command.CommandText = $"""
            SELECT DocumentId, WorkspaceId, WorkspaceName, SessionId, SessionTitle,
                   RootSessionId, ParentSessionId, ProfileId, Role, Activity, TurnId, ItemId,
                   CallId, AnchorKind, SourceContentRevision, SourceIsStreaming, CreatedAtUtc,
                   DisplaySnippet, ProjectionHash
            FROM HistoryDocuments
            WHERE GenerationId = $generationId AND DocumentId IN ({string.Join(", ", parameters)});
            """;
        command.Parameters.AddWithValue("$generationId", generationId);
        var documents = new List<HistoryStoredDocument>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                documents.Add(new HistoryStoredDocument(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    Guid.Parse(reader.GetString(3)),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : Enum.Parse<AgentMessageRole>(reader.GetString(8), ignoreCase: true),
                    Enum.Parse<HistoryActivityKind>(reader.GetString(9), ignoreCase: true),
                    Guid.Parse(reader.GetString(10)),
                    Guid.Parse(reader.GetString(11)),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    Enum.Parse<HistoryAnchorKind>(reader.GetString(13), ignoreCase: true),
                    reader.GetInt64(14),
                    reader.GetInt64(15) != 0,
                    DateTimeOffset.Parse(reader.GetString(16), CultureInfo.InvariantCulture),
                    reader.GetString(17),
                    reader.GetString(18),
                    []));
            }
        }

        var facets = LoadFacets(connection, transaction, generationId, documentIds);
        transaction.Commit();
        return documents
            .Select(document => document with
            {
                Facets = facets.GetValueOrDefault(document.DocumentId) ?? [],
            })
            .ToArray();
    }

    internal IReadOnlyList<HistorySemanticCandidate> SearchSemanticTopK(
        HistoryProjectionPin pin,
        HistoryProjectionConfiguration configuration,
        HistorySearchRequest request,
        byte[] queryVector,
        int dimensions,
        int requestedResultCount,
        out bool wasTruncated,
        HistorySessionScope? sessionScope = null)
    {
        EnsureAvailable();
        wasTruncated = false;
        if (pin.EmbeddingGenerationId is not { } embeddingGenerationId
            || dimensions is <= 0 or > HistorySearchLimits.MaximumVectorDimensions
            || queryVector.Length != checked(dimensions * sizeof(float))
            || configuration.EmbeddingProviderPackageId is null
            || configuration.EmbeddingProviderId is null
            || configuration.EmbeddingModelId is null
            || configuration.EmbeddingSpaceFingerprint is null)
        {
            return [];
        }

        var vectorBytes = checked(dimensions * sizeof(float));
        var byteBoundedDocuments = HistorySearchLimits.GetMaximumSemanticScanDocuments(dimensions);
        var maximumDocuments = Math.Max(1, byteBoundedDocuments);
        var resultCount = Math.Clamp(requestedResultCount, 1, HistorySearchLimits.MaximumSemanticResults);
        var queue = new PriorityQueue<HistoryScoredEmbedding, HistoryScoredEmbedding>(
            ScoredEmbeddingComparer.Instance);

        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT e.DocumentId, e.Dimensions, e.Vector, length(e.Vector),
                   d.SourceContentRevision, d.ProjectionHash
            FROM HistoryEmbeddings AS e
            INNER JOIN HistoryDocuments AS d
              ON d.GenerationId = e.TextGenerationId
             AND d.DocumentId = e.DocumentId
             AND d.ProjectionHash = e.DocumentProjectionHash
            WHERE e.EmbeddingGenerationId = $embeddingGenerationId
              AND e.TextGenerationId = $textGenerationId
              AND e.ProviderPackageId = $providerPackageId COLLATE NOCASE
              AND e.ProviderId = $providerId COLLATE NOCASE
              AND e.ModelId = $modelId COLLATE BINARY
              AND e.ConfigurationRevision = $configurationRevision
              AND e.EmbeddingSpaceFingerprint = $spaceFingerprint
              AND e.Dimensions = $dimensions
              AND length(e.Vector) = $vectorBytes
              AND d.SourceIsStreaming = 0
              AND {BuildScopePredicate("d")}
            ORDER BY e.DocumentId COLLATE BINARY
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$embeddingGenerationId", embeddingGenerationId);
        command.Parameters.AddWithValue("$textGenerationId", pin.TextGenerationId);
        command.Parameters.AddWithValue("$generationId", pin.TextGenerationId);
        command.Parameters.AddWithValue("$providerPackageId", configuration.EmbeddingProviderPackageId);
        command.Parameters.AddWithValue("$providerId", configuration.EmbeddingProviderId);
        command.Parameters.AddWithValue("$modelId", configuration.EmbeddingModelId);
        command.Parameters.AddWithValue("$configurationRevision", configuration.Revision);
        command.Parameters.AddWithValue("$spaceFingerprint", configuration.EmbeddingSpaceFingerprint);
        command.Parameters.AddWithValue("$dimensions", dimensions);
        command.Parameters.AddWithValue("$vectorBytes", vectorBytes);
        command.Parameters.AddWithValue("$limit", maximumDocuments + 1);
        AddScopeParameters(command, request, sessionScope);

        long consumedBytes = 0;
        var documentsRead = 0;
        var rented = ArrayPool<byte>.Shared.Rent(vectorBytes);
        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (documentsRead >= maximumDocuments)
                {
                    wasTruncated = true;
                    break;
                }
                var storedDimensions = reader.GetInt32(1);
                var storedBytes = reader.GetInt64(3);
                if (storedDimensions != dimensions || storedBytes != vectorBytes)
                {
                    wasTruncated = true;
                    continue;
                }
                if (consumedBytes + storedBytes > HistorySearchLimits.MaximumSemanticScanBytes)
                {
                    wasTruncated = true;
                    break;
                }
                var copied = reader.GetBytes(2, 0, rented, 0, vectorBytes);
                if (copied != vectorBytes)
                {
                    wasTruncated = true;
                    continue;
                }
                consumedBytes += storedBytes;
                documentsRead++;
                var score = HistoryVectorCodec.Dot(
                    queryVector,
                    rented.AsSpan(0, vectorBytes),
                    dimensions);
                if (!double.IsFinite(score))
                {
                    wasTruncated = true;
                    continue;
                }
                var scored = new HistoryScoredEmbedding(
                    reader.GetString(0),
                    score,
                    reader.GetInt64(4),
                    reader.GetString(5));
                queue.Enqueue(scored, scored);
                if (queue.Count > resultCount)
                {
                    queue.Dequeue();
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
        transaction.Commit();

        return queue.UnorderedItems
            .Select(static item => item.Element)
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.DocumentId, StringComparer.Ordinal)
            .Select((item, index) => new HistorySemanticCandidate(
                item.DocumentId,
                index + 1,
                item.SourceContentRevision,
                item.ProjectionHash))
            .ToArray();
    }

    private static Dictionary<string, IReadOnlyList<HistoryProjectionFacet>> LoadFacets(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long generationId,
        IReadOnlyList<string> documentIds)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameters = AddInParameters(command, "$facetDocument", documentIds);
        command.CommandText = $"""
            SELECT DocumentId, Kind, Value, NormalizedValue
            FROM HistoryDocumentFacets
            WHERE GenerationId = $generationId AND DocumentId IN ({string.Join(", ", parameters)})
            ORDER BY DocumentId, Kind, NormalizedValue;
            """;
        command.Parameters.AddWithValue("$generationId", generationId);
        using var reader = command.ExecuteReader();
        var facets = new Dictionary<string, List<HistoryProjectionFacet>>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var documentId = reader.GetString(0);
            if (!facets.TryGetValue(documentId, out var values))
            {
                values = [];
                facets[documentId] = values;
            }
            values.Add(new HistoryProjectionFacet(reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
        return facets.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<HistoryProjectionFacet>)pair.Value,
            StringComparer.Ordinal);
    }

    private static string BuildScopePredicate(string alias)
        => $"""
            ($workspaceId IS NULL OR {alias}.WorkspaceId = $workspaceId)
             AND ($sessionId IS NULL OR {alias}.SessionId IN (
                 SELECT value FROM json_each($sessionScopeJson)
             ))
            AND ($profileId IS NULL OR {alias}.ProfileId = $profileId)
            AND ($role IS NULL OR {alias}.Role = $role)
            AND ($activity IS NULL OR {alias}.Activity = $activity)
            AND ($fromUtc IS NULL OR {alias}.CreatedAtUtc COLLATE BINARY >= $fromUtc COLLATE BINARY)
            AND ($toUtc IS NULL OR {alias}.CreatedAtUtc COLLATE BINARY <= $toUtc COLLATE BINARY)
            """;

    private static void AddScopeParameters(
        SqliteCommand command,
        HistorySearchRequest request,
        HistorySessionScope? sessionScope)
    {
        command.Parameters.AddWithValue("$workspaceId", (object?)request.WorkspaceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sessionId", request.SessionId?.ToString() ?? (object)DBNull.Value);
        var scopedIds = request.SessionId is null
            ? Array.Empty<string>()
            : (sessionScope?.SessionIds ?? [request.SessionId.Value])
                .Select(static id => id.ToString())
                .ToArray();
        command.Parameters.AddWithValue("$sessionScopeJson", JsonSerializer.Serialize(scopedIds));
        command.Parameters.AddWithValue("$profileId", (object?)request.ProfileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$role", request.Role == HistorySearchRoleFilter.Any ? DBNull.Value : request.Role.ToString());
        command.Parameters.AddWithValue("$activity", request.Activity == HistoryActivityKind.None ? DBNull.Value : request.Activity.ToString());
        command.Parameters.AddWithValue("$fromUtc", request.FromUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$toUtc", request.ToUtc?.ToString("O") ?? (object)DBNull.Value);
    }

    private static IReadOnlyList<string> AddInParameters(
        SqliteCommand command,
        string prefix,
        IReadOnlyList<string> values)
    {
        var names = new string[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            names[index] = prefix + index.ToString(CultureInfo.InvariantCulture);
            command.Parameters.AddWithValue(names[index], values[index]);
        }
        return names;
    }

    private sealed class ScoredEmbeddingComparer : IComparer<HistoryScoredEmbedding>
    {
        internal static ScoredEmbeddingComparer Instance { get; } = new();

        public int Compare(HistoryScoredEmbedding? left, HistoryScoredEmbedding? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var score = left.Score.CompareTo(right.Score);
            return score != 0
                ? score
                : -StringComparer.Ordinal.Compare(left.DocumentId, right.DocumentId);
        }
    }
}
