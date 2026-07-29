using Microsoft.Data.Sqlite;

namespace Sunder.Package.Agent.HistorySearch;

internal sealed partial class HistorySearchStore
{
    internal (HistoryProjectionConfiguration Configuration, bool StaleTextGenerationInvalidated)
        EnforceLocalOnlyAutomaticMaintenance()
    {
        EnsureAvailable();
        lock (_writeLock)
        {
            EnsureCommittedRuntimeEpochCurrent();
            if (_requiresSecureRecreation)
            {
                using var legacyConnection = CreateConnection();
                legacyConnection.Open();
                var legacy = GetConfiguration(legacyConnection, transaction: null);
                var replacement = CreateLocalOnlyConfiguration(legacy);
                legacyConnection.Close();
                RecreateDerivedDatabase(replacement.Revision, manuallyCleared: false);
                return (replacement, true);
            }

            var activeBeforeMaintenance = GetActiveTextGeneration();
            if (activeBeforeMaintenance is not null)
            {
                EnsureGenerationRuntimeEpoch(activeBeforeMaintenance);
            }
            if (activeBeforeMaintenance is not null
                && activeBeforeMaintenance.RedactionVersion != HistorySearchVersions.Redaction)
            {
                var configurationBeforeRecreation = GetConfiguration();
                var replacement = CreateLocalOnlyConfiguration(configurationBeforeRecreation);
                RecreateDerivedDatabase(replacement.Revision, manuallyCleared: false);
                return (replacement, true);
            }

            using var connection = CreateConnection();
            connection.Open();
            using var transaction = BeginMutation(connection, "enforce-local-only-maintenance");
            ReclaimAbandonedGenerations(connection, transaction);
            var current = GetConfiguration(connection, transaction);
            var activeTextGenerationId = GetActiveTextGenerationId(connection, transaction);
            var activeText = activeTextGenerationId is { } generationId
                ? GetGeneration(connection, transaction, generationId)
                : null;
            var staleText = activeText is not null && !UsesCurrentProvenance(activeText);
            var requiresConfigurationUpdate = current.SemanticEnabled
                                              || current.EmbeddingProviderPackageId is not null
                                              || current.EmbeddingProviderId is not null
                                              || current.EmbeddingModelId is not null
                                              || current.EmbeddingSpaceFingerprint is not null;
            var next = requiresConfigurationUpdate ? CreateLocalOnlyConfiguration(current) : current;
            if (requiresConfigurationUpdate)
            {
                SaveConfiguration(connection, transaction, next);
            }
            RemoveAllEmbeddingData(connection, transaction);
            if (staleText)
            {
                DeleteTextGenerationData(connection, transaction, activeText!.GenerationId);
                using var generation = connection.CreateCommand();
                generation.Transaction = transaction;
                generation.CommandText = "DELETE FROM HistoryProjectionGenerations WHERE GenerationId = $generationId;";
                generation.Parameters.AddWithValue("$generationId", activeText.GenerationId);
                generation.ExecuteNonQuery();
                Execute(connection, transaction, """
                    UPDATE HistoryProjectionState
                    SET ActiveTextGenerationId = NULL,
                        ActiveEmbeddingGenerationId = NULL,
                        SemanticReady = 0,
                        IsManuallyCleared = 0,
                        LastReconciledAtUtc = NULL
                    WHERE Id = 1;
                    """);
            }
            else
            {
                Execute(connection, transaction, """
                    UPDATE HistoryProjectionState
                    SET ActiveEmbeddingGenerationId = NULL,
                        SemanticReady = 0,
                        IsManuallyCleared = 0
                    WHERE Id = 1;
                    """);
            }
            CommitMutation(connection, transaction);
            CheckpointTruncate(connection);
            return (next, staleText);
        }
    }

    private static HistoryProjectionConfiguration CreateLocalOnlyConfiguration(
        HistoryProjectionConfiguration current)
        => new(
            SemanticEnabled: false,
            EmbeddingProviderPackageId: null,
            EmbeddingProviderId: null,
            EmbeddingModelId: null,
            Revision: current.SemanticEnabled
                      || current.EmbeddingProviderPackageId is not null
                      || current.EmbeddingProviderId is not null
                      || current.EmbeddingModelId is not null
                      || current.EmbeddingSpaceFingerprint is not null
                ? checked(current.Revision + 1)
                : current.Revision,
            EmbeddingSpaceFingerprint: null);

