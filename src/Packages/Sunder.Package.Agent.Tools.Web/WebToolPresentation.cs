using System.Text;
using System.Text.Json;

namespace Sunder.Package.Agent.Tools.Web;

internal static class WebToolPresentation
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static T? TryDeserialize<T>(string argumentsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(argumentsJson, JsonOptions);
        }
        catch
        {
            return default;
        }
    }

    public static string BuildUnavailableRequestMarkdown()
        => "**Request**\nRequest details are unavailable.";

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
