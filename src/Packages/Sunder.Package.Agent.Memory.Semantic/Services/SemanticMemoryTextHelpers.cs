using System.Text;
using System.Text.RegularExpressions;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Memory.Semantic.Services;

internal static class SemanticMemoryTextHelpers
{
    public static string RenderTurnText(AgentTurnRecord turn)
        => string.Join("\n\n", turn.Items
            .Where(item => item.Kind == AgentTurnItemKind.Text && !string.IsNullOrWhiteSpace(item.TextContent))
            .Select(item => item.TextContent!.Trim()));

    public static string RenderToolResultText(AgentTurnRecord turn)
        => string.Join("\n\n", turn.Items
            .Where(item => item.Kind == AgentTurnItemKind.ToolResult)
            .Select(item => !string.IsNullOrWhiteSpace(item.ResultSummary)
                ? item.ResultSummary!.Trim()
                : item.TextContent?.Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text))!);

    public static string BuildToolEvidenceText(AgentTurnItemRecord item)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(item.ResultSummary))
        {
            builder.AppendLine(item.ResultSummary.Trim());
        }

        if (!string.IsNullOrWhiteSpace(item.TextContent))
        {
            builder.AppendLine(item.TextContent.Trim());
        }

        return builder.ToString().Trim();
    }

    public static IReadOnlyList<string> EnumerateFactLines(string text)
        => text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(BuildFactContent)
            .Where(line => !string.IsNullOrWhiteSpace(line) && line.Length <= 220)
            .Take(8)
            .ToArray();

    public static string BuildFactContent(string text)
        => Regex.Replace(text.Trim().TrimStart('-', '*', '>', '#', '`'), "\\s+", " ").Trim();

    public static string? ChooseRicherText(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return string.IsNullOrWhiteSpace(right) ? null : right.Trim();
        }

        if (string.IsNullOrWhiteSpace(right))
        {
            return left.Trim();
        }

        return right.Trim().Length > left.Trim().Length ? right.Trim() : left.Trim();
    }

    public static IReadOnlySet<string> Tokenize(string text)
        => Normalize(text)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 2)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string text)
        => Regex.Replace(text.Trim().ToLowerInvariant(), "\\s+", " ");

    public static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..(maxLength - 3)].TrimEnd() + "...";

    public static AgentTurnRecord? GetLatestTurn(IReadOnlyList<AgentTurnRecord> turns, AgentTurnKind kind)
        => turns.Where(turn => turn.Kind == kind).OrderByDescending(turn => turn.CreatedAtUtc).FirstOrDefault();
}