    internal bool ReplaceTurnDocuments(
        long generationId,
        Guid sessionId,
        Guid turnId,
        IReadOnlyList<HistoryProjectionDocument> documents)
    {
        EnsureAvailable();
        lock (_writeLock)
        {
            using var connection = CreateConnection();
            connection.Open();
            using var transaction = BeginMutation(connection, "replace-turn-documents");
            EnsureTextGenerationWritable(connection, transaction, generationId);
            if (TurnDocumentsMatch(connection, transaction, generationId, sessionId, turnId, documents))
            {
                CommitMutation(connection, transaction);
                return false;
            }
            MarkSemanticIncomplete(connection, transaction, generationId);
            DeleteDocuments(
                connection,
                transaction,
                generationId,
                "SessionId = $sessionId AND TurnId = $turnId",
                command =>
                {
                    command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
                    command.Parameters.AddWithValue("$turnId", turnId.ToString());
                });
            foreach (var document in documents)
            {
                InsertDocument(connection, transaction, generationId, document);
            }
            CommitMutation(connection, transaction);
            return true;
        }
    }

    internal void ReplaceSessionDocuments(
        long generationId,
        HistorySourceSession session,
        IReadOnlyList<HistoryProjectionDocument> documents)
    {
        EnsureAvailable();
        lock (_writeLock)
        {
            using var connection = CreateConnection();
            connection.Open();
            using var transaction = BeginMutation(connection, "replace-session-documents");
            EnsureTextGenerationWritable(connection, transaction, generationId);
            MarkSemanticIncomplete(connection, transaction, generationId);
            DeleteDocuments(
                connection,
                transaction,
                generationId,
                "SessionId = $sessionId",
                command => command.Parameters.AddWithValue("$sessionId", session.SessionId.ToString()));
            foreach (var document in documents)
            {
                InsertDocument(connection, transaction, generationId, document);
            }
            CommitMutation(connection, transaction);
        }
    }

    internal void ClearSessionDocuments(long generationId, Guid sessionId)
    {
        EnsureAvailable();
        lock (_writeLock)
        {
            using var connection = CreateConnection();
            connection.Open();
            using var transaction = BeginMutation(connection, "clear-session-documents");
            EnsureTextGenerationWritable(connection, transaction, generationId);
            MarkSemanticIncomplete(connection, transaction, generationId);
            DeleteDocuments(
                connection,
                transaction,
                generationId,
                "SessionId = $sessionId",
                command => command.Parameters.AddWithValue("$sessionId", sessionId.ToString()));
            CommitMutation(connection, transaction);
        }
    }

    internal bool DeleteSessionDocuments(Guid sessionId)
    {
        if (!IsAvailable)
        {
            return false;
        }
        lock (_writeLock)
        {
            using var connection = CreateConnection();
            connection.Open();
            if (!_ftsSecureDeleteEnabled)
            {
                var hadDocuments = GetSnapshot().DocumentCount > 0;
                var current = GetConfiguration();
                connection.Close();
                RecreateDerivedDatabase(checked(current.Revision + 1), manuallyCleared: false);
                return hadDocuments;
            }
            using var transaction = BeginMutation(connection, "delete-session-documents");
            var generations = ListTextGenerationIds(connection, transaction);
            var changed = false;
            foreach (var generationId in generations)
            {
                MarkSemanticIncomplete(connection, transaction, generationId);
                changed |= DeleteDocuments(
                    connection,
                    transaction,
                    generationId,
                    "SessionId = $sessionId",
                    command => command.Parameters.AddWithValue("$sessionId", sessionId.ToString()));
            }
            SetSemanticReadyFromInvariants(connection, transaction);
            CommitMutation(connection, transaction);
            CheckpointTruncate(connection);
            return changed;
        }
    }

