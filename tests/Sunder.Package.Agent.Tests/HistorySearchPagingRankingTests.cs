using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class HistorySearchPagingRankingTests
{
    [Fact]
    public void FinalOrdering_UsesCreatedAtThenDocumentIdWhenRrfScoresActuallyTie()
    {
        var timestamp = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
        var laneRanks = new[] { new HistoryLexicalLaneRank(HistoryLexicalLaneKind.AllExact, 7) };
        var candidates = new[]
        {
            Candidate("tie-b", timestamp, laneRanks),
            Candidate("older", timestamp.AddTicks(-1), laneRanks),
            Candidate("newer-z", timestamp.AddTicks(1), laneRanks),
            Candidate("tie-a", timestamp, laneRanks),
        };
        Assert.Single(candidates.Select(static candidate => candidate.Score).Distinct());

        var expected = new[] { "newer-z", "tie-a", "tie-b", "older" };
        foreach (var inserted in new[] { candidates, candidates.Reverse().ToArray() })
        {
            var ordered = HistorySearchRankingPolicy.OrderCandidates(inserted, inserted.Length);
            Assert.Equal(expected, ordered.Select(static candidate => candidate.DocumentId));
            Assert.Equal(Enumerable.Range(1, expected.Length), ordered.Select(static candidate => candidate.Rank));
            foreach (var pageSize in new[] { 1, 2, 3 })
            {
                Assert.Equal(expected, ordered.Chunk(pageSize)
                    .SelectMany(static page => page)
                    .Select(static candidate => candidate.DocumentId));
            }
        }
    }

    [Fact]
    public async Task PagingIsDeterministicWithoutDuplicatesAndVersionChangesRestartSafely()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var services = CreateRuntime(scope);
        var workspace = services.GetRequiredService<AgentWorkspaceService>()
            .CreateWorkspace("Ranked paging");
        var sessions = services.GetRequiredService<AgentSessionService>();
        var session = sessions.CreateSession("Paging session", workspaceId: workspace.WorkspaceId);
        for (var index = 0; index < 47; index++)
        {
            sessions.AppendTextTurn(
                session.SessionId,
                index % 2 == 0 ? AgentMessageRole.User : AgentMessageRole.Assistant,
                $"deterministic paging evidence shared word item{index:D2}");
        }
        using (var connection = new SqliteConnection(
                   $"Data Source={services.GetRequiredService<Sunder.Package.Agent.Storage.AgentLocalStore>().DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE AgentTurns
                SET CreatedAtUtc = '2026-07-25T12:00:00.0000000+00:00',
                    UpdatedAtUtc = '2026-07-25T12:00:00.0000000+00:00';
                """;
            command.ExecuteNonQuery();
        }
        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        var projection = services.GetRequiredService<HistorySearchStore>();
        var search = services.GetRequiredService<HistorySearchService>();
        await indexer.StartAsync();
        await WaitUntilAsync(() => projection.GetSnapshot().DocumentCount == 47);

        var firstPass = await ReadAllPagesAsync(search, "paging shared", 7);
        var secondPass = await ReadAllPagesAsync(search, "paging shared", 11);

        Assert.Equal(47, firstPass.Count);
        Assert.Equal(47, firstPass.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(firstPass, secondPass);

        var firstPage = await search.SearchAsync(new HistorySearchRequest("paging shared", Limit: 5));
        var continuation = Assert.IsType<string>(firstPage.Continuation);
        var wrongRankingVersion = RewriteContinuationVersion(
            continuation,
            "RankingVersion",
            HistorySearchVersions.RankingVersion,
            HistorySearchVersions.RankingVersion + 1);
        var restarted = await search.SearchAsync(new HistorySearchRequest(
            "paging shared",
            Limit: 5,
            Continuation: wrongRankingVersion));

        Assert.True(restarted.Restarted);
        Assert.True(restarted.IsPartial);
        Assert.Equal(firstPage.Results.Select(static hit => hit.DocumentId),
            restarted.Results.Select(static hit => hit.DocumentId));

        var legacy = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"Version\":1,\"Offset\":5}"));
        var legacyRestart = await search.SearchAsync(new HistorySearchRequest(
            "paging shared",
            Limit: 5,
            Continuation: legacy));
        Assert.True(legacyRestart.Restarted);
        Assert.Equal(firstPage.Results[0].DocumentId, legacyRestart.Results[0].DocumentId);
        await indexer.StopAsync();
    }

    [Fact]
    public async Task RuntimeHitUsesMatchCenteredRedactedBodySnippetNearTheEnd()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var services = CreateRuntime(scope);
        var workspace = services.GetRequiredService<AgentWorkspaceService>()
            .CreateWorkspace("Snippets");
        var sessions = services.GetRequiredService<AgentSessionService>();
        var session = sessions.CreateSession("Snippet session", workspaceId: workspace.WorkspaceId);
        var body = string.Join(' ', Enumerable.Repeat("opening", 170))
                   + "\npassword = \"quoted-late-secret\""
                   + "\nTOKEN: colon-late-secret"
                   + "\n\\\"api_key\\\":\\\"escaped-late-secret\\\""
                   + "\nauthorization: Basic authorization-late-secret"
                   + "\ntarget-near-end closing";
        sessions.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, body);
        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        var projection = services.GetRequiredService<HistorySearchStore>();
        var search = services.GetRequiredService<HistorySearchService>();
        await indexer.StartAsync();
        await WaitUntilAsync(() => projection.GetSnapshot().DocumentCount == 1);

        var response = await search.SearchAsync(new HistorySearchRequest("target-near-end"));
        var hit = Assert.Single(response.Results);

        Assert.Contains("target-near-end", hit.Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quoted-late-secret", hit.Snippet, StringComparison.Ordinal);
        Assert.DoesNotContain("colon-late-secret", hit.Snippet, StringComparison.Ordinal);
        Assert.DoesNotContain("escaped-late-secret", hit.Snippet, StringComparison.Ordinal);
        Assert.DoesNotContain("authorization-late-secret", hit.Snippet, StringComparison.Ordinal);
        Assert.InRange(hit.Snippet.Length, 1, HistorySearchLimits.MaximumSnippetCharacters);
        Assert.True(hit.Snippet.Length < body.Length);
        Assert.Contains(hit.MatchReasons, static reason =>
            reason is "All words match" or "All word prefixes match" or "Phrase match");
        await indexer.StopAsync();
    }

    private static async Task<IReadOnlyList<string>> ReadAllPagesAsync(
        HistorySearchService search,
        string query,
        int limit)
    {
        var results = new List<string>();
        string? continuation = null;
        var seenContinuations = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var response = await search.SearchAsync(new HistorySearchRequest(
                query,
                Limit: limit,
                Continuation: continuation));
            results.AddRange(response.Results.Select(static result => result.DocumentId));
            continuation = response.Continuation;
            if (continuation is not null)
            {
                Assert.True(seenContinuations.Add(continuation));
            }
        } while (continuation is not null);
        return results;
    }

    private static string RewriteContinuationVersion(
        string continuation,
        string property,
        int current,
        int replacement)
    {
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(continuation));
        var expected = $"\"{property}\":{current}";
        Assert.Contains(expected, json, StringComparison.Ordinal);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(
            json.Replace(expected, $"\"{property}\":{replacement}", StringComparison.Ordinal)));
    }

    private static HistoryLexicalCandidate Candidate(
        string documentId,
        DateTimeOffset timestamp,
        IReadOnlyList<HistoryLexicalLaneRank> laneRanks)
        => new(
            documentId,
            Rank: 0,
            Tier: 1,
            Score: 0.25,
            timestamp,
            SourceContentRevision: 1,
            ProjectionHash: new string('a', 64),
            Reasons: ["All words match"],
            laneRanks);

    private static ServiceProvider CreateRuntime(RegressionTestPackageScope scope)
    {
        var services = new ServiceCollection();
        services.AddSingleton(scope.Context);
        services.AddSingleton<IPackageContext>(scope.Context);
        services.AddSingleton<Sunder.Package.Agent.Protocol.AgentRpcCatalog>(new RegressionTestExtensionCatalog());
        services.AddSingleton<IBackgroundProcessQueue, CompositionBackgroundProcessQueue>();
        new PackageModule().ConfigureRuntimeServices(services, scope.Context);
        return services.BuildServiceProvider();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for History Search indexing.");
            }
            await Task.Delay(20);
        }
    }
}
