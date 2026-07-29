using System.Globalization;
using System.Text;

namespace Sunder.Package.Agent.HistorySearch;

internal sealed class HistoryFtsQueryPlan
{
    internal const int MaximumTokens = 24;
    internal const int MaximumQuotedPhrases = 8;
    internal const int MaximumTokenRunes = 64;

    private HistoryFtsQueryPlan(
        bool isRecentRequest,
        string facetQuery,
        bool facetPrefixEligible,
        string? requiredPhraseMatch,
        IReadOnlyList<HistoryFtsToken> unquotedTokens,
        IReadOnlyList<HistoryFtsPhrase> requiredPhrases,
        IReadOnlyList<HistoryFtsLane> lanes)
    {
        IsRecentRequest = isRecentRequest;
        FacetQuery = facetQuery;
        FacetPrefixEligible = facetPrefixEligible;
        RequiredPhraseMatch = requiredPhraseMatch;
        UnquotedTokens = unquotedTokens;
        RequiredPhrases = requiredPhrases;
        Lanes = lanes;
        BroadBodyMatch = lanes.Count == 0
            ? null
            : $"BodyText : ({lanes[^1].Match})";
    }

    internal bool IsRecentRequest { get; }
    internal string FacetQuery { get; }
    internal bool FacetPrefixEligible { get; }
    internal string? RequiredPhraseMatch { get; }
    internal IReadOnlyList<HistoryFtsToken> UnquotedTokens { get; }
    internal IReadOnlyList<HistoryFtsPhrase> RequiredPhrases { get; }
    internal IReadOnlyList<HistoryFtsLane> Lanes { get; }
    internal string? BroadBodyMatch { get; }
    internal bool HasTokens => UnquotedTokens.Count > 0 || RequiredPhrases.Count > 0;

    internal static HistoryFtsQueryPlan Create(string? query)
    {
        var valid = HistorySearchText.BoundAtRuneBoundary(
                HistorySearchText.ToValidScalars(query, out var hadNonWhitespaceInput),
                HistorySearchLimits.MaximumQueryCharacters)
            .Normalize(NormalizationForm.FormC);
        valid = HistorySearchText.BoundAtRuneBoundary(valid, HistorySearchLimits.MaximumQueryCharacters);
        if (!hadNonWhitespaceInput)
        {
            return new HistoryFtsQueryPlan(true, string.Empty, false, null, [], [], []);
        }

        var parser = new Parser(valid);
        parser.Parse();
        var tokens = parser.UnquotedTokens.ToArray();
        if (parser.LastUnquotedTokenIndex is { } finalIndex)
        {
            var final = tokens[finalIndex];
            if (!final.IsPrefix && HistorySearchText.CountRunes(final.Value) > 1)
            {
                tokens[finalIndex] = final with { IsPrefix = true, IsAutomaticPrefix = true };
            }
        }

        var phrases = parser.RequiredPhrases.ToArray();
        var lanes = BuildLanes(tokens, phrases);
        var requiredPhraseMatch = phrases.Length == 0
            ? null
            : string.Join(" AND ", phrases.Select(static phrase => QuotePhrase(phrase.Tokens)));
        return new HistoryFtsQueryPlan(
            false,
            HistorySearchText.BoundAtRuneBoundary(
                HistorySearchText.NormalizeFacet(parser.FacetText),
                HistorySearchLimits.MaximumQueryCharacters),
            parser.LastUnquotedTokenIndex is { } lastIndex && tokens[lastIndex].IsPrefix,
            requiredPhraseMatch,
            tokens,
            phrases,
            lanes);
    }

