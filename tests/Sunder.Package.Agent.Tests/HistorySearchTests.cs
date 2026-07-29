using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Package.Agent.Subagents.Runtime;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class HistorySearchTests
{
    [Fact]
    public void ProjectionStore_CreatesIndependentSchemaAndDefaultsSemanticOff()
    {
        using var scope = RegressionTestPackageScope.Create();

        var projection = new HistorySearchStore(scope.Context);

        Assert.True(projection.IsAvailable);
        Assert.EndsWith(Path.Combine("agent", "history-search.db"), projection.DatabasePath, StringComparison.Ordinal);
        Assert.False(File.Exists(scope.Context.Storage.RoleLocalWorkspace.GetLocalPath("agent/agent.db")));
        Assert.Equal(new HistoryProjectionConfiguration(false, null, null, null, 0, null), projection.GetConfiguration());
        using var connection = Open(projection.DatabasePath);
        Assert.Equal(HistorySearchVersions.Schema, ExecuteInt64(connection, "SELECT Version FROM HistorySchemaMarker WHERE Id = 1;"));
        Assert.Equal("wal", ExecuteString(connection, "PRAGMA journal_mode;"));
        Assert.Equal(1L, ExecuteInt64(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'HistoryDocumentsFts';"));
    }

    [Fact]
    public void ProjectionStore_DeletesInvalidSchemaAndSidecarsBeforeRecreatingDerivedIndex()
    {
        using var scope = RegressionTestPackageScope.Create();
        var original = new HistorySearchStore(scope.Context);
        using (var connection = Open(original.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE HistorySchemaMarker SET Version = 999 WHERE Id = 1;";
            command.ExecuteNonQuery();
        }

        var recovered = new HistorySearchStore(scope.Context);

        Assert.True(recovered.IsAvailable);
        Assert.True(recovered.WasRecovered);
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(recovered.DatabasePath)!,
            "history-search.db.corrupt-*"));
        using var verification = Open(recovered.DatabasePath);
        Assert.Equal(HistorySearchVersions.Schema, ExecuteInt64(verification, "SELECT Version FROM HistorySchemaMarker WHERE Id = 1;"));
    }

    [Fact]
    public void ProjectionStore_FailedGenerationLeavesPreviousGenerationSearchable()
    {
        using var scope = RegressionTestPackageScope.Create();
        var projection = new HistorySearchStore(scope.Context);
        var active = projection.BeginTextGeneration();
        var first = CreateDocument("previous searchable content", path: "src/previous.cs");
        projection.ReplaceTurnDocuments(active, first.SessionId, first.TurnId, [first]);
        projection.ActivateTextGeneration(active);
        var failed = projection.BeginTextGeneration();
        var replacement = CreateDocument("replacement content", path: "src/replacement.cs");
        projection.ReplaceTurnDocuments(failed, replacement.SessionId, replacement.TurnId, [replacement]);

        projection.FailGeneration(failed, "test-failure");

        Assert.Equal(active, projection.GetSnapshot().ActiveTextGenerationId);
        Assert.Single(projection.SearchLexical(active, new HistorySearchRequest("previous"), 10));
        Assert.Empty(projection.SearchLexical(active, new HistorySearchRequest("replacement"), 10));
    }

    [Fact]
    public void ProjectionStore_SuccessfulActivationDiscardsSupersededGenerationData()
    {
        using var scope = RegressionTestPackageScope.Create();
        var projection = new HistorySearchStore(scope.Context);
        var firstGeneration = projection.BeginTextGeneration();
        var first = CreateDocument("first generation");
        projection.ReplaceTurnDocuments(firstGeneration, first.SessionId, first.TurnId, [first]);
        projection.ActivateTextGeneration(firstGeneration);
        var secondGeneration = projection.BeginTextGeneration();
        var second = CreateDocument("second generation");
        projection.ReplaceTurnDocuments(secondGeneration, second.SessionId, second.TurnId, [second]);

        projection.ActivateTextGeneration(secondGeneration);

        using var connection = Open(projection.DatabasePath);
        Assert.Equal(1L, ExecuteInt64(connection, "SELECT COUNT(*) FROM HistoryProjectionGenerations;"));
        Assert.Equal(1L, ExecuteInt64(connection, "SELECT COUNT(*) FROM HistoryDocuments;"));
        Assert.Equal(1L, ExecuteInt64(connection, "SELECT COUNT(*) FROM HistoryDocumentsFts;"));
        Assert.Empty(projection.SearchLexical(secondGeneration, new HistorySearchRequest("first"), 10));
        Assert.Single(projection.SearchLexical(secondGeneration, new HistorySearchRequest("second"), 10));
    }

    [Fact]
    public void ProjectionStore_UnchangedReconciliationPreservesActiveEmbedding()
    {
        using var scope = RegressionTestPackageScope.Create();
        var projection = new HistorySearchStore(scope.Context);
        var textGeneration = projection.BeginTextGeneration();
        var document = CreateDocument("stable semantic body");
        projection.ReplaceTurnDocuments(textGeneration, document.SessionId, document.TurnId, [document]);
        projection.ActivateTextGeneration(textGeneration);
        var configuration = projection.ChangeConfiguration(
            semanticEnabled: true,
            "test.package",
            "provider",
            "model",
            new string('a', 64));
        var embeddingGeneration = projection.BeginEmbeddingGeneration(textGeneration, configuration);
        Assert.True(HistoryVectorCodec.TryNormalize(
            new AgentEmbeddingGenerationResult("model", [1f, 0f]),
            "model",
            expectedDimensions: null,
            out var dimensions,
            out var vector));
        var work = Assert.Single(projection.ListEmbeddingWorkPage(textGeneration, null, 10));
        projection.SaveEmbedding(
            embeddingGeneration,
            textGeneration,
            configuration,
            work,
            dimensions,
            vector);
        projection.ActivateEmbeddingGeneration(embeddingGeneration, dimensions, configuration);
        Assert.True(projection.GetSnapshot().SemanticReady);

        projection.ReplaceTurnDocuments(textGeneration, document.SessionId, document.TurnId, [document]);

        Assert.Equal(1, projection.GetSnapshot().EmbeddingCount);
        Assert.Equal(embeddingGeneration, projection.GetSnapshot().ActiveEmbeddingGenerationId);

        projection.ReplaceTurnDocuments(
            textGeneration,
            document.SessionId,
            document.TurnId,
            [document with { BodyText = "changed semantic body" }]);
        Assert.Equal(0, projection.GetSnapshot().EmbeddingCount);
        Assert.False(projection.GetSnapshot().SemanticReady);
    }

    [Theory]
    [InlineData("resume", "Résumé alpha beta", null)]
    [InlineData("\"alpha beta\"", "Résumé alpha beta", null)]
    [InlineData("alph*", "Résumé alphabet soup", null)]
    [InlineData("src/100%", "unrelated", "src/100%/History_File.cs")]
    [InlineData("History_File", "unrelated", "src/100%/History_File.cs")]
    [InlineData("NEAR(()):", "NEAR harmless input", null)]
    public void LexicalSearch_EscapesInputAndSupportsUnicodePhrasePrefixAndFacets(
        string query,
        string body,
        string? path)
    {
        using var scope = RegressionTestPackageScope.Create();
        var projection = new HistorySearchStore(scope.Context);
        var generation = projection.BeginTextGeneration();
        var document = CreateDocument(body, path);
        projection.ReplaceTurnDocuments(generation, document.SessionId, document.TurnId, [document]);
        projection.ActivateTextGeneration(generation);

        var error = Record.Exception(() => projection.SearchLexical(generation, new HistorySearchRequest(query), 10));

        Assert.Null(error);
        if (query != "NEAR(()):")
        {
            Assert.Single(projection.SearchLexical(generation, new HistorySearchRequest(query), 10));
        }
    }

    [Fact]
    public void LexicalSearch_AppliesRoleActivityProfileWorkspaceAndDateFilters()
    {
        using var scope = RegressionTestPackageScope.Create();
        var projection = new HistorySearchStore(scope.Context);
        var generation = projection.BeginTextGeneration();
        var user = CreateDocument("shared filter phrase") with
        {
            WorkspaceId = "workspace-a",
            ProfileId = "profile-a",
            Role = AgentMessageRole.User,
            CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
        var activity = CreateDocument("shared filter phrase") with
        {
            DocumentId = Guid.NewGuid().ToString("N"),
            WorkspaceId = "workspace-b",
            ProfileId = "profile-b",
            Role = null,
            Activity = HistoryActivityKind.Edit,
            TurnId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            AnchorKind = HistoryAnchorKind.Activity,
            CreatedAtUtc = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
        };
        projection.ReplaceTurnDocuments(generation, user.SessionId, user.TurnId, [user]);
        projection.ReplaceTurnDocuments(generation, activity.SessionId, activity.TurnId, [activity]);
        projection.ActivateTextGeneration(generation);

        Assert.Single(projection.SearchLexical(generation, new HistorySearchRequest(
            "shared",
            WorkspaceId: "workspace-a",
            Role: HistorySearchRoleFilter.User), 10));
        Assert.Single(projection.SearchLexical(generation, new HistorySearchRequest(
            "shared",
            ProfileId: "profile-b",
            Activity: HistoryActivityKind.Edit), 10));
        Assert.Single(projection.SearchLexical(generation, new HistorySearchRequest(
            "shared",
            FromUtc: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero)), 10));
        Assert.Empty(projection.SearchLexical(generation, new HistorySearchRequest(
            "shared",
            WorkspaceId: "missing"), 10));
    }

    [Fact]
    public void Extractor_RedactsCredentialsAndIndexesOnlySafeActivityMetadata()
    {
        var session = CreateSourceSession();
        var textTurn = CreateTurn(
            AgentMessageRole.Assistant,
            AgentTurnKind.ToolCall,
            AgentTurnItemKind.Text,
            "Use token=top-secret and sk-proj-1234567890abcdefghijklmnop for the request.");

        var text = Assert.Single(HistorySearchExtractor.Extract(session, textTurn));

        Assert.DoesNotContain("top-secret", text.BodyText, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-proj-", text.BodyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED", text.BodyText, StringComparison.Ordinal);

        var patch = "*** Begin Patch\n*** Update File: src/History.cs\n-old secret hunk\n+new secret hunk\n*** End Patch";
        var patchTurn = CreateTurn(
            AgentMessageRole.Assistant,
            AgentTurnKind.ToolCall,
            AgentTurnItemKind.ToolCall,
            text: null,
            toolId: "apply_patch",
            argumentsJson: JsonSerializer.Serialize(new { patchText = patch }),
            toolOwnerPackageId: "sunder.package.agent.tools.files");
        var activity = Assert.Single(HistorySearchExtractor.Extract(
            session,
            patchTurn));

        Assert.Equal(HistoryActivityKind.Edit, activity.Activity);
        Assert.Contains(activity.Facets, facet => facet.Kind == "path" && facet.Value == "src/History.cs");
        Assert.DoesNotContain("secret hunk", activity.BodyText, StringComparison.Ordinal);

        var unknownTurn = CreateTurn(
            AgentMessageRole.Assistant,
            AgentTurnKind.ToolCall,
            AgentTurnItemKind.ToolCall,
            text: null,
            toolId: "vendor_custom_tool",
            argumentsJson: "{\"path\":\"private.txt\",\"operation\":\"inspect\",\"payload\":\"do-not-index\"}");
        var unknown = Assert.Single(HistorySearchExtractor.Extract(session, unknownTurn));
        Assert.DoesNotContain(unknown.Facets, facet => facet.Kind == "path");
        Assert.DoesNotContain("do-not-index", unknown.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public void Extractor_ProducesDeterministicBoundedOverlappingChunks()
    {
        var session = CreateSourceSession();
        var content = string.Join(' ', Enumerable.Repeat("deterministic history chunk", 400));
        var turn = CreateTurn(
            AgentMessageRole.User,
            AgentTurnKind.Message,
            AgentTurnItemKind.Text,
            content);

        var first = HistorySearchExtractor.Extract(session, turn);
        var second = HistorySearchExtractor.Extract(session, turn);

        Assert.True(first.Count > 1);
        Assert.Equal(first.Select(static document => document.DocumentId),
            second.Select(static document => document.DocumentId));
        Assert.All(first, document => Assert.InRange(document.BodyText.Length, 1, 1_800));
        Assert.All(first, document => Assert.InRange(document.DisplaySnippet.Length, 1, 1_600));
        Assert.Contains("deterministic history", first[0].BodyText, StringComparison.Ordinal);
        Assert.Contains("deterministic history", first[1].BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public void VectorCodec_NormalizesFloat32AndRejectsInvalidOrMismatchedVectors()
    {
        Assert.True(HistoryVectorCodec.TryNormalize(
            new AgentEmbeddingGenerationResult("model", [3f, 4f]),
            "model",
            expectedDimensions: null,
            out var dimensions,
            out var vector));
        Assert.Equal(2, dimensions);
        Assert.Equal(1d, HistoryVectorCodec.Dot(vector, vector, dimensions), precision: 6);
        Assert.False(HistoryVectorCodec.TryNormalize(
            new AgentEmbeddingGenerationResult("other", [3f, 4f]),
            "model",
            expectedDimensions: null,
            out _,
            out _));
        Assert.False(HistoryVectorCodec.TryNormalize(
            new AgentEmbeddingGenerationResult("model", [float.NaN, 1f]),
            "model",
            expectedDimensions: null,
            out _,
            out _));
        Assert.Equal(double.NegativeInfinity, HistoryVectorCodec.Dot(vector, [0], dimensions));
    }

    [Fact]
    public async Task RuntimeIndexer_SearchesAcrossRootAndChildSessionsAndValidatesStaleHits()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = new ServiceCollection();
        services.AddSingleton(scope.Context);
        services.AddSingleton<IPackageContext>(scope.Context);
        var extensionCatalog = new RegressionTestExtensionCatalog();
        var embeddingProvider = new HistoryEmbeddingProvider();
        extensionCatalog.AddExtension(
            PackageExtensionPoints.EmbeddingProviders,
            embeddingProvider);
        services.AddSingleton<IPackageExtensionCatalog>(extensionCatalog);
        services.AddSingleton<IBackgroundProcessQueue, CompositionBackgroundProcessQueue>();
        new PackageModule().ConfigureRuntimeServices(services, scope.Context);
        await using var provider = services.BuildServiceProvider();
        var sessions = provider.GetRequiredService<AgentSessionService>();
        var workspaces = provider.GetRequiredService<AgentWorkspaceService>();
        var workspace = workspaces.CreateWorkspace("History workspace");
        var root = sessions.CreateSession("Root", workspaceId: workspace.WorkspaceId);
        var child = sessions.CreateSession(
            "Child",
            parentSessionId: root.SessionId,
            rootSessionId: root.SessionId,
            workspaceId: workspace.WorkspaceId);
        var rootTurn = sessions.AppendTextTurn(root.SessionId, AgentMessageRole.User, "root pagination needle");
        sessions.AppendTextTurn(root.SessionId, AgentMessageRole.Assistant, "second pagination needle");
        sessions.AppendTextTurn(root.SessionId, AgentMessageRole.User, "third pagination needle");
        var childTurn = sessions.AppendTextTurn(child.SessionId, AgentMessageRole.Assistant, "child-only needle");
        var indexer = provider.GetRequiredService<HistorySearchIndexingService>();
        var projection = provider.GetRequiredService<HistorySearchStore>();
        var search = provider.GetRequiredService<HistorySearchService>();

        await indexer.StartAsync();
        await WaitUntilAsync(() => projection.GetSnapshot().DocumentCount == 4);

        var first = await search.SearchAsync(new HistorySearchRequest(
            "pagination needle",
            SessionId: root.SessionId,
            IncludeChildSessions: false,
            Limit: 2));
        Assert.Equal(2, first.Results.Count);
        Assert.NotNull(first.Continuation);
        var second = await search.SearchAsync(new HistorySearchRequest(
            "pagination needle",
            SessionId: root.SessionId,
            IncludeChildSessions: false,
            Limit: 2,
            Continuation: first.Continuation));
        Assert.Single(second.Results);
        Assert.Null(second.Continuation);

        var childIncluded = await search.SearchAsync(new HistorySearchRequest(
            "child-only",
            SessionId: root.SessionId,
            IncludeChildSessions: true));
        Assert.Single(childIncluded.Results);
        Assert.Equal(child.SessionId, childIncluded.Results[0].SessionId);
        Assert.Empty((await search.SearchAsync(new HistorySearchRequest(
            "child-only",
            SessionId: root.SessionId,
            IncludeChildSessions: false))).Results);

        Assert.False(projection.GetConfiguration().SemanticEnabled);
        await search.ExecuteCommandAsync(new HistorySearchCommand(
            HistorySearchCommandKind.ConfigureSemantic,
            SemanticEnabled: true,
            EmbeddingProviderId: HistoryEmbeddingProvider.ProviderId,
            EmbeddingModelId: HistoryEmbeddingProvider.ModelId));
        Assert.False(projection.GetConfiguration().SemanticEnabled);
        Assert.False(provider.GetRequiredService<HistorySearchRuntimeState>().Current.SemanticReady);
        await indexer.ReconcileNowAsync();
        var semantic = await search.SearchAsync(new HistorySearchRequest("conceptual relation"));
        Assert.Empty(semantic.Results);
        Assert.Null(projection.GetSnapshot().ActiveEmbeddingGenerationId);
        Assert.Equal(0, embeddingProvider.CatalogCallCount);
        Assert.Equal(0, embeddingProvider.QueryCallCount);

        await indexer.ClearDerivedIndexAsync();
        Assert.Equal(0, projection.GetSnapshot().DocumentCount);
        Assert.NotNull(provider.GetRequiredService<AgentLocalStore>().GetTurn(rootTurn.TurnId));
        indexer.RequestRebuild();
        await WaitUntilAsync(() => projection.GetSnapshot().DocumentCount == 4);

        var around = provider.GetRequiredService<AgentLocalStore>().LoadTranscriptAroundTurn(
            root.SessionId,
            rootTurn.TurnId,
            beforeLimit: 10,
            afterLimit: 1);
        Assert.Equal(rootTurn.TurnId, around.AnchorTurnId);
        Assert.Equal(2, around.Turns.Count);
        Assert.True(around.HasNewer);

        var childAround = await new SubsessionLocalRuntimeAdapter(
                provider.GetRequiredService<AgentRuntimeCatalog>())
            .LoadAroundTurnAsync(
                child.SessionId,
                childTurn.TurnId,
                childTurn.CreatedAtUtc,
                childTurn.Items[0].ItemId,
                beforeLimit: 20,
                afterLimit: 20);
        Assert.Equal(childTurn.TurnId, Assert.Single(childAround.Turns).TurnId);

        sessions.DeleteSession(child.SessionId);
        Assert.Empty((await search.SearchAsync(new HistorySearchRequest("child-only"))).Results);
        await indexer.StopAsync();
    }

    private static HistoryProjectionDocument CreateDocument(string body, string? path = null)
    {
        var turnId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var facets = path is null
            ? Array.Empty<HistoryProjectionFacet>()
            : [new HistoryProjectionFacet("path", path, path.ToLowerInvariant())];
        return new HistoryProjectionDocument(
            HistorySearchExtractor.CreateDocumentId(itemId, 0, HistoryAnchorKind.Text),
            "workspace",
            "Workspace",
            Guid.NewGuid(),
            "Session",
            RootSessionId: null,
            ParentSessionId: null,
            ProfileId: "profile",
            AgentMessageRole.User,
            HistoryActivityKind.None,
            turnId,
            itemId,
            CallId: null,
            HistoryAnchorKind.Text,
            SourceContentRevision: 1,
            SourceIsStreaming: false,
            DateTimeOffset.UtcNow,
            body,
            body,
            facets);
    }

    private static HistorySourceSession CreateSourceSession()
        => new(
            Guid.NewGuid(),
            "Session",
            "workspace",
            "Workspace",
            ParentSessionId: null,
            RootSessionId: null,
            ProfileId: "profile",
            ProfileName: "Profile",
            UpdatedAtUtc: DateTimeOffset.UtcNow);

    private static AgentTurnRecord CreateTurn(
        AgentMessageRole role,
        AgentTurnKind turnKind,
        AgentTurnItemKind itemKind,
        string? text,
        string? toolId = null,
        string? argumentsJson = null,
        string? toolOwnerPackageId = null)
    {
        var turnId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        return new AgentTurnRecord(
            turnId,
            Guid.NewGuid(),
            role,
            turnKind,
            [new AgentTurnItemRecord(
                Guid.NewGuid(),
                turnId,
                0,
                itemKind,
                text,
                itemKind == AgentTurnItemKind.ToolCall ? "call-1" : null,
                toolId,
                argumentsJson,
                ResultSummary: null,
                StructuredPayloadJson: null,
                SourcesJson: null,
                WasTruncated: false,
                IsError: false,
                ErrorCode: null,
                BackendId: null)
            {
                ToolOwnerPackageId = toolOwnerPackageId,
            }],
            now,
            now)
        {
            ContentRevision = 1,
        };
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    private static long ExecuteInt64(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string ExecuteString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for history indexing.");
            }
            await Task.Delay(20);
        }
    }

    private sealed class HistoryEmbeddingProvider : IAgentEmbeddingProvider
    {
        internal const string ProviderId = "history-test";
        internal const string ModelId = "history-test-v1";

        public AgentEmbeddingProviderDescriptor Descriptor { get; } =
            new(ProviderId, "History Test", []);

        internal int CatalogCallCount { get; private set; }
        internal int QueryCallCount { get; private set; }

        public ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> GetAvailableModelsAsync(
            CancellationToken cancellationToken = default)
        {
            CatalogCallCount++;
            return ValueTask.FromResult<IReadOnlyList<AgentEmbeddingModelDescriptor>>([
                new AgentEmbeddingModelDescriptor(ModelId, "History Test V1", Dimensions: 2),
            ]);
        }

        public ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(
            CancellationToken cancellationToken = default)
        {
            CatalogCallCount++;
            return ValueTask.FromResult(new AgentEmbeddingProviderReadiness(
                ProviderId,
                AgentProviderReadinessStatus.Ready,
                "Ready."));
        }

        public ValueTask<AgentEmbeddingGenerationResult?> GenerateEmbeddingAsync(
            string modelId,
            string text,
            CancellationToken cancellationToken = default)
        {
            QueryCallCount++;
            return ValueTask.FromResult<AgentEmbeddingGenerationResult?>(
                new AgentEmbeddingGenerationResult(modelId, Vector(text)));
        }

        public ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateEmbeddingsAsync(
            string modelId,
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
        {
            QueryCallCount++;
            return ValueTask.FromResult<IReadOnlyList<AgentEmbeddingGenerationResult?>>(
                texts.Select(text => (AgentEmbeddingGenerationResult?)new AgentEmbeddingGenerationResult(
                    modelId,
                    Vector(text))).ToArray());
        }

        private static IReadOnlyList<float> Vector(string text)
            => text.Contains("child", StringComparison.OrdinalIgnoreCase)
               || text.Contains("conceptual", StringComparison.OrdinalIgnoreCase)
                ? [1f, 0f]
                : [0f, 1f];
    }
}
