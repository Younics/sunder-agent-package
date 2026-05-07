using System.Text;

namespace Sunder.Package.Agent.Skills.Services;

internal static class SkillMarkdownParser
{
    public static ParsedSkillMarkdown Parse(string rawContent)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? name = null;
        string? description = null;
        string? version = null;
        string? author = null;
        var body = rawContent;

        var frontmatter = ExtractFrontmatter(rawContent, out var extractedBody);
        if (frontmatter is null)
        {
            return new ParsedSkillMarkdown(null, null, null, null, body, rawContent, metadata);
        }

        body = extractedBody.TrimStart();
        foreach (var pair in ParseYamlishFrontmatter(frontmatter))
        {
            var value = NormalizeScalar(pair.Value);
            switch (pair.Key.ToLowerInvariant())
            {
                case "name":
                    name = value;
                    break;
                case "description":
                    description = value;
                    break;
                case "version":
                    version = value;
                    break;
                case "author":
                    author = value;
                    break;
                default:
                    metadata[pair.Key] = pair.Value.Trim();
                    break;
            }
        }

        return new ParsedSkillMarkdown(name, description, version, author, body, rawContent, metadata);
    }

    private static string? ExtractFrontmatter(string rawContent, out string body)
    {
        body = rawContent;
        if (!rawContent.StartsWith("---", StringComparison.Ordinal))
        {
            return null;
        }

        using var reader = new StringReader(rawContent);
        var firstLine = reader.ReadLine();
        if (!string.Equals(firstLine?.Trim(), "---", StringComparison.Ordinal))
        {
            return null;
        }

        var frontmatter = new StringBuilder();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.Equals(line.Trim(), "---", StringComparison.Ordinal))
            {
                body = reader.ReadToEnd();
                return frontmatter.ToString();
            }

            frontmatter.AppendLine(line);
        }

        body = rawContent;
        return null;
    }

    private static IReadOnlyDictionary<string, string> ParseYamlishFrontmatter(string frontmatter)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentKey = null;
        var currentValue = new StringBuilder();

        foreach (var rawLine in frontmatter.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                AppendContinuation(currentValue, rawLine);
                continue;
            }

            var trimmedStart = rawLine.TrimStart();
            var isIndented = rawLine.Length != trimmedStart.Length;
            var colonIndex = trimmedStart.IndexOf(':', StringComparison.Ordinal);
            if (!isIndented && colonIndex > 0)
            {
                FlushCurrent();
                currentKey = trimmedStart[..colonIndex].Trim();
                currentValue.Append(trimmedStart[(colonIndex + 1)..].Trim());
                continue;
            }

            AppendContinuation(currentValue, rawLine);
        }

        FlushCurrent();
        return pairs;

        void FlushCurrent()
        {
            if (!string.IsNullOrWhiteSpace(currentKey))
            {
                pairs[currentKey] = currentValue.ToString().Trim();
            }

            currentKey = null;
            currentValue.Clear();
        }
    }

    private static void AppendContinuation(StringBuilder builder, string value)
    {
        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.Append(value.TrimEnd());
    }

    private static string? NormalizeScalar(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)
            || string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "~", StringComparison.Ordinal))
        {
            return null;
        }

        if ((trimmed.StartsWith('"') && trimmed.EndsWith('"'))
            || (trimmed.StartsWith('\'') && trimmed.EndsWith('\'')))
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }
}