    internal void ActivateTextGeneration(long generationId)
    {
        EnsureAvailable();
        lock (_writeLock)
        {
            using var connection = CreateConnection();
            connection.Open();
            using var transaction = BeginMutation(connection, "activate-text-generation");
            EnsureStagingGeneration(connection, transaction, generationId, "Text");
            Execute(connection, transaction, "UPDATE HistoryProjectionGenerations SET State = 'Superseded' WHERE ProjectionKind = 'Text' AND State = 'Active';");
            CompleteGeneration(connection, transaction, generationId, dimensions: null);
            using (var state = connection.CreateCommand())
            {
                state.Transaction = transaction;
                state.CommandText = """
                    UPDATE HistoryProjectionState
                    SET ActiveTextGenerationId = $generationId,
                        ActiveEmbeddingGenerationId = NULL,
                        SemanticReady = 0,
                        IsManuallyCleared = 0,
                        LastReconciledAtUtc = $now
                    WHERE Id = 1;
                    """;
                state.Parameters.AddWithValue("$generationId", generationId);
                state.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                state.ExecuteNonQuery();
            }
            DeleteInactiveTextProjectionData(connection, transaction, generationId);
            CommitMutation(connection, transaction);
        }
    }

    internal void ActivateEmbeddingGeneration(
        long generationId,
        int dimensions,
        HistoryProjectionConfiguration expectedConfiguration)
    {
        EnsureAvailable();
        if (dimensions is <= 0 or > HistorySearchLimits.MaximumVectorDimensions)
        {
            throw new InvalidOperationException("Embedding dimensions are invalid.");
        }
        lock (_writeLock)
        {
            using var connection = CreateConnection();
            connection.Open();
            using var transaction = BeginMutation(connection, "activate-embedding-generation");
            var generation = EnsureStagingGeneration(connection, transaction, generationId, "Embedding");
            var configuration = GetConfiguration(connection, transaction);
            EnsureConfigurationMatches(configuration, expectedConfiguration);
            EnsureGenerationMatchesConfiguration(generation, configuration);
            var activeText = GetActiveTextGenerationId(connection, transaction);
            if (generation.ParentTextGenerationId != activeText)
            {
                throw new OperationCanceledException("The embedding projection targets a stale text generation.");
            }
            EnsureEmbeddingCoverageComplete(
                connection,
                transaction,
                generationId,
                activeText!.Value,
                dimensions,
                configuration);
            Execute(connection, transaction, "UPDATE HistoryProjectionGenerations SET State = 'Superseded' WHERE ProjectionKind = 'Embedding' AND State = 'Active';");
            CompleteGeneration(connection, transaction, generationId, dimensions);
            using (var state = connection.CreateCommand())
            {
                state.Transaction = transaction;
                state.CommandText = """
                    UPDATE HistoryProjectionState
                    SET ActiveEmbeddingGenerationId = $generationId,
                        SemanticReady = 1
                    WHERE Id = 1;
                    """;
                state.Parameters.AddWithValue("$generationId", generationId);
                state.ExecuteNonQuery();
            }
            DeleteInactiveEmbeddingProjectionData(connection, transaction, generationId);
            CommitMutation(connection, transaction);
        }
    }

