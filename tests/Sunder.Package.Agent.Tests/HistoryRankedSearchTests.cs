using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.HistorySearch;
using System.Text;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class HistoryRankedSearchTests
{
    [Fact]
    public void RankedLanes_OrderPhraseThenAllThenAnyAndReturnAnyMatchingWord()
    {
        using var scope = RegressionTestPackageScope.Create();
        var projection = new HistorySearchStore(scope.Context);
        var generation = projection.BeginTextGeneration();
        Insert(projection, generation, CreateDocument("phrase", "alpha beta"));
        Insert(projection, generation, CreateDocument("all", "alpha separating beta"));
        Insert(projection, generation, CreateDocument("prefix", "alpha betamax"));
        Insert(projection, generation, CreateDocument("any-alpha", "alpha alone"));
        Insert(projection, generation, CreateDocument("any-beta", "beta alone"));
        Insert(projection, generation, CreateDocument("unrelated", "gamma alone"));
        projection.ActivateTextGeneration(generation);

        var results = projection.SearchLexical(
            generation,
            new HistorySearchRequest("alpha beta"),
            HistorySearchLimits.CandidateLimit);

        Assert.Equal(["phrase", "all", "prefix"],
            results.Take(3).Select(static result => result.DocumentId));
        Assert.Equal(["any-alpha", "any-beta"],
            results.Skip(3).Select(static result => result.DocumentId).Order(StringComparer.Ordinal));
        Assert.Equal([0, 1, 1, 2, 2], results.Select(static result => result.Tier));
        Assert.Contains("Phrase match", results[0].Reasons);
        Assert.Contains("All words match", results[1].Reasons);
        Assert.Contains("All word prefixes match", results[2].Reasons);
        Assert.All(results.Skip(3), static result => Assert.Contains("Any word match", result.Reasons));
        Assert.True(results[0].LaneRanks.Count > results[1].LaneRanks.Count);
    }

    [Fact]
    public void ExactEvidenceRanksAbovePrefixAndPrefixNeverMatchesInfix()
    {
        using var scope = RegressionTestPackageScope.Create();
        var projection = new HistorySearchStore(scope.Context);
        var generation = projection.BeginTextGeneration();
        Insert(projection, generation, CreateDocument("exact", "occur"));
        Insert(projection, generation, CreateDocument("past", "occurred"));
        Insert(projection, generation, CreateDocument("noun", "occurrence"));
        Insert(projection, generation, CreateDocument("infix", "reoccur"));
        projection.ActivateTextGeneration(generation);

        var results = projection.SearchLexical(generation, new HistorySearchRequest("occur"), 20);

        Assert.Equal("exact", results[0].DocumentId);
        Assert.Equal(
            ["exact", "noun", "past"],
            results.Select(static result => result.DocumentId).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(results, static result => result.DocumentId == "infix");
        Assert.Contains(results[0].LaneRanks, static lane => lane.Lane == HistoryLexicalLaneKind.AllExact);
        Assert.DoesNotContain(results[1].LaneRanks, static lane => lane.Lane == HistoryLexicalLaneKind.AllExact);
    }

    [Fact]
    public void ClosedQuoteRemainsStrictWhileUnquotedSearchUsesBroadRecall()
    {
        using var scope = RegressionTestPackageScope.Create();
        var projection = new HistorySearchStore(scope.Context);
        var generation = projection.BeginTextGeneration();
        Insert(projection, generation, CreateDocument("strict", "alpha beta gamma"));
        Insert(projection, generation, CreateDocument("separated", "alpha intervening beta gamma"));
        Insert(projection, generation, CreateDocument("missing", "alpha beta delta"));
        projection.ActivateTextGeneration(generation);

        var results = projection.SearchLexical(
            generation,
            new HistorySearchRequest("\"alpha beta\" gamma"),
            20);

        Assert.Equal("strict", Assert.Single(results).DocumentId);
    }

    [Fact]
    public void FacetLanes_UseUnquotedTextAndEnforceEveryRequiredPhrase()
    {
        using var scope = RegressionTestPackageScope.Create();
        var projection = new HistorySearchStore(scope.Context);
        var generation = projection.BeginTextGeneration();
        Insert(projection, generation, CreateDocument(
            "strict-facet",
            "alpha beta in order",
            [Facet("path", "src/History.cs")]));
        Insert(projection, generation, CreateDocument(
            "separated-facet",
            "alpha intervening beta",
            [Facet("path", "src/History.cs")]));
        Insert(projection, generation, CreateDocument(
            "missing-facet",
            "unrelated body",
            [Facet("path", "src/History.cs")]));
        projection.ActivateTextGeneration(generation);

        var results = projection.SearchLexical(
            generation,
            new HistorySearchRequest("\"alpha beta\" src/History.cs"),
            20);

        var result = Assert.Single(results);
        Assert.Equal("strict-facet", result.DocumentId);
        Assert.Contains("Path exact match", result.Reasons);
    }

    [Fact]
    public void FacetPrefixLane_RequiresTwoScalarsOrAnExplicitStar()
    {
        using var scope = RegressionTestPackageScope.Create();
        var projection = new HistorySearchStore(scope.Context);
        var generation = projection.BeginTextGeneration();
        Insert(projection, generation, CreateDocument(
            "facet-prefix",
            "metadata only",
            [Facet("path", "alpha-path")]));
        projection.ActivateTextGeneration(generation);

        Assert.Empty(projection.SearchLexical(generation, new HistorySearchRequest("a"), 20));
        var explicitPrefix = Assert.Single(projection.SearchLexical(
            generation,
            new HistorySearchRequest("a*"),
            20));
        Assert.Contains("Path prefix match", explicitPrefix.Reasons);
    }

    [Fact]
    public void ExactAndPrefixFacetLanesDeduplicateDocumentsAndExplainFacetKinds()
    {
        using var scope = RegressionTestPackageScope.Create();
        var projection = new HistorySearchStore(scope.Context);
        var generation = projection.BeginTextGeneration();
        var exact = CreateDocument(
            "facet-exact",
            "metadata only",
            [
                Facet("path", "src/History.cs"),
                Facet("symbol", "src/History.cs"),
                Facet("path", "src/History.cs"),
            ]);
        var prefix = CreateDocument(
            "facet-prefix",
            "metadata only",
            [Facet("path", "src/HistoryExtras.cs")]);
        Insert(projection, generation, exact);
        Insert(projection, generation, prefix);
        projection.ActivateTextGeneration(generation);

        var exactResults = projection.SearchLexical(
            generation,
            new HistorySearchRequest("src/History.cs"),
            20);
        var prefixResults = projection.SearchLexical(
            generation,
            new HistorySearchRequest("src/History"),
            20);

        var exactResult = Assert.Single(exactResults, static result => result.DocumentId == "facet-exact");
        Assert.Contains("Path exact match", exactResult.Reasons);
        Assert.Contains("Symbol exact match", exactResult.Reasons);
        Assert.Single(exactResult.LaneRanks, static lane => lane.Lane == HistoryLexicalLaneKind.FacetExact);
        Assert.Equal(2, prefixResults.Count);
        Assert.All(prefixResults, static result => Assert.Contains("Path prefix match", result.Reasons));
        Assert.Equal(prefixResults.Count, prefixResults.Select(static result => result.DocumentId).Distinct().Count());
    }

    [Fact]
    public void PathSymbolActivityAndCanonicalUnicodeFacetsRemainSearchable()
    {
        using var scope = RegressionTestPackageScope.Create();
        var projection = new HistorySearchStore(scope.Context);
        var generation = projection.BeginTextGeneration();
        Insert(projection, generation, CreateDocument(
            "facets",
            "résumé body",
            [
                Facet("path", "src/Cafe\u0301/History.cs"),
                Facet("symbol", "HistorySearch.RunAsync"),
                Facet("activity", "Execute"),
            ]));
        projection.ActivateTextGeneration(generation);

        Assert.Single(projection.SearchLexical(generation, new HistorySearchRequest("src/Café/History.cs"), 20));
        Assert.Single(projection.SearchLexical(generation, new HistorySearchRequest("HistorySearch.RunAsync"), 20));
        var activity = Assert.Single(projection.SearchLexical(generation, new HistorySearchRequest("Execute"), 20));
        Assert.Contains(activity.Reasons, static reason => reason.StartsWith("Activity ", StringComparison.Ordinal));
        Assert.Single(projection.SearchLexical(generation, new HistorySearchRequest("re\u0301sume\u0301"), 20));
    }

    [Fact]
    public void RealFts_SearchesNfcCombiningAndSupplementaryCasePairsSymmetrically()
    {
        using var scope = RegressionTestPackageScope.Create();
        var projection = new HistorySearchStore(scope.Context);
        var generation = projection.BeginTextGeneration();
        var adlamUpper = "\U0001E900\U0001E901\U0001E902";
        var adlamLower = "\U0001E922\U0001E923\U0001E924";
        var reverseAdlamUpper = "\U0001E903\U0001E904\U0001E905";
        var reverseAdlamLower = "\U0001E925\U0001E926\U0001E927";
        var hangulDecomposed = "\u1100\u1161\u1102\u1161";
        var hangulComposed = "가나";
        var hebrew = "שָׁלוֹם";
        Insert(projection, generation, CreateDocument(
            "unicode",
            $"{adlamUpper} {hangulDecomposed} {hebrew.Normalize(NormalizationForm.FormD)}"));
        Insert(projection, generation, CreateDocument("unicode-reverse", reverseAdlamLower));
        projection.ActivateTextGeneration(generation);

        Assert.Equal("unicode", Assert.Single(projection.SearchLexical(
            generation,
            new HistorySearchRequest(adlamLower),
            20)).DocumentId);
        Assert.Equal("unicode-reverse", Assert.Single(projection.SearchLexical(
            generation,
            new HistorySearchRequest(reverseAdlamUpper),
            20)).DocumentId);
        Assert.Single(projection.SearchLexical(
            generation,
            new HistorySearchRequest(adlamLower[..^2]),
            20));
        Assert.Single(projection.SearchLexical(generation, new HistorySearchRequest(hangulComposed), 20));
        Assert.Single(projection.SearchLexical(
            generation,
            new HistorySearchRequest($"\"{adlamLower} {hangulComposed}\""),
            20));
        Assert.Single(projection.SearchLexical(
            generation,
            new HistorySearchRequest(hebrew.Normalize(NormalizationForm.FormC)),
            20));
    }

    [Fact]
    public void ProjectionWriteBoundary_RedactsAndNormalizesStoredFtsFacetAndSnippetText()
    {
        using var scope = RegressionTestPackageScope.Create();
        var projection = new HistorySearchStore(scope.Context);
        var generation = projection.BeginTextGeneration();
        var canaries = new[]
        {
            "body-quoted-canary",
            "body-colon-canary",
            "body-escaped-canary",
            "snippet-canary",
            "facet-canary",
            "workspace-canary",
            "session-canary",
        };
        var document = CreateDocument(
            "boundary",
            "safe-anchor Cafe\u0301\npassword = \"body-quoted-canary\"\nTOKEN: body-colon-canary\n\\\"api_key\\\":\\\"body-escaped-canary\\\"",
            [Facet("path", "src/Cafe\u0301/token=facet-canary")]) with
        {
            WorkspaceName = "password=workspace-canary",
            SessionTitle = "secret: session-canary",
            DisplaySnippet = "safe fallback password='snippet-canary'",
        };
        Insert(projection, generation, document);
        projection.ActivateTextGeneration(generation);

        var stored = Assert.Single(projection.LoadDocuments(generation, [document.DocumentId]));
        var bodySnippet = Assert.Single(projection.LoadBodySnippets(
            generation,
            HistoryFtsQueryPlan.Create("safe-anchor"),
            [document.DocumentId])).Value;
        using var connection = new SqliteConnection($"Data Source={projection.DatabasePath};Pooling=False");
        connection.Open();
        var persisted = ReadText(connection, """
            SELECT WorkspaceName || char(10) || SessionTitle || char(10) || BodyText || char(10) || DisplaySnippet
            FROM HistoryDocuments;
            """);
        var fts = ReadText(connection, """
            SELECT group_concat(BodyText || PathText || SymbolText || ActivityText || ToolText, char(10))
            FROM HistoryDocumentsFts;
            """);
        var facets = ReadText(connection, """
            SELECT group_concat(Value || NormalizedValue, char(10)) FROM HistoryDocumentFacets;
            """);
        var exposed = string.Join('\n',
            persisted,
            fts,
            facets,
            stored.WorkspaceName,
            stored.SessionTitle,
            stored.DisplaySnippet,
            string.Join(' ', stored.Facets.Select(static facet => facet.Value)),
            bodySnippet);

        Assert.All(canaries, canary => Assert.DoesNotContain(canary, exposed, StringComparison.Ordinal));
        Assert.All(canaries, canary => Assert.Empty(projection.SearchLexical(
            generation,
            new HistorySearchRequest(canary),
            20)));
        Assert.Contains("safe-anchor", bodySnippet, StringComparison.OrdinalIgnoreCase);
        Assert.True(persisted.IsNormalized(NormalizationForm.FormC));
        Assert.True(facets.IsNormalized(NormalizationForm.FormC));
    }

    [Fact]
    public void CumulativeLaneEvidenceBoostsDocumentsWithinTheSameTier()
    {
        using var scope = RegressionTestPackageScope.Create();
        var projection = new HistorySearchStore(scope.Context);
        var generation = projection.BeginTextGeneration();
        Insert(projection, generation, CreateDocument("all", "cumulative alpha evidence beta"));
        Insert(projection, generation, CreateDocument("prefix-only", "cumulative alpha evidence betamax"));
        projection.ActivateTextGeneration(generation);

        var results = projection.SearchLexical(generation, new HistorySearchRequest("alpha beta"), 20);

        Assert.Equal(1, results[0].Tier);
        Assert.Equal(1, results[1].Tier);
        Assert.Equal("all", results[0].DocumentId);
        Assert.True(results[0].Score > results[1].Score);
        Assert.True(results[0].LaneRanks.Count > results[1].LaneRanks.Count);
    }

    [Fact]
    public void MatchCenteredBodySnippetRedactsBoundsAndPreservesUnicodeScalars()
    {
        using var scope = RegressionTestPackageScope.Create();
        var projection = new HistorySearchStore(scope.Context);
        var generation = projection.BeginTextGeneration();
        var body = string.Join(' ', Enumerable.Repeat("opening", 180))
                   + " target api_key=super-secret "
                   + string.Concat(Enumerable.Repeat("😀", 900));
        var document = CreateDocument("snippet", body);
        Insert(projection, generation, document);
        projection.ActivateTextGeneration(generation);
        var plan = HistoryFtsQueryPlan.Create("target");

        var snippets = projection.LoadBodySnippets(generation, plan, [document.DocumentId]);
        var snippet = Assert.Single(snippets).Value;

        Assert.Contains("target", snippet, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("super-secret", snippet, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", snippet, StringComparison.Ordinal);
        Assert.InRange(snippet.Length, 1, HistorySearchLimits.MaximumSnippetCharacters);
        Assert.DoesNotContain('\ufffd', snippet);
        Assert.All(snippet.EnumerateRunes(), static rune => Assert.NotEqual(Rune.ReplacementChar, rune));
    }

    [Fact]
    public void MetadataOnlyMatchDoesNotInventBodySnippetAndStoredSnippetIsDefensivelySafe()
    {
        using var scope = RegressionTestPackageScope.Create();
        var projection = new HistorySearchStore(scope.Context);
        var generation = projection.BeginTextGeneration();
        var document = CreateDocument(
            "metadata",
            "unrelated api_key=stored-secret",
            [Facet("path", "src/Needle.cs")]);
        Insert(projection, generation, document);
        projection.ActivateTextGeneration(generation);
        var plan = HistoryFtsQueryPlan.Create("src/Needle.cs");

        var snippets = projection.LoadBodySnippets(generation, plan, [document.DocumentId]);

        Assert.Empty(snippets);
        Assert.DoesNotContain("stored-secret", HistorySearchText.SanitizeSnippet(document.DisplaySnippet), StringComparison.Ordinal);
    }

    [Fact]
    public void ParameterizedLaneExecutionIsSafeForFuzzedFtsSyntax()
    {
        using var scope = RegressionTestPackageScope.Create();
        var projection = new HistorySearchStore(scope.Context);
        var generation = projection.BeginTextGeneration();
        var literalBodies = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["safe"] = ["harmless", "searchable", "history"],
            ["alpha"] = ["alpha", "boundary", "canary"],
            ["beta"] = ["beta", "boundary", "canary"],
            ["gamma"] = ["gamma", "boundary", "canary"],
            ["supplementary-guard"] = ["\U0001E925\U0001E926\U0001E927"],
        };
        foreach (var (documentId, tokens) in literalBodies)
        {
            Insert(projection, generation, CreateDocument(documentId, string.Join(' ', tokens)));
        }
        projection.ActivateTextGeneration(generation);
        using var connection = new SqliteConnection($"Data Source={projection.DatabasePath};Pooling=False");
        connection.Open();
        var tableCount = Convert.ToInt64(new SqliteCommand(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';",
            connection).ExecuteScalar());
        var random = new Random(1927);
        const string alphabet = "abcXYZ09\"*():{}[]+-/^~_.' ";

        for (var iteration = 0; iteration < 250; iteration++)
        {
            var query = new string(Enumerable.Range(0, random.Next(1, 80))
                .Select(_ => alphabet[random.Next(alphabet.Length)])
                .ToArray());
            if (iteration % 17 == 0)
            {
                query += '\ud800';
            }

            var exception = Record.Exception(() => projection.SearchLexical(
                generation,
                new HistorySearchRequest(query),
                20));

            Assert.Null(exception);
            var plan = HistoryFtsQueryPlan.Create(query);
            var expected = literalBodies
                .Where(pair => IsLegitimateLiteralMatch(plan, pair.Value))
                .Select(static pair => pair.Key)
                .Order(StringComparer.Ordinal);
            var actual = projection.SearchLexical(generation, new HistorySearchRequest(query), 20)
                .Select(static result => result.DocumentId)
                .Order(StringComparer.Ordinal);
            Assert.Equal(expected, actual);
        }

        Assert.Equal(
            ["alpha", "beta"],
            projection.SearchLexical(generation, new HistorySearchRequest("alpha OR beta"), 20)
                .Select(static result => result.DocumentId)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            ["alpha", "gamma"],
            projection.SearchLexical(generation, new HistorySearchRequest("alpha NEAR gamma"), 20)
                .Select(static result => result.DocumentId)
                .Order(StringComparer.Ordinal));
        Assert.Empty(projection.SearchLexical(generation, new HistorySearchRequest("* OR *"), 20));
        Assert.Empty(projection.SearchLexical(generation, new HistorySearchRequest("\"alpha\" OR beta"), 20));
        Assert.Equal(tableCount, Convert.ToInt64(new SqliteCommand(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';",
            connection).ExecuteScalar()));
        Assert.Equal(literalBodies.Count, Convert.ToInt32(new SqliteCommand(
            "SELECT COUNT(*) FROM HistoryDocuments;",
            connection).ExecuteScalar()));
    }

    [Fact]
    public void CandidateCap_IsDeterministicAtTheFourHundredOfFourHundredOneBoundary()
    {
        using var scope = RegressionTestPackageScope.Create();
        var projection = new HistorySearchStore(scope.Context);
        var ids = Enumerable.Range(0, HistorySearchLimits.CandidateLimit + 1)
            .Select(static index => $"candidate-{index:D3}")
            .ToArray();
        var random = new Random(7291);

        var firstGeneration = projection.BeginTextGeneration();
        foreach (var id in ids.OrderBy(_ => random.Next()))
        {
            Insert(projection, firstGeneration, CreateDocument(id, "equal evidence"));
        }
        projection.ActivateTextGeneration(firstGeneration);
        var first = projection.SearchLexical(
            firstGeneration,
            new HistorySearchRequest("equal"),
            HistorySearchLimits.CandidateLimit);

        var secondGeneration = projection.BeginTextGeneration();
        foreach (var id in ids.Reverse())
        {
            Insert(projection, secondGeneration, CreateDocument(id, "equal evidence"));
        }
        projection.ActivateTextGeneration(secondGeneration);
        var second = projection.SearchLexical(
            secondGeneration,
            new HistorySearchRequest("equal"),
            HistorySearchLimits.CandidateLimit);
        var expected = ids.Take(HistorySearchLimits.CandidateLimit).ToArray();

        Assert.Equal(HistorySearchLimits.CandidateLimit, first.Count);
        Assert.Equal(expected, first.Select(static result => result.DocumentId));
        Assert.Equal(expected, second.Select(static result => result.DocumentId));
        Assert.DoesNotContain(ids[^1], first.Select(static result => result.DocumentId));
    }

    private static void Insert(
        HistorySearchStore projection,
        long generation,
        HistoryProjectionDocument document)
        => projection.ReplaceTurnDocuments(generation, document.SessionId, document.TurnId, [document]);

    private static HistoryProjectionDocument CreateDocument(
        string id,
        string body,
        IReadOnlyList<HistoryProjectionFacet>? facets = null)
    {
        var seed = id.Aggregate(17, static (value, character) => unchecked(value * 31 + character));
        var bytes = new byte[16];
        BitConverter.GetBytes(seed).CopyTo(bytes, 0);
        BitConverter.GetBytes(seed * 397).CopyTo(bytes, 8);
        var turnId = new Guid(bytes);
        bytes[15] ^= 0x5a;
        var itemId = new Guid(bytes);
        return new HistoryProjectionDocument(
            id,
            "workspace",
            "Workspace",
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
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
            new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero),
            body,
            body,
            facets ?? []);
    }

    private static HistoryProjectionFacet Facet(string kind, string value)
        => new(kind, value.Normalize(), HistorySearchText.NormalizeFacet(value));

    private static string ReadText(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    private static bool IsLegitimateLiteralMatch(
        HistoryFtsQueryPlan plan,
        IReadOnlyList<string> bodyTokens)
    {
        if (plan.IsRecentRequest)
        {
            return true;
        }
        if (!plan.HasTokens || plan.RequiredPhrases.Any(phrase => !ContainsPhrase(bodyTokens, phrase.Tokens)))
        {
            return false;
        }
        return plan.UnquotedTokens.Count == 0 || plan.UnquotedTokens.Any(queryToken =>
            bodyTokens.Any(bodyToken => queryToken.IsPrefix
                ? bodyToken.StartsWith(queryToken.Value, StringComparison.Ordinal)
                : string.Equals(bodyToken, queryToken.Value, StringComparison.Ordinal)));
    }

    private static bool ContainsPhrase(
        IReadOnlyList<string> bodyTokens,
        IReadOnlyList<string> phraseTokens)
    {
        for (var start = 0; start <= bodyTokens.Count - phraseTokens.Count; start++)
        {
            if (phraseTokens.Select((token, offset) => string.Equals(
                    token,
                    bodyTokens[start + offset],
                    StringComparison.Ordinal)).All(static matches => matches))
            {
                return true;
            }
        }
        return false;
    }
}
