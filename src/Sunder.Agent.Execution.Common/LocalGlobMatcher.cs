namespace Sunder.Agent.Execution.Common;

internal static class LocalGlobMatcher
{
    private const int MaxBraceExpansions = 256;
    private const int MaxMatchStates = 65_536;

    public static bool IsWithinExpansionBounds(string pattern)
    {
        var expansions = 1;
        var offset = 0;
        while (offset < pattern.Length)
        {
            var open = pattern.IndexOf('{', offset);
            var close = open < 0 ? -1 : pattern.IndexOf('}', open + 1);
            if (open < 0 || close < 0)
            {
                return true;
            }

            var optionCount = pattern[(open + 1)..close]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Length;
            if (optionCount == 0 || expansions > MaxBraceExpansions / optionCount)
            {
                return false;
            }
            expansions *= optionCount;
            offset = close + 1;
        }
        return true;
    }

    public static bool Matches(
        string pattern,
        string relativePath,
        CancellationToken cancellationToken = default)
        => Matches(
            pattern,
            relativePath,
            new LocalGlobMatchBudget(cancellationToken),
            posixPaths: !OperatingSystem.IsWindows());

    internal static bool Matches(
        string pattern,
        string relativePath,
        LocalGlobMatchBudget budget,
        bool posixPaths)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return true;
        }

        var normalizedPattern = NormalizeSeparators(pattern.Trim(), posixPaths);
        var normalizedPath = NormalizeSeparators(relativePath, posixPaths).TrimStart('/');
        foreach (var expanded in ExpandBraces(normalizedPattern, budget))
        {
            if (MatchesPattern(expanded, normalizedPath, budget))
            {
                return true;
            }
        }
        return false;
    }

    private static string NormalizeSeparators(string value, bool posixPaths)
        => !posixPaths && OperatingSystem.IsWindows()
            ? value.Replace('\\', '/')
            : value;

    private static bool MatchesPattern(string pattern, string path, LocalGlobMatchBudget budget)
    {
        pattern = pattern.StartsWith("./", StringComparison.Ordinal) ? pattern[2..] : pattern.TrimStart('/');
        if (!pattern.Contains('/', StringComparison.Ordinal))
        {
            var separator = path.LastIndexOf('/');
            var name = separator < 0 ? path : path[(separator + 1)..];
            return MatchesSegment(pattern, name, budget);
        }

        var patternSegments = CollapseRecursiveWildcards(
            pattern.Split('/', StringSplitOptions.RemoveEmptyEntries));
        var pathSegments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pending = new Stack<(int Pattern, int Path)>();
        var visited = new HashSet<(int Pattern, int Path)>();
        pending.Push((0, 0));
        while (pending.TryPop(out var state))
        {
            budget.Visit();
            if (!visited.Add(state))
            {
                continue;
            }
            if (state.Pattern == patternSegments.Count)
            {
                if (state.Path == pathSegments.Length)
                {
                    return true;
                }
                continue;
            }

            if (patternSegments[state.Pattern] == "**")
            {
                pending.Push((state.Pattern + 1, state.Path));
                if (state.Path < pathSegments.Length)
                {
                    pending.Push((state.Pattern, state.Path + 1));
                }
                continue;
            }
            if (state.Path < pathSegments.Length
                && MatchesSegment(patternSegments[state.Pattern], pathSegments[state.Path], budget))
            {
                pending.Push((state.Pattern + 1, state.Path + 1));
            }
        }
        return false;
    }

    private static bool MatchesSegment(string pattern, string value, LocalGlobMatchBudget budget)
    {
        var pending = new Stack<(int Pattern, int Value)>();
        var visited = new HashSet<(int Pattern, int Value)>();
        pending.Push((0, 0));
        while (pending.TryPop(out var state))
        {
            budget.Visit();
            if (!visited.Add(state))
            {
                continue;
            }
            if (state.Pattern == pattern.Length)
            {
                if (state.Value == value.Length)
                {
                    return true;
                }
                continue;
            }

            var token = pattern[state.Pattern];
            if (token == '*')
            {
                pending.Push((state.Pattern + 1, state.Value));
                if (state.Value < value.Length)
                {
                    pending.Push((state.Pattern, state.Value + 1));
                }
            }
            else if (state.Value < value.Length
                     && (token == '?' || token == value[state.Value]))
            {
                pending.Push((state.Pattern + 1, state.Value + 1));
            }
        }
        return false;
    }

    private static IReadOnlyList<string> CollapseRecursiveWildcards(IEnumerable<string> segments)
    {
        var collapsed = new List<string>();
        foreach (var segment in segments)
        {
            if (segment == "**" && collapsed.Count > 0 && collapsed[^1] == "**")
            {
                continue;
            }
            collapsed.Add(segment);
        }
        return collapsed;
    }

    private static IEnumerable<string> ExpandBraces(string pattern, LocalGlobMatchBudget budget)
    {
        var pending = new Stack<string>();
        pending.Push(pattern);
        var expansions = 0;
        while (pending.TryPop(out var candidate))
        {
            budget.Visit();
            var open = candidate.IndexOf('{', StringComparison.Ordinal);
            var close = open < 0 ? -1 : candidate.IndexOf('}', open + 1);
            if (open < 0 || close < 0)
            {
                if (++expansions > MaxBraceExpansions)
                {
                    throw new LocalSecurePathException(
                        $"A glob expression exceeds the {MaxBraceExpansions}-expansion limit.");
                }
                yield return candidate;
                continue;
            }

            var options = candidate[(open + 1)..close]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (options.Length == 0)
            {
                yield break;
            }
            for (var index = options.Length - 1; index >= 0; index--)
            {
                pending.Push(candidate[..open] + options[index] + candidate[(close + 1)..]);
            }
        }
    }

    internal sealed class LocalGlobMatchBudget(CancellationToken cancellationToken)
    {
        private int _states;

        public void Visit()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++_states > MaxMatchStates)
            {
                throw new LocalSecurePathException(
                    $"A glob search exceeds the aggregate {MaxMatchStates}-state evaluation limit.");
            }
        }
    }
}
