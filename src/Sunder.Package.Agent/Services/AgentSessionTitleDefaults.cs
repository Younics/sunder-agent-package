using System.Text.RegularExpressions;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services;

public static partial class AgentSessionTitleDefaults
{
    public static string CreateNextTitle(IEnumerable<AgentSessionRecord> sessions)
    {
        var maxSessionNumber = sessions
            .Where(session => session.ParentSessionId is null)
            .Select(session => TryParseSessionNumber(session.Title))
            .Where(number => number is > 0)
            .DefaultIfEmpty(0)
            .Max();

        return $"Session {maxSessionNumber + 1}";
    }

    public static bool IsGeneratedDefaultTitle(string? title) =>
        !string.IsNullOrWhiteSpace(title) && ExactGeneratedTitlePattern().IsMatch(title.Trim());

    private static int? TryParseSessionNumber(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var match = TrailingSessionNumberPattern().Match(title.Trim());
        return match.Success && int.TryParse(match.Groups[1].Value, out var number)
            ? number
            : null;
    }

    [GeneratedRegex(@"^Session\s+(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExactGeneratedTitlePattern();

    [GeneratedRegex(@"(?:^|\s)Session\s+(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingSessionNumberPattern();
}
