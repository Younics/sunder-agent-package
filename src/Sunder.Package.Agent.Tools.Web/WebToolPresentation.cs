using System.Text;

namespace Sunder.Package.Agent.Tools.Web;

internal static class WebToolPresentation
{
    public static string BuildRawRequestMarkdown(string argumentsJson, string? error = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("**Request**");
        if (!string.IsNullOrWhiteSpace(error))
        {
            builder.AppendLine(error.Trim());
            builder.AppendLine();
        }

        builder.AppendLine("```json");
        builder.AppendLine(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson.Trim());
        builder.AppendLine("```");
        return builder.ToString().Trim();
    }

    public static string BuildRequestMarkdown(params (string Label, string? Value)[] values)
    {
        var builder = new StringBuilder();
        builder.AppendLine("**Request**");
        foreach (var (label, value) in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            builder.Append("- ").Append(label).Append(": ").AppendLine(value.Trim());
        }

        return builder.ToString().Trim();
    }
}
