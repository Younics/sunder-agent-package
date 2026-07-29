using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.HistorySearch;

internal sealed partial class HistorySearchStore
{
    private readonly object _writeLock = new();
    private readonly object _recoveryLock = new();

    internal HistorySearchStore(IPackageContext packageContext)
    {
        DatabasePath = packageContext.Storage.RoleLocalWorkspace.GetLocalPath("agent/history-search.db");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            InitializeWithRecovery();
            IsAvailable = true;
        }
        catch
        {
            IsAvailable = false;
            FailureCode = "projection-store-unavailable";
        }
    }

    internal string DatabasePath { get; }
    internal bool IsAvailable { get; private set; }
    internal bool WasRecovered { get; private set; }
    internal string? FailureCode { get; private set; }

    internal HistoryProjectionConfiguration GetConfiguration()
    {
        if (!IsAvailable)
        {
            return DisabledConfiguration;
        }
        using var connection = CreateConnection();
        connection.Open();
        return GetConfiguration(connection, transaction: null);
    }

    internal HistoryProjectionConfiguration ChangeConfiguration(
        bool semanticEnabled,
        string? providerPackageId,
        string? providerId,
        string? modelId,
        string? spaceFingerprint)
    {
        EnsureAvailable();
        lock (_writeLock)
        {
            using var connection = CreateConnection();
            connection.Open();
            using var transaction = BeginMutation(connection, "change-configuration");
            var current = GetConfiguration(connection, transaction);
            var next = new HistoryProjectionConfiguration(
                semanticEnabled,
                semanticEnabled ? providerPackageId : null,
                semanticEnabled ? providerId : null,
                semanticEnabled ? modelId : null,
                checked(current.Revision + 1),
                semanticEnabled ? spaceFingerprint : null);
            SaveConfiguration(connection, transaction, next);
            RemoveAllEmbeddingData(connection, transaction);
            using (var state = connection.CreateCommand())
            {
                state.Transaction = transaction;
                state.CommandText = """
                    UPDATE HistoryProjectionState
                    SET ActiveEmbeddingGenerationId = NULL,
                        SemanticReady = 0
                    WHERE Id = 1;
                    """;
                state.ExecuteNonQuery();
            }
            CommitMutation(connection, transaction);
            return next;
        }
    }

    internal HistoryProjectionConfiguration RefreshConfigurationFingerprint(
        long expectedRevision,
        string fingerprint)
    {
        EnsureAvailable();
        lock (_writeLock)
        {
            using var connection = CreateConnection();
            connection.Open();
            using var transaction = BeginMutation(connection, "refresh-configuration-fingerprint");
            var current = GetConfiguration(connection, transaction);
            if (current.Revision != expectedRevision || !current.SemanticEnabled)
            {
                throw new OperationCanceledException("The semantic configuration was superseded.");
            }
            var next = current with
            {
                Revision = checked(current.Revision + 1),
                EmbeddingSpaceFingerprint = fingerprint,
            };
            SaveConfiguration(connection, transaction, next);
            RemoveAllEmbeddingData(connection, transaction);
            Execute(connection, transaction, "UPDATE HistoryProjectionState SET ActiveEmbeddingGenerationId = NULL, SemanticReady = 0 WHERE Id = 1;");
            CommitMutation(connection, transaction);
            return next;
        }
    }

    internal HistoryProjectionSnapshot GetSnapshot()
    {
        if (!IsAvailable || _requiresSecureRecreation)
        {
            return EmptySnapshot;
        }
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.ActiveTextGenerationId, s.ActiveEmbeddingGenerationId,
                   (SELECT COUNT(*) FROM HistoryDocuments d WHERE d.GenerationId = s.ActiveTextGenerationId),
                   (SELECT COUNT(*) FROM HistoryEmbeddings e WHERE e.EmbeddingGenerationId = s.ActiveEmbeddingGenerationId),
                   s.LastReconciledAtUtc, s.SemanticReady, s.IsManuallyCleared, c.Revision,
                   s.RuntimeEpoch
            FROM HistoryProjectionState AS s
            CROSS JOIN HistoryConfiguration AS c
            WHERE s.Id = 1 AND c.Id = 1;
            """;
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new HistoryProjectionSnapshot(
                reader.IsDBNull(0) ? null : reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                reader.GetInt64(5) != 0,
                reader.GetInt64(6) != 0,
                reader.GetInt64(7),
                reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8)))
            : EmptySnapshot;
    }

    internal HistoryProjectionGeneration? GetActiveTextGeneration()
        => GetGeneration(GetSnapshot().ActiveTextGenerationId);

    internal HistoryProjectionGeneration? GetActiveEmbeddingGeneration()
        => GetGeneration(GetSnapshot().ActiveEmbeddingGenerationId);

    internal long BeginTextGeneration()
    {
        EnsureAvailable();
        var configuration = GetConfiguration() with
        {
            EmbeddingProviderPackageId = null,
            EmbeddingProviderId = null,
            EmbeddingModelId = null,
            EmbeddingSpaceFingerprint = null,
        };
        return BeginGeneration("Text", parentTextGenerationId: null, configuration);
    }

    internal long BeginEmbeddingGeneration(
        long textGenerationId,
        HistoryProjectionConfiguration configuration)
    {
        EnsureAvailable();
        if (!configuration.SemanticEnabled
            || configuration.EmbeddingProviderPackageId is null
            || configuration.EmbeddingProviderId is null
            || configuration.EmbeddingModelId is null
            || configuration.EmbeddingSpaceFingerprint is null)
        {
            throw new InvalidOperationException("Semantic configuration is incomplete.");
        }
        return BeginGeneration("Embedding", textGenerationId, configuration);
    }

    internal void FailGeneration(long generationId, string failureCode)
    {
        _ = failureCode;
        if (!IsAvailable)
        {
            return;
        }
        lock (_writeLock)
        {
            using var connection = CreateConnection();
            connection.Open();
            using var transaction = BeginMutation(connection, "fail-generation");
            var generation = GetGeneration(connection, transaction, generationId);
            if (generation is null || generation.State != "Staging")
            {
                CommitMutation(connection, transaction);
                return;
            }
            EnsureGenerationRuntimeEpoch(generation);
            if (generation.ProjectionKind == "Text")
            {
                DeleteTextGenerationData(connection, transaction, generationId);
            }
            else
            {
                using var embeddings = connection.CreateCommand();
                embeddings.Transaction = transaction;
                embeddings.CommandText = "DELETE FROM HistoryEmbeddings WHERE EmbeddingGenerationId = $generationId;";
                embeddings.Parameters.AddWithValue("$generationId", generationId);
                embeddings.ExecuteNonQuery();
            }
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM HistoryProjectionGenerations
                WHERE GenerationId = $generationId AND State = 'Staging';
                """;
            command.Parameters.AddWithValue("$generationId", generationId);
            command.ExecuteNonQuery();
            CommitMutation(connection, transaction);
        }
    }

    private IReadOnlyList<HistoryEmbeddingWorkItem> ListEmbeddingWorkPage(
        long textGenerationId,
        long? embeddingGenerationId,
        string? afterDocumentId,
        int limit)
    {
        EnsureAvailable();
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = embeddingGenerationId is null
            ? """
                SELECT d.DocumentId, d.BodyText, d.ProjectionHash
                FROM HistoryDocuments AS d
                WHERE d.GenerationId = $textGenerationId
                  AND d.SourceIsStreaming = 0
                  AND ($afterDocumentId IS NULL OR d.DocumentId COLLATE BINARY > $afterDocumentId COLLATE BINARY)
                ORDER BY d.DocumentId COLLATE BINARY
                LIMIT $limit;
                """
            : """
                SELECT d.DocumentId, d.BodyText, d.ProjectionHash
                FROM HistoryDocuments AS d
                LEFT JOIN HistoryEmbeddings AS e
                  ON e.EmbeddingGenerationId = $embeddingGenerationId
                 AND e.DocumentId = d.DocumentId
                 AND e.TextGenerationId = d.GenerationId
                 AND e.DocumentProjectionHash = d.ProjectionHash
                WHERE d.GenerationId = $textGenerationId
                  AND d.SourceIsStreaming = 0
                  AND e.DocumentId IS NULL
                  AND ($afterDocumentId IS NULL OR d.DocumentId COLLATE BINARY > $afterDocumentId COLLATE BINARY)
                ORDER BY d.DocumentId COLLATE BINARY
                LIMIT $limit;
                """;
        command.Parameters.AddWithValue("$textGenerationId", textGenerationId);
        command.Parameters.AddWithValue("$embeddingGenerationId", (object?)embeddingGenerationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$afterDocumentId", (object?)afterDocumentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        using var reader = command.ExecuteReader();
        var items = new List<HistoryEmbeddingWorkItem>();
        while (reader.Read())
        {
            items.Add(new HistoryEmbeddingWorkItem(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        return items;
    }

    private static HistoryProjectionConfiguration GetConfiguration(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT SemanticEnabled, EmbeddingProviderPackageId, EmbeddingProviderId,
                   EmbeddingModelId, Revision, EmbeddingSpaceFingerprint
            FROM HistoryConfiguration WHERE Id = 1;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidDataException("History configuration singleton is missing.");
        }
        return new HistoryProjectionConfiguration(
            reader.GetInt64(0) != 0,
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private static void SaveConfiguration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HistoryProjectionConfiguration configuration)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE HistoryConfiguration
            SET SemanticEnabled = $semanticEnabled,
                EmbeddingProviderPackageId = $providerPackageId,
                EmbeddingProviderId = $providerId,
                EmbeddingModelId = $modelId,
                Revision = $revision,
                EmbeddingSpaceFingerprint = $spaceFingerprint,
                UpdatedAtUtc = $updatedAtUtc
            WHERE Id = 1;
            """;
        command.Parameters.AddWithValue("$semanticEnabled", configuration.SemanticEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$providerPackageId", (object?)configuration.EmbeddingProviderPackageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$providerId", (object?)configuration.EmbeddingProviderId ?? DBNull.Value);
        command.Parameters.AddWithValue("$modelId", (object?)configuration.EmbeddingModelId ?? DBNull.Value);
        command.Parameters.AddWithValue("$revision", configuration.Revision);
        command.Parameters.AddWithValue("$spaceFingerprint", (object?)configuration.EmbeddingSpaceFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidDataException("History configuration singleton is missing.");
        }
    }

    private static void InsertDocument(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long generationId,
        HistoryProjectionDocument document)
    {
        document = HistoryProjectionDocumentNormalizer.Normalize(document);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO HistoryDocuments (
                    GenerationId, DocumentId, WorkspaceId, WorkspaceName, SessionId, SessionTitle,
                    RootSessionId, ParentSessionId, ProfileId, Role, Activity, TurnId, ItemId,
                    CallId, AnchorKind, SourceContentRevision, SourceIsStreaming, CreatedAtUtc,
                    BodyText, DisplaySnippet, ProjectionHash)
                VALUES (
                    $generationId, $documentId, $workspaceId, $workspaceName, $sessionId, $sessionTitle,
                    $rootSessionId, $parentSessionId, $profileId, $role, $activity, $turnId, $itemId,
                    $callId, $anchorKind, $contentRevision, $isStreaming, $createdAtUtc,
                    $bodyText, $displaySnippet, $projectionHash);
                """;
            AddDocumentParameters(command, generationId, document);
            command.ExecuteNonQuery();
        }
        foreach (var facet in document.Facets)
        {
            using var facetCommand = connection.CreateCommand();
            facetCommand.Transaction = transaction;
            facetCommand.CommandText = """
                INSERT OR IGNORE INTO HistoryDocumentFacets
                    (GenerationId, DocumentId, Kind, Value, NormalizedValue)
                VALUES ($generationId, $documentId, $kind, $value, $normalizedValue);
                """;
            facetCommand.Parameters.AddWithValue("$generationId", generationId);
            facetCommand.Parameters.AddWithValue("$documentId", document.DocumentId);
            facetCommand.Parameters.AddWithValue("$kind", facet.Kind);
            facetCommand.Parameters.AddWithValue("$value", facet.Value);
            facetCommand.Parameters.AddWithValue("$normalizedValue", facet.NormalizedValue);
            facetCommand.ExecuteNonQuery();
        }

        var pathText = JoinFacets(document.Facets, "path", "basename", "working-directory", "url");
        var symbolText = JoinFacets(document.Facets, "symbol");
        var activityText = JoinFacets(document.Facets, "activity", "operation", "status");
        var toolText = JoinFacets(document.Facets, "tool");
        using var fts = connection.CreateCommand();
        fts.Transaction = transaction;
        fts.CommandText = """
            INSERT INTO HistoryDocumentsFts
                (DocumentId, GenerationId, BodyText, PathText, SymbolText, ActivityText, ToolText)
            VALUES ($documentId, $generationId, $bodyText, $pathText, $symbolText, $activityText, $toolText);
            """;
        fts.Parameters.AddWithValue("$documentId", document.DocumentId);
        fts.Parameters.AddWithValue("$generationId", generationId.ToString(CultureInfo.InvariantCulture));
        fts.Parameters.AddWithValue("$bodyText", HistorySearchText.NormalizeForSearch(document.BodyText));
        fts.Parameters.AddWithValue("$pathText", pathText);
        fts.Parameters.AddWithValue("$symbolText", symbolText);
        fts.Parameters.AddWithValue("$activityText", activityText);
        fts.Parameters.AddWithValue("$toolText", toolText);
        fts.ExecuteNonQuery();
    }

    private static bool DeleteDocuments(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long generationId,
        string predicate,
        Action<SqliteCommand> addParameters)
    {
        var documentIds = new List<string>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = $"SELECT DocumentId FROM HistoryDocuments WHERE GenerationId = $generationId AND {predicate};";
            select.Parameters.AddWithValue("$generationId", generationId);
            addParameters(select);
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                documentIds.Add(reader.GetString(0));
            }
        }
        foreach (var documentId in documentIds)
        {
            using (var fts = connection.CreateCommand())
            {
                fts.Transaction = transaction;
                fts.CommandText = "DELETE FROM HistoryDocumentsFts WHERE DocumentId = $documentId AND GenerationId = $generationId;";
                fts.Parameters.AddWithValue("$documentId", documentId);
                fts.Parameters.AddWithValue("$generationId", generationId.ToString(CultureInfo.InvariantCulture));
                fts.ExecuteNonQuery();
            }
            using (var embeddings = connection.CreateCommand())
            {
                embeddings.Transaction = transaction;
                embeddings.CommandText = "DELETE FROM HistoryEmbeddings WHERE TextGenerationId = $generationId AND DocumentId = $documentId;";
                embeddings.Parameters.AddWithValue("$generationId", generationId);
                embeddings.Parameters.AddWithValue("$documentId", documentId);
                embeddings.ExecuteNonQuery();
            }
        }
        using (var facets = connection.CreateCommand())
        {
            facets.Transaction = transaction;
            facets.CommandText = $"DELETE FROM HistoryDocumentFacets WHERE GenerationId = $generationId AND DocumentId IN (SELECT DocumentId FROM HistoryDocuments WHERE GenerationId = $generationId AND {predicate});";
            facets.Parameters.AddWithValue("$generationId", generationId);
            addParameters(facets);
            facets.ExecuteNonQuery();
        }
        using (var documents = connection.CreateCommand())
        {
            documents.Transaction = transaction;
            documents.CommandText = $"DELETE FROM HistoryDocuments WHERE GenerationId = $generationId AND {predicate};";
            documents.Parameters.AddWithValue("$generationId", generationId);
            addParameters(documents);
            documents.ExecuteNonQuery();
        }
        return documentIds.Count > 0;
    }

    private static void AddDocumentParameters(
        SqliteCommand command,
        long generationId,
        HistoryProjectionDocument document)
    {
        command.Parameters.AddWithValue("$generationId", generationId);
        command.Parameters.AddWithValue("$documentId", document.DocumentId);
        command.Parameters.AddWithValue("$workspaceId", document.WorkspaceId);
        command.Parameters.AddWithValue("$workspaceName", document.WorkspaceName);
        command.Parameters.AddWithValue("$sessionId", document.SessionId.ToString());
        command.Parameters.AddWithValue("$sessionTitle", document.SessionTitle);
        command.Parameters.AddWithValue("$rootSessionId", document.RootSessionId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$parentSessionId", document.ParentSessionId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$profileId", (object?)document.ProfileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$role", document.Role?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$activity", document.Activity.ToString());
        command.Parameters.AddWithValue("$turnId", document.TurnId.ToString());
        command.Parameters.AddWithValue("$itemId", document.ItemId.ToString());
        command.Parameters.AddWithValue("$callId", (object?)document.CallId ?? DBNull.Value);
        command.Parameters.AddWithValue("$anchorKind", document.AnchorKind.ToString());
        command.Parameters.AddWithValue("$contentRevision", document.SourceContentRevision);
        command.Parameters.AddWithValue("$isStreaming", document.SourceIsStreaming ? 1 : 0);
        command.Parameters.AddWithValue("$createdAtUtc", document.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$bodyText", document.BodyText);
        command.Parameters.AddWithValue("$displaySnippet", document.DisplaySnippet);
        command.Parameters.AddWithValue("$projectionHash", ComputeProjectionHash(document));
    }

    private static bool TurnDocumentsMatch(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long generationId,
        Guid sessionId,
        Guid turnId,
        IReadOnlyList<HistoryProjectionDocument> documents)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DocumentId, ProjectionHash FROM HistoryDocuments
            WHERE GenerationId = $generationId AND SessionId = $sessionId AND TurnId = $turnId;
            """;
        command.Parameters.AddWithValue("$generationId", generationId);
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$turnId", turnId.ToString());
        var expected = documents.ToDictionary(
            static document => document.DocumentId,
            ComputeProjectionHash,
            StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            count++;
            if (!expected.TryGetValue(reader.GetString(0), out var hash)
                || !string.Equals(hash, reader.GetString(1), StringComparison.Ordinal))
            {
                return false;
            }
        }
        return count == expected.Count;
    }

    private static string ComputeProjectionHash(HistoryProjectionDocument document)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                HistoryProjectionDocumentNormalizer.Normalize(document))))
            .ToLowerInvariant();

    private static string JoinFacets(IReadOnlyList<HistoryProjectionFacet> facets, params string[] kinds)
        => string.Join(' ', facets
            .Where(facet => kinds.Contains(facet.Kind, StringComparer.OrdinalIgnoreCase))
            .Select(static facet => facet.NormalizedValue));

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void MarkSemanticIncomplete(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long textGenerationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE HistoryProjectionState
            SET SemanticReady = 0
            WHERE Id = 1 AND ActiveTextGenerationId = $textGenerationId;
            """;
        command.Parameters.AddWithValue("$textGenerationId", textGenerationId);
        command.ExecuteNonQuery();
    }

    private static bool SetSemanticReadyFromInvariants(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT CASE WHEN c.SemanticEnabled = 1
                    AND s.ActiveTextGenerationId IS NOT NULL
                    AND s.ActiveEmbeddingGenerationId IS NOT NULL
                    AND g.GenerationId = s.ActiveEmbeddingGenerationId
                    AND g.State = 'Active'
                    AND g.ParentTextGenerationId = s.ActiveTextGenerationId
                    AND g.ProviderPackageId = c.EmbeddingProviderPackageId COLLATE NOCASE
                    AND g.ProviderId = c.EmbeddingProviderId COLLATE NOCASE
                    AND g.ModelId = c.EmbeddingModelId COLLATE BINARY
                    AND g.ConfigurationRevision = c.Revision
                    AND g.EmbeddingSpaceFingerprint = c.EmbeddingSpaceFingerprint
                    AND g.Dimensions > 0
                    AND NOT EXISTS (
                        SELECT 1
                        FROM HistoryDocuments d
                        LEFT JOIN HistoryEmbeddings e
                          ON e.EmbeddingGenerationId = s.ActiveEmbeddingGenerationId
                         AND e.DocumentId = d.DocumentId
                         AND e.TextGenerationId = d.GenerationId
                         AND e.ProviderPackageId = c.EmbeddingProviderPackageId COLLATE NOCASE
                         AND e.ProviderId = c.EmbeddingProviderId COLLATE NOCASE
                          AND e.ModelId = c.EmbeddingModelId COLLATE BINARY
                         AND e.ConfigurationRevision = c.Revision
                         AND e.EmbeddingSpaceFingerprint = c.EmbeddingSpaceFingerprint
                         AND e.DocumentProjectionHash = d.ProjectionHash
                         AND e.Dimensions = g.Dimensions
                         AND length(e.Vector) = g.Dimensions * 4
                        WHERE d.GenerationId = s.ActiveTextGenerationId
                          AND d.SourceIsStreaming = 0
                          AND e.DocumentId IS NULL)
                THEN 1 ELSE 0 END
            FROM HistoryProjectionState s
            CROSS JOIN HistoryConfiguration c
            LEFT JOIN HistoryProjectionGenerations g ON g.GenerationId = s.ActiveEmbeddingGenerationId
            WHERE s.Id = 1 AND c.Id = 1;
            """;
        var ready = Convert.ToInt64(query.ExecuteScalar()) != 0;
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE HistoryProjectionState SET SemanticReady = $ready WHERE Id = 1;";
        update.Parameters.AddWithValue("$ready", ready ? 1 : 0);
        update.ExecuteNonQuery();
        return ready;
    }

    private static void EnsureEmbeddingCoverageComplete(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long embeddingGenerationId,
        long textGenerationId,
        int dimensions,
        HistoryProjectionConfiguration configuration)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM HistoryDocuments d
            LEFT JOIN HistoryEmbeddings e
              ON e.EmbeddingGenerationId = $embeddingGenerationId
             AND e.DocumentId = d.DocumentId
             AND e.TextGenerationId = d.GenerationId
             AND e.ProviderPackageId = $providerPackageId COLLATE NOCASE
             AND e.ProviderId = $providerId COLLATE NOCASE
              AND e.ModelId = $modelId COLLATE BINARY
             AND e.ConfigurationRevision = $configurationRevision
             AND e.EmbeddingSpaceFingerprint = $spaceFingerprint
             AND e.DocumentProjectionHash = d.ProjectionHash
             AND e.Dimensions = $dimensions
             AND length(e.Vector) = $vectorBytes
            WHERE d.GenerationId = $textGenerationId
              AND d.SourceIsStreaming = 0
              AND e.DocumentId IS NULL;
            """;
        command.Parameters.AddWithValue("$embeddingGenerationId", embeddingGenerationId);
        command.Parameters.AddWithValue("$textGenerationId", textGenerationId);
        command.Parameters.AddWithValue("$providerPackageId", configuration.EmbeddingProviderPackageId!);
        command.Parameters.AddWithValue("$providerId", configuration.EmbeddingProviderId!);
        command.Parameters.AddWithValue("$modelId", configuration.EmbeddingModelId!);
        command.Parameters.AddWithValue("$configurationRevision", configuration.Revision);
        command.Parameters.AddWithValue("$spaceFingerprint", configuration.EmbeddingSpaceFingerprint!);
        command.Parameters.AddWithValue("$dimensions", dimensions);
        command.Parameters.AddWithValue("$vectorBytes", dimensions * sizeof(float));
        if (Convert.ToInt64(command.ExecuteScalar()) != 0)
        {
            throw new InvalidOperationException("The embedding generation does not cover every finalized document.");
        }
    }

    private static bool DocumentHashMatches(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long textGenerationId,
        string documentId,
        string projectionHash)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM HistoryDocuments
            WHERE GenerationId = $generationId AND DocumentId = $documentId
              AND ProjectionHash = $projectionHash AND SourceIsStreaming = 0;
            """;
        command.Parameters.AddWithValue("$generationId", textGenerationId);
        command.Parameters.AddWithValue("$documentId", documentId);
        command.Parameters.AddWithValue("$projectionHash", projectionHash);
        return command.ExecuteScalar() is not null;
    }

    private static void EnsureConfigurationMatches(
        HistoryProjectionConfiguration actual,
        HistoryProjectionConfiguration expected)
    {
        if (actual.Revision != expected.Revision
            || actual.SemanticEnabled != expected.SemanticEnabled
            || !string.Equals(actual.EmbeddingProviderPackageId, expected.EmbeddingProviderPackageId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(actual.EmbeddingProviderId, expected.EmbeddingProviderId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(actual.EmbeddingModelId, expected.EmbeddingModelId, StringComparison.Ordinal)
            || !string.Equals(actual.EmbeddingSpaceFingerprint, expected.EmbeddingSpaceFingerprint, StringComparison.Ordinal))
        {
            throw new OperationCanceledException("The semantic configuration was superseded.");
        }
    }

    private static void EnsureGenerationMatchesConfiguration(
        HistoryProjectionGeneration generation,
        HistoryProjectionConfiguration configuration)
    {
        if (generation.ConfigurationRevision != configuration.Revision
            || !string.Equals(generation.ProviderPackageId, configuration.EmbeddingProviderPackageId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(generation.ProviderId, configuration.EmbeddingProviderId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(generation.ModelId, configuration.EmbeddingModelId, StringComparison.Ordinal)
            || !string.Equals(generation.EmbeddingSpaceFingerprint, configuration.EmbeddingSpaceFingerprint, StringComparison.Ordinal))
        {
            throw new OperationCanceledException("The embedding generation belongs to a superseded configuration.");
        }
    }

    private static void RemoveAllEmbeddingData(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        Execute(connection, transaction, "DELETE FROM HistoryEmbeddings;");
        Execute(connection, transaction, "DELETE FROM HistoryProjectionGenerations WHERE ProjectionKind = 'Embedding';");
    }

    private static void DeleteTextGenerationData(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long generationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM HistoryEmbeddings WHERE TextGenerationId = $generationId;
            DELETE FROM HistoryDocumentsFts WHERE CAST(GenerationId AS INTEGER) = $generationId;
            DELETE FROM HistoryDocumentFacets WHERE GenerationId = $generationId;
            DELETE FROM HistoryDocuments WHERE GenerationId = $generationId;
            """;
        command.Parameters.AddWithValue("$generationId", generationId);
        command.ExecuteNonQuery();
    }

    private static void DeleteInactiveTextProjectionData(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long activeGenerationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM HistoryEmbeddings;
            DELETE FROM HistoryDocumentsFts WHERE CAST(GenerationId AS INTEGER) <> $generationId;
            DELETE FROM HistoryDocumentFacets WHERE GenerationId <> $generationId;
            DELETE FROM HistoryDocuments WHERE GenerationId <> $generationId;
            DELETE FROM HistoryProjectionGenerations
            WHERE ProjectionKind = 'Embedding'
               OR (ProjectionKind = 'Text' AND GenerationId <> $generationId);
            """;
        command.Parameters.AddWithValue("$generationId", activeGenerationId);
        command.ExecuteNonQuery();
    }

    private static void DeleteInactiveEmbeddingProjectionData(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long activeGenerationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM HistoryEmbeddings WHERE EmbeddingGenerationId <> $generationId;
            DELETE FROM HistoryProjectionGenerations
            WHERE ProjectionKind = 'Embedding' AND GenerationId <> $generationId;
            """;
        command.Parameters.AddWithValue("$generationId", activeGenerationId);
        command.ExecuteNonQuery();
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("History search projection storage is unavailable.");
        }
    }

    private static string Bound(string value, int maximumLength)
        => value.Length <= maximumLength ? value : value[..maximumLength];

    private static HistoryProjectionConfiguration DisabledConfiguration { get; } =
        new(false, null, null, null, 0, null);

    private static HistoryProjectionSnapshot EmptySnapshot { get; } =
        new(null, null, 0, 0, null, false, false, 0, null);
}