    internal void SaveEmbedding(
        long embeddingGenerationId,
        long textGenerationId,
        HistoryProjectionConfiguration expectedConfiguration,
        HistoryEmbeddingWorkItem document,
        int dimensions,
        byte[] vector)
    {
        EnsureAvailable();
        if (dimensions is <= 0 or > HistorySearchLimits.MaximumVectorDimensions
            || vector.Length != checked(dimensions * sizeof(float)))
        {
            throw new InvalidOperationException("Embedding vector storage is invalid.");
        }
        lock (_writeLock)
        {
            using var connection = CreateConnection();
            connection.Open();
            using var transaction = BeginMutation(connection, "save-embedding");
            var configuration = GetConfiguration(connection, transaction);
            EnsureConfigurationMatches(configuration, expectedConfiguration);
            var generation = GetGeneration(connection, transaction, embeddingGenerationId)
                ?? throw new OperationCanceledException("The embedding generation no longer exists.");
            if (generation.ProjectionKind != "Embedding" || generation.State is not ("Staging" or "Active"))
            {
                throw new OperationCanceledException("The embedding generation is no longer writable.");
            }
            EnsureGenerationRuntimeEpoch(generation);
            EnsureGenerationMatchesConfiguration(generation, configuration);
            if (generation.ParentTextGenerationId != textGenerationId
                || !DocumentHashMatches(connection, transaction, textGenerationId, document.DocumentId, document.ProjectionHash))
            {
                throw new OperationCanceledException("The embedding source document was superseded.");
            }
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO HistoryEmbeddings (
                    EmbeddingGenerationId, DocumentId, TextGenerationId, ProviderPackageId,
                    ProviderId, ModelId, ConfigurationRevision, EmbeddingSpaceFingerprint,
                    DocumentProjectionHash, Dimensions, Vector)
                VALUES ($embeddingGenerationId, $documentId, $textGenerationId, $providerPackageId,
                        $providerId, $modelId, $configurationRevision, $spaceFingerprint,
                        $projectionHash, $dimensions, $vector)
                ON CONFLICT(EmbeddingGenerationId, DocumentId) DO UPDATE SET
                    TextGenerationId = excluded.TextGenerationId,
                    ProviderPackageId = excluded.ProviderPackageId,
                    ProviderId = excluded.ProviderId,
                    ModelId = excluded.ModelId,
                    ConfigurationRevision = excluded.ConfigurationRevision,
                    EmbeddingSpaceFingerprint = excluded.EmbeddingSpaceFingerprint,
                    DocumentProjectionHash = excluded.DocumentProjectionHash,
                    Dimensions = excluded.Dimensions,
                    Vector = excluded.Vector;
                """;
            command.Parameters.AddWithValue("$embeddingGenerationId", embeddingGenerationId);
            command.Parameters.AddWithValue("$documentId", document.DocumentId);
            command.Parameters.AddWithValue("$textGenerationId", textGenerationId);
            command.Parameters.AddWithValue("$providerPackageId", configuration.EmbeddingProviderPackageId!);
            command.Parameters.AddWithValue("$providerId", configuration.EmbeddingProviderId!);
            command.Parameters.AddWithValue("$modelId", configuration.EmbeddingModelId!);
            command.Parameters.AddWithValue("$configurationRevision", configuration.Revision);
            command.Parameters.AddWithValue("$spaceFingerprint", configuration.EmbeddingSpaceFingerprint!);
            command.Parameters.AddWithValue("$projectionHash", document.ProjectionHash);
            command.Parameters.AddWithValue("$dimensions", dimensions);
            command.Parameters.Add("$vector", SqliteType.Blob).Value = vector;
            command.ExecuteNonQuery();
            CommitMutation(connection, transaction);
        }
    }

    internal IReadOnlyList<HistoryEmbeddingWorkItem> ListEmbeddingWorkPage(
        long textGenerationId,
        string? afterDocumentId,
        int limit)
        => ListEmbeddingWorkPage(textGenerationId, embeddingGenerationId: null, afterDocumentId, limit);

    internal IReadOnlyList<HistoryEmbeddingWorkItem> ListMissingEmbeddingWorkPage(
        long textGenerationId,
        long embeddingGenerationId,
        string? afterDocumentId,
        int limit)
        => ListEmbeddingWorkPage(textGenerationId, embeddingGenerationId, afterDocumentId, limit);

    internal IReadOnlyList<Guid> ListIndexedSessionIds(long generationId)
    {
        EnsureAvailable();
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT SessionId FROM HistoryDocuments WHERE GenerationId = $generationId;";
        command.Parameters.AddWithValue("$generationId", generationId);
        using var reader = command.ExecuteReader();
        var values = new List<Guid>();
        while (reader.Read())
        {
            values.Add(Guid.Parse(reader.GetString(0)));
        }
        return values;
    }

    internal IReadOnlyList<Guid> ListIndexedTurnIds(long generationId, Guid sessionId)
    {
        EnsureAvailable();
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT TurnId FROM HistoryDocuments
            WHERE GenerationId = $generationId AND SessionId = $sessionId;
            """;
        command.Parameters.AddWithValue("$generationId", generationId);
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        using var reader = command.ExecuteReader();
        var values = new List<Guid>();
        while (reader.Read())
        {
            values.Add(Guid.Parse(reader.GetString(0)));
        }
        return values;
    }

    internal void DeactivateEmbeddings()
    {
        if (!IsAvailable)
        {
            return;
        }
        lock (_writeLock)
        {
            using var connection = CreateConnection();
            connection.Open();
            using var transaction = BeginMutation(connection, "deactivate-embeddings");
            RemoveAllEmbeddingData(connection, transaction);
            Execute(connection, transaction, "UPDATE HistoryProjectionState SET ActiveEmbeddingGenerationId = NULL, SemanticReady = 0 WHERE Id = 1;");
            CommitMutation(connection, transaction);
        }
    }

    internal bool InvalidateStaleActiveGenerations()
    {
        if (!IsAvailable)
        {
            return false;
        }
        lock (_writeLock)
        {
            using var connection = CreateConnection();
            connection.Open();
            using var transaction = BeginMutation(connection, "invalidate-stale-generations");
            long? activeTextGenerationId;
            long? activeEmbeddingGenerationId;
            using (var state = connection.CreateCommand())
            {
                state.Transaction = transaction;
                state.CommandText = """
                    SELECT ActiveTextGenerationId, ActiveEmbeddingGenerationId
                    FROM HistoryProjectionState WHERE Id = 1;
                    """;
                using var reader = state.ExecuteReader();
                if (!reader.Read())
                {
                    throw new InvalidDataException("History projection state singleton is missing.");
                }
                activeTextGenerationId = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                activeEmbeddingGenerationId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            }

            var activeText = activeTextGenerationId is { } textGenerationId
                ? GetGeneration(connection, transaction, textGenerationId)
                : null;
            var activeEmbedding = activeEmbeddingGenerationId is { } embeddingGenerationId
                ? GetGeneration(connection, transaction, embeddingGenerationId)
                : null;
            if (activeText is not null)
            {
                EnsureGenerationRuntimeEpoch(activeText);
            }
            if (activeEmbedding is not null)
            {
                EnsureGenerationRuntimeEpoch(activeEmbedding);
            }
            var staleText = activeText is not null && !UsesCurrentProvenance(activeText);
            var staleEmbedding = activeEmbedding is not null && !UsesCurrentProvenance(activeEmbedding);
            if (!staleText && !staleEmbedding)
            {
                CommitMutation(connection, transaction);
                return false;
            }

            if (staleText)
            {
                DeleteTextGenerationData(connection, transaction, activeText!.GenerationId);
                RemoveAllEmbeddingData(connection, transaction);
                using (var generation = connection.CreateCommand())
                {
                    generation.Transaction = transaction;
                    generation.CommandText = "DELETE FROM HistoryProjectionGenerations WHERE GenerationId = $generationId;";
                    generation.Parameters.AddWithValue("$generationId", activeText.GenerationId);
                    generation.ExecuteNonQuery();
                }
                Execute(connection, transaction, """
                    UPDATE HistoryProjectionState
                    SET ActiveTextGenerationId = NULL,
                        ActiveEmbeddingGenerationId = NULL,
                        SemanticReady = 0,
                        LastReconciledAtUtc = NULL
                    WHERE Id = 1;
                    """);
            }
            else
            {
                RemoveAllEmbeddingData(connection, transaction);
                Execute(connection, transaction, """
                    UPDATE HistoryProjectionState
                    SET ActiveEmbeddingGenerationId = NULL,
                        SemanticReady = 0
                    WHERE Id = 1;
                    """);
            }
            CommitMutation(connection, transaction);
            return true;
        }
    }

    internal void MarkReconciled(DateTimeOffset reconciledAtUtc)
    {
        if (!IsAvailable)
        {
            return;
        }
        lock (_writeLock)
        {
            using var connection = CreateConnection();
            connection.Open();
            using var transaction = BeginMutation(connection, "mark-reconciled");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE HistoryProjectionState SET LastReconciledAtUtc = $value WHERE Id = 1;";
            command.Parameters.AddWithValue("$value", reconciledAtUtc.ToString("O"));
            command.ExecuteNonQuery();
            CommitMutation(connection, transaction);
        }
    }

    private static bool UsesCurrentProvenance(HistoryProjectionGeneration generation)
        => generation.ExtractorVersion == HistorySearchVersions.Extractor
           && generation.RedactionVersion == HistorySearchVersions.Redaction;

    internal void SetManuallyCleared(bool value)
    {
        if (!IsAvailable)
        {
            return;
        }
        lock (_writeLock)
        {
            using var connection = CreateConnection();
            connection.Open();
            using var transaction = BeginMutation(connection, "set-manually-cleared");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE HistoryProjectionState SET IsManuallyCleared = $value WHERE Id = 1;";
            command.Parameters.AddWithValue("$value", value ? 1 : 0);
            command.ExecuteNonQuery();
            CommitMutation(connection, transaction);
        }
    }

    internal HistoryProjectionConfiguration ClearDerivedIndex()
    {
        EnsureAvailable();
        lock (_writeLock)
        {
            using var connection = CreateConnection();
            connection.Open();
            using var transaction = BeginMutation(connection, "clear-derived-index");
            var current = GetConfiguration(connection, transaction);
            var next = current with { Revision = checked(current.Revision + 1) };
            SaveConfiguration(connection, transaction, next);
            Execute(connection, transaction, "DELETE FROM HistoryDocumentsFts;");
            Execute(connection, transaction, "DELETE FROM HistoryEmbeddings;");
            Execute(connection, transaction, "DELETE FROM HistoryDocumentFacets;");
            Execute(connection, transaction, "DELETE FROM HistoryDocuments;");
            Execute(connection, transaction, "DELETE FROM HistoryProjectionGenerations;");
            Execute(connection, transaction, """
                UPDATE HistoryProjectionState
                SET ActiveTextGenerationId = NULL,
                    ActiveEmbeddingGenerationId = NULL,
                    SemanticReady = 0,
                    IsManuallyCleared = 1,
                    LastReconciledAtUtc = NULL
                WHERE Id = 1;
                """);
            CommitMutation(connection, transaction);
            CheckpointTruncate(connection);
            return next;
        }
    }

    internal bool DeriveSemanticReadiness()
    {
        if (!IsAvailable)
        {
            return false;
        }
        lock (_writeLock)
        {
            using var connection = CreateConnection();
            connection.Open();
            using var transaction = BeginMutation(connection, "derive-semantic-readiness");
            var ready = SetSemanticReadyFromInvariants(connection, transaction);
            CommitMutation(connection, transaction);
            return ready;
        }
    }

    internal bool MarkSemanticReadyIfComplete(
        long textGenerationId,
        long embeddingGenerationId,
        int dimensions,
        HistoryProjectionConfiguration expectedConfiguration)
    {
        EnsureAvailable();
        lock (_writeLock)
        {
            using var connection = CreateConnection();
            connection.Open();
            using var transaction = BeginMutation(connection, "mark-semantic-ready");
            var configuration = GetConfiguration(connection, transaction);
            EnsureConfigurationMatches(configuration, expectedConfiguration);
            var generation = GetGeneration(connection, transaction, embeddingGenerationId);
            if (generation is not null)
            {
                EnsureGenerationRuntimeEpoch(generation);
            }
            if (generation?.State != "Active"
                || generation.ParentTextGenerationId != textGenerationId
                || generation.Dimensions != dimensions)
            {
                CommitMutation(connection, transaction);
                return false;
            }
            var ready = SetSemanticReadyFromInvariants(connection, transaction);
            CommitMutation(connection, transaction);
            return ready;
        }
    }

    internal void MarkSemanticUnavailable()
    {
        if (!IsAvailable)
        {
            return;
        }
        lock (_writeLock)
        {
            using var connection = CreateConnection();
            connection.Open();
            using var transaction = BeginMutation(connection, "mark-semantic-unavailable");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE HistoryProjectionState SET SemanticReady = 0 WHERE Id = 1 AND SemanticReady <> 0;";
            command.ExecuteNonQuery();
            CommitMutation(connection, transaction);
        }
    }

    internal bool TryRecover(Exception exception)
    {
        if (!IsConfirmedProjectionFailure(exception))
        {
            return false;
        }
        lock (_recoveryLock)
        {
            try
            {
                lock (_writeLock)
                {
                    EnsureCommittedRuntimeEpochCurrent();
                    RecreateDerivedDatabase(configurationRevision: 0, manuallyCleared: false);
                    EnsureCommittedRuntimeEpochCurrent();
                    IsAvailable = true;
                    WasRecovered = true;
                    FailureCode = null;
                }
                return true;
            }
            catch
            {
                IsAvailable = false;
                FailureCode = "projection-store-unavailable";
                return false;
            }
        }
    }
}
