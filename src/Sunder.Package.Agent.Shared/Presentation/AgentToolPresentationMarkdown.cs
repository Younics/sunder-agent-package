using System.Text;

namespace Sunder.Package.Agent.Shared.Presentation;

internal static class AgentToolPresentationMarkdown
{
    public static string BuildRawRequestMarkdown(string? argumentsJson, string? error = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("**Request**");
        if (!string.IsNullOrWhiteSpace(error))
        {
            builder.AppendLine(error.Trim());
            builder.AppendLine();
        }

        builder.AppendLine(BuildFencedBlock("json", string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson.Trim()));
        return builder.ToString().Trim();
    }

    public static string BuildRequestMarkdown(params (string Label, string? Value)[] values)
    {
        var builder = new StringBuilder();
        builder.AppendLine("**Request**");
        foreach (var (label, value) in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                builder.Append("- ").Append(label).Append(": ").AppendLine(value.Trim());
            }
        }

        return builder.ToString().Trim();
    }

    public static string BuildFencedMarkdown(string title, string language, string content)
        => $"**{title}**{Environment.NewLine}{BuildFencedBlock(language, content.Trim())}";

    public static string BuildFailureMarkdown(string title, string message)
        => $"### {title}{Environment.NewLine}{Environment.NewLine}{message.Trim()}";

    private static string BuildFencedBlock(string language, string content)
    {
        var fence = new string('`', Math.Max(3, LongestBacktickRun(content) + 1));
        return $"{fence}{language}{Environment.NewLine}{content}{Environment.NewLine}{fence}";
    }

    private static int LongestBacktickRun(string content)
    {
        var longest = 0;
        var current = 0;
        foreach (var character in content)
        {
            if (character == '`')
            {
                longest = Math.Max(longest, ++current);
            }
            else
            {
                current = 0;
            }
        }

        return longest;
    }
}
