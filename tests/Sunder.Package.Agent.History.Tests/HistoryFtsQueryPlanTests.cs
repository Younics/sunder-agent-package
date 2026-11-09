using Sunder.Package.Agent.HistorySearch;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class HistoryFtsQueryPlanTests
{
    [Fact]
    public void Whitespace_IsTheOnlyRecentHistoryQueryShape()
    {
        var empty = HistoryFtsQueryPlan.Create(" \t\r\n");
        var punctuation = HistoryFtsQueryPlan.Create("(()):*** ///");

        Assert.True(empty.IsRecentRequest);
        Assert.False(empty.HasTokens);
        Assert.Empty(empty.Lanes);
        Assert.False(punctuation.IsRecentRequest);
        Assert.False(punctuation.HasTokens);
        Assert.Empty(punctuation.Lanes);
    }

    [Fact]
    public void PathsAndSymbols_AreScalarTokenizedAndEmitOnlyTrustedGrammar()
    {
        var plan = HistoryFtsQueryPlan.Create("src/HistorySearch.cs Agent::Run");

        Assert.Equal(
            ["src", "historysearch", "cs", "agent", "run"],
            plan.UnquotedTokens.Select(static token => token.Value));
        Assert.Equal("src/historysearch.cs agent::run", plan.FacetQuery);
        Assert.Equal(
            [
                "ExactPhrase=\"src historysearch cs agent run\"",
                "AllExact=\"src\" AND \"historysearch\" AND \"cs\" AND \"agent\" AND \"run\"",
                "AllPrefix=\"src\" AND \"historysearch\" AND \"cs\" AND \"agent\" AND \"run\"*",
                "AnyExact=(\"src\" OR \"historysearch\" OR \"cs\" OR \"agent\" OR \"run\")",
                "AnyPrefix=(\"src\" OR \"historysearch\" OR \"cs\" OR \"agent\" OR \"run\"*)",
            ],
            Describe(plan));
    }

    [Fact]
    public void Unicode_IsNfcNormalizedAndSupplementaryLettersRemainWhole()
    {
        var plan = HistoryFtsQueryPlan.Create("Cafe\u0301 \U00010400");

        Assert.Equal(["café", "\U00010428"], plan.UnquotedTokens.Select(static token => token.Value));
        Assert.All(plan.UnquotedTokens, static token => Assert.False(token.IsPrefix));
        Assert.Equal(
            [
                "ExactPhrase=\"café \U00010428\"",
                "AllExact=\"café\" AND \"\U00010428\"",
                "AnyExact=(\"café\" OR \"\U00010428\")",
            ],
            Describe(plan));
    }

    [Fact]
    public void DuplicateTerms_AreDeduplicatedAndTheActualFinalTermGetsAutomaticPrefix()
    {
        var plan = HistoryFtsQueryPlan.Create("alpha, beta alpha");

        Assert.Equal(["alpha", "beta"], plan.UnquotedTokens.Select(static token => token.Value));
        Assert.True(plan.UnquotedTokens[0].IsPrefix);
        Assert.True(plan.UnquotedTokens[0].IsAutomaticPrefix);
        Assert.False(plan.UnquotedTokens[1].IsPrefix);
        Assert.Contains(
            "AllPrefix=\"alpha\"* AND \"beta\"",
            Describe(plan));
    }

    [Fact]
    public void TermsAndTokenLength_AreHardBoundedAtRuneBoundaries()
    {
        var longToken = string.Concat(Enumerable.Repeat("\U00010400", 80));
        var query = longToken + " " + string.Join(' ', Enumerable.Range(0, 40).Select(static index => $"word{index:D2}"));

        var plan = HistoryFtsQueryPlan.Create(query);

        Assert.Equal(HistoryFtsQueryPlan.MaximumTokens, plan.UnquotedTokens.Count);
        Assert.InRange(plan.FacetQuery.Length, 1, HistorySearchLimits.MaximumQueryCharacters);
        Assert.Equal(HistoryFtsQueryPlan.MaximumTokenRunes,
            plan.UnquotedTokens[0].Value.EnumerateRunes().Count());
        Assert.DoesNotContain(plan.UnquotedTokens, static token =>
            token.Value.Any(char.IsSurrogate) && !token.Value.EnumerateRunes().Any());
        Assert.Equal("word22", plan.UnquotedTokens[^1].Value);
        Assert.True(plan.UnquotedTokens[^1].IsAutomaticPrefix);
    }

    [Fact]
    public void ClosedQuotes_AreRequiredStrictPhrasesInEveryLane()
    {
        var plan = HistoryFtsQueryPlan.Create("\"alpha/beta\" gamma delta");

        Assert.Equal(["alpha", "beta"], Assert.Single(plan.RequiredPhrases).Tokens);
        Assert.Equal(
            [
                "ExactPhrase=\"alpha beta\" AND \"gamma delta\"",
                "AllExact=\"alpha beta\" AND \"gamma\" AND \"delta\"",
                "AllPrefix=\"alpha beta\" AND \"gamma\" AND \"delta\"*",
                "AnyExact=\"alpha beta\" AND (\"gamma\" OR \"delta\")",
                "AnyPrefix=\"alpha beta\" AND (\"gamma\" OR \"delta\"*)",
            ],
            Describe(plan));
        Assert.All(plan.Lanes, static lane => Assert.StartsWith("\"alpha beta\" AND ", lane.Match));
    }

    [Fact]
    public void FacetQuery_UsesOnlyUnquotedTextAndTracksPrefixEligibility()
    {
        var plan = HistoryFtsQueryPlan.Create("\"required phrase\" src/History* \"ignored metadata\"");

        Assert.Equal("src/history", plan.FacetQuery);
        Assert.True(plan.FacetPrefixEligible);
        Assert.Equal("\"required phrase\" AND \"ignored metadata\"", plan.RequiredPhraseMatch);
        Assert.DoesNotContain('*', plan.FacetQuery);

        var quotedOnly = HistoryFtsQueryPlan.Create("\"src/History.cs\"");
        Assert.Equal(string.Empty, quotedOnly.FacetQuery);
        Assert.False(quotedOnly.FacetPrefixEligible);
    }

    [Fact]
    public void FacetAutomaticPrefix_RequiresMoreThanOneScalarUnlessExplicit()
    {
        var oneScalar = HistoryFtsQueryPlan.Create("a");
        var explicitPrefix = HistoryFtsQueryPlan.Create("a*");
        var automaticPrefix = HistoryFtsQueryPlan.Create("ab");

        Assert.False(oneScalar.FacetPrefixEligible);
        Assert.True(explicitPrefix.FacetPrefixEligible);
        Assert.True(automaticPrefix.FacetPrefixEligible);
        Assert.Equal("a", explicitPrefix.FacetQuery);
    }

    [Fact]
    public void UnmatchedQuote_IsASeparatorRatherThanAnFtsOperator()
    {
        var plan = HistoryFtsQueryPlan.Create("\"alpha beta");

        Assert.Empty(plan.RequiredPhrases);
        Assert.Equal(["alpha", "beta"], plan.UnquotedTokens.Select(static token => token.Value));
        Assert.Equal("\"alpha beta\"", plan.Lanes[0].Match);
        Assert.Equal("\"alpha\" AND \"beta\"*", plan.Lanes[2].Match);
    }

    [Fact]
    public void ExplicitAndAutomaticPrefixes_AreLimitedToMarkedUnquotedTokens()
    {
        var plan = HistoryFtsQueryPlan.Create("a* beta gamma* delta e");

        Assert.Equal(
            [
                ("a", true, false),
                ("beta", false, false),
                ("gamma", true, false),
                ("delta", false, false),
                ("e", false, false),
            ],
            plan.UnquotedTokens.Select(static token =>
                (token.Value, token.IsPrefix, token.IsAutomaticPrefix)));
        Assert.Contains(
            "AllPrefix=\"a\"* AND \"beta\" AND \"gamma\"* AND \"delta\" AND \"e\"",
            Describe(plan));

        var automatic = HistoryFtsQueryPlan.Create("a beta");
        Assert.False(automatic.UnquotedTokens[0].IsPrefix);
        Assert.True(automatic.UnquotedTokens[1].IsAutomaticPrefix);
    }

    [Fact]
    public void MaliciousFtsSyntax_CannotSurviveTrustedGrammarEmission()
    {
        var plan = HistoryFtsQueryPlan.Create("NEAR(foo) OR body:* - {column} \"strict OR phrase\"");

        Assert.Equal(
            ["near", "foo", "or", "body", "column"],
            plan.UnquotedTokens.Select(static token => token.Value));
        Assert.Equal(["strict", "or", "phrase"], Assert.Single(plan.RequiredPhrases).Tokens);
        Assert.All(plan.Lanes, static lane =>
        {
            Assert.DoesNotContain("NEAR(", lane.Match, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("body:", lane.Match, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("{", lane.Match, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void InvalidUtf16_IsDiscardedWithoutSplittingNeighboringTokens()
    {
        var query = string.Concat("alpha", '\ud800', "beta", '\udc00', " gamma");

        var exception = Record.Exception(() => HistoryFtsQueryPlan.Create(query));
        var plan = HistoryFtsQueryPlan.Create(query);

        Assert.Null(exception);
        Assert.False(plan.IsRecentRequest);
        Assert.Equal(["alpha", "beta", "gamma"], plan.UnquotedTokens.Select(static token => token.Value));
        Assert.True(plan.UnquotedTokens[^1].IsAutomaticPrefix);
    }

    [Fact]
    public void CanonicalFingerprint_IsNfcCaseStableAndDelimiterSafe()
    {
        var first = HistorySearchService.CreateRequestFingerprint(new HistorySearchRequest(
            "CAFE\u0301",
            WorkspaceId: "workspace\nprofile",
            ProfileId: "value"));
        var equivalent = HistorySearchService.CreateRequestFingerprint(new HistorySearchRequest(
            "café",
            WorkspaceId: "workspace\nprofile",
            ProfileId: "value"));
        var delimiterVariant = HistorySearchService.CreateRequestFingerprint(new HistorySearchRequest(
            "café",
            WorkspaceId: "workspace",
            ProfileId: "profile\nvalue"));

        Assert.Equal(first, equivalent);
        Assert.NotEqual(first, delimiterVariant);
        Assert.Equal(64, first.Length);
    }

    private static IReadOnlyList<string> Describe(HistoryFtsQueryPlan plan)
        => plan.Lanes.Select(static lane => $"{lane.Kind}={lane.Match}").ToArray();
}