    private static IReadOnlyList<HistoryFtsLane> BuildLanes(
        IReadOnlyList<HistoryFtsToken> tokens,
        IReadOnlyList<HistoryFtsPhrase> phrases)
    {
        var required = phrases.Select(static phrase => QuotePhrase(phrase.Tokens)).ToArray();
        var lanes = new List<HistoryFtsLane>();
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        if (tokens.Count == 0)
        {
            if (required.Length > 0)
            {
                AddLane(
                    lanes,
                    emitted,
                    HistoryLexicalLaneKind.ExactPhrase,
                    string.Join(" AND ", required));
            }
            return lanes;
        }

        if (tokens.Count > 1)
        {
            AddLane(
                lanes,
                emitted,
                HistoryLexicalLaneKind.ExactPhrase,
                Conjoin(required, QuotePhrase(tokens.Select(static token => token.Value))));
        }

        var exactTerms = tokens.Select(static token => QuoteToken(token.Value)).ToArray();
        AddLane(
            lanes,
            emitted,
            HistoryLexicalLaneKind.AllExact,
            Conjoin(required, string.Join(" AND ", exactTerms)));

        var prefixTerms = tokens.Select(static token =>
            QuoteToken(token.Value) + (token.IsPrefix ? "*" : string.Empty)).ToArray();
        AddLane(
            lanes,
            emitted,
            HistoryLexicalLaneKind.AllPrefix,
            Conjoin(required, string.Join(" AND ", prefixTerms)));

        if (tokens.Count > 1)
        {
            AddLane(
                lanes,
                emitted,
                HistoryLexicalLaneKind.AnyExact,
                Conjoin(required, $"({string.Join(" OR ", exactTerms)})"));
            AddLane(
                lanes,
                emitted,
                HistoryLexicalLaneKind.AnyPrefix,
                Conjoin(required, $"({string.Join(" OR ", prefixTerms)})"));
        }
        return lanes;
    }

    private static void AddLane(
        ICollection<HistoryFtsLane> lanes,
        ISet<string> emitted,
        HistoryLexicalLaneKind kind,
        string match)
    {
        if (emitted.Add(match))
        {
            lanes.Add(new HistoryFtsLane(kind, match));
        }
    }

    private static string Conjoin(IReadOnlyList<string> required, string expression)
        => required.Count == 0
            ? expression
            : string.Join(" AND ", required.Append(expression));

    private static string QuotePhrase(IEnumerable<string> tokens)
        => $"\"{string.Join(' ', tokens)}\"";

    private static string QuoteToken(string token) => $"\"{token}\"";

    private sealed class Parser(string value)
    {
        private readonly Dictionary<string, int> _unquotedIndexes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _phraseKeys = new(StringComparer.Ordinal);
        private readonly StringBuilder _facetText = new();
        private int _emittedTokenCount;

        internal List<HistoryFtsToken> UnquotedTokens { get; } = [];
        internal List<HistoryFtsPhrase> RequiredPhrases { get; } = [];
        internal int? LastUnquotedTokenIndex { get; private set; }
        internal string FacetText => _facetText.ToString();

        internal void Parse()
        {
            var cursor = 0;
            while (cursor < value.Length && _emittedTokenCount < MaximumTokens)
            {
                var opening = value.IndexOf('"', cursor);
                if (opening < 0)
                {
                    ParseUnquoted(value.AsSpan(cursor));
                    break;
                }

                ParseUnquoted(value.AsSpan(cursor, opening - cursor));
                var closing = value.IndexOf('"', opening + 1);
                if (closing < 0)
                {
                    ParseUnquoted(value.AsSpan(opening + 1));
                    break;
                }

                ParsePhrase(value.AsSpan(opening + 1, closing - opening - 1));
                cursor = closing + 1;
            }
        }

        private void ParseUnquoted(ReadOnlySpan<char> segment)
        {
            AppendFacetText(segment);
            var token = new StringBuilder();
            var runeCount = 0;
            var tokenWasTruncated = false;
            var remaining = segment;
            while (!remaining.IsEmpty && _emittedTokenCount < MaximumTokens)
            {
                Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);
                if (IsTokenRune(rune))
                {
                    if (runeCount < MaximumTokenRunes)
                    {
                        token.Append(rune.ToString());
                        runeCount++;
                    }
                    else
                    {
                        tokenWasTruncated = true;
                    }
                }
                else
                {
                    var explicitPrefix = rune.Value == '*' && token.Length > 0;
                    EmitUnquoted(token, explicitPrefix);
                    token.Clear();
                    runeCount = 0;
                    tokenWasTruncated = false;
                }
                remaining = remaining[consumed..];
            }
            if (!tokenWasTruncated || token.Length > 0)
            {
                EmitUnquoted(token, explicitPrefix: false);
            }
        }

