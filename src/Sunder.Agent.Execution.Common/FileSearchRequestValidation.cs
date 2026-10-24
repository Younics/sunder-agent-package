using System.Text.RegularExpressions;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Agent.Execution.Common;

// Pure request validation shared by tool preflight and target-owned execution.
internal static class FileSearchRequestValidation
{
    internal const int DefaultResults = 100;
    internal const int MaximumResults = 1000;
    internal const int MaxBraceExpansions = 256;
    private const int MaxExpressionCharacters = 4096;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    internal static bool IsDirectoryGlob(string pattern) => pattern.TrimEnd().EndsWith('/');

    internal static Regex CreateGrepRegex(string pattern, bool literal = false, bool caseSensitive = true)
        => new(literal ? Regex.Escape(pattern) : pattern,
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase), RegexTimeout);

    internal static bool TryValidateRequest(AgentFileSearchRequest request, out string? error)
    {
        if (string.IsNullOrWhiteSpace(request.Path)
            || string.IsNullOrEmpty(request.Pattern)
            || request.Pattern.Length > MaxExpressionCharacters
            || request.Include?.Length > MaxExpressionCharacters
            || !Enum.IsDefined(request.Kind)
            || request.Limit is < 1 or > MaximumResults
            || request.Kind == AgentFileSearchKind.Glob && (request.Literal || request.CaseSensitive == false || string.IsNullOrWhiteSpace(request.Pattern))
            || (request.Kind == AgentFileSearchKind.Glob
                && !IsWithinExpansionBounds(request.Pattern))
            || (request.Kind == AgentFileSearchKind.Grep
                && request.Include is not null
                && !IsWithinExpansionBounds(request.Include)))
        {
            error = "A structured file search requires a path, search kind, and bounded non-empty expression.";
            return false;
        }

        if (request.Kind == AgentFileSearchKind.Grep)
        {
            try
            {
                _ = CreateGrepRegex(request.Pattern, request.Literal, request.CaseSensitive != false);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                error = ex.Message;
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool IsWithinExpansionBounds(string pattern)
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
}