        private void EmitUnquoted(StringBuilder builder, bool explicitPrefix)
        {
            if (builder.Length == 0)
            {
                return;
            }
            var token = HistorySearchText.NormalizeForSearch(builder.ToString());
            if (_unquotedIndexes.TryGetValue(token, out var index))
            {
                LastUnquotedTokenIndex = index;
                if (explicitPrefix && !UnquotedTokens[index].IsPrefix)
                {
                    UnquotedTokens[index] = UnquotedTokens[index] with { IsPrefix = true };
                }
                return;
            }
            _unquotedIndexes[token] = UnquotedTokens.Count;
            LastUnquotedTokenIndex = UnquotedTokens.Count;
            UnquotedTokens.Add(new HistoryFtsToken(token, explicitPrefix, IsAutomaticPrefix: false));
            _emittedTokenCount++;
        }

        private void ParsePhrase(ReadOnlySpan<char> segment)
        {
            if (RequiredPhrases.Count >= MaximumQuotedPhrases)
            {
                return;
            }
            var tokens = TokenizePhrase(segment, MaximumTokens - _emittedTokenCount);
            if (tokens.Count == 0)
            {
                return;
            }
            var key = string.Join('\u001f', tokens);
            if (!_phraseKeys.Add(key))
            {
                return;
            }
            RequiredPhrases.Add(new HistoryFtsPhrase(tokens));
            _emittedTokenCount += tokens.Count;
        }

        private static IReadOnlyList<string> TokenizePhrase(ReadOnlySpan<char> segment, int remainingBudget)
        {
            var values = new List<string>();
            var token = new StringBuilder();
            var runeCount = 0;
            var remaining = segment;
            while (!remaining.IsEmpty && values.Count < remainingBudget)
            {
                Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);
                if (IsTokenRune(rune))
                {
                    if (runeCount < MaximumTokenRunes)
                    {
                        token.Append(rune.ToString());
                        runeCount++;
                    }
                }
                else
                {
                    EmitPhraseToken(values, token);
                    token.Clear();
                    runeCount = 0;
                }
                remaining = remaining[consumed..];
            }
            if (values.Count < remainingBudget)
            {
                EmitPhraseToken(values, token);
            }
            return values;
        }

        private static void EmitPhraseToken(ICollection<string> values, StringBuilder token)
        {
            if (token.Length > 0)
            {
                values.Add(HistorySearchText.NormalizeForSearch(token.ToString()));
            }
        }

        private void AppendFacetText(ReadOnlySpan<char> segment)
        {
            var value = segment.ToString().Replace("*", string.Empty, StringComparison.Ordinal).Trim();
            if (value.Length == 0)
            {
                return;
            }
            if (_facetText.Length > 0)
            {
                _facetText.Append(' ');
            }
            _facetText.Append(value);
        }

        private static bool IsTokenRune(Rune rune)
            => Rune.GetUnicodeCategory(rune) is
                UnicodeCategory.UppercaseLetter
                or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter
                or UnicodeCategory.OtherLetter
                or UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark
                or UnicodeCategory.DecimalDigitNumber
                or UnicodeCategory.LetterNumber
                or UnicodeCategory.OtherNumber;
    }
}

internal sealed record HistoryFtsToken(
    string Value,
    bool IsPrefix,
    bool IsAutomaticPrefix);

internal sealed record HistoryFtsPhrase(IReadOnlyList<string> Tokens);

internal sealed record HistoryFtsLane(HistoryLexicalLaneKind Kind, string Match);
