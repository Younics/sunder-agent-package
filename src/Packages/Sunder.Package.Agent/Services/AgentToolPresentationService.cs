using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Services;

public sealed class AgentToolPresentationService(
    InstalledPackageToolSource? installedPackageToolSource = null,
    IPackageExtensionCatalog? extensionCatalog = null)
{
    private const int MaxHeaderChars = 160;
    private const int MaxHeaderLines = 2;

    private static readonly string[] PreferredHeaderFields =
    [
        "command",
        "script",
        "url",
        "query",
        "path",
        "filePath",
        "pattern",
    ];

    public AgentToolPresentation Resolve(AgentTurnItemRecord item)
    {
        var request = new AgentToolPresentationRequest(
            item.ToolId ?? string.Empty,
            item.ArgumentsJson ?? "{}",
            item.ResultSummary,
            item.TextContent,
            item.StructuredPayloadJson,
            item.SourcesJson,
            item.IsError,
            item.ErrorCode,
            item.BackendId);
        var fallback = BuildFallbackPresentation(request);

        foreach (var resolver in ListResolvers())
        {
            AgentToolPresentation? resolved;
            try
            {
                resolved = resolver.ResolveToolPresentation(request);
            }
            catch
            {
                continue;
            }

            if (resolved is not null)
            {
                return NormalizePresentation(MergeResolved(resolved, fallback));
            }
        }

        return NormalizePresentation(fallback);
    }

    private IReadOnlyList<IAgentToolPresentationResolver> ListResolvers()
    {
        var resolvers = new List<IAgentToolPresentationResolver>();
        if (installedPackageToolSource is not null)
        {
            resolvers.Add(installedPackageToolSource);
        }

        if (extensionCatalog is not null)
        {
            resolvers.AddRange(extensionCatalog.GetExtensions(PackageExtensionPoints.ToolSources)
                .OfType<IAgentToolPresentationResolver>());
        }

        return resolvers;
    }

    private static AgentToolPresentation MergeResolved(AgentToolPresentation resolved, AgentToolPresentation fallback)
        => new(
            string.IsNullOrWhiteSpace(resolved.HeaderText) ? fallback.HeaderText : resolved.HeaderText,
            resolved.DetailMarkdown,
            resolved.OutputText ?? fallback.OutputText);

    private static AgentToolPresentation NormalizePresentation(AgentToolPresentation presentation)
        => presentation with
        {
            HeaderText = NormalizeHeader(presentation.HeaderText),
        };

    private static string? NormalizeHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var lines = header.Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var text = string.Join(" ", lines.Take(MaxHeaderLines));
        if (lines.Length > MaxHeaderLines)
        {
            text += "...";
        }

        var normalized = text.Length <= MaxHeaderChars
            ? text
            : text[..MaxHeaderChars].TrimEnd() + "...";
        return normalized.Length > 1 && normalized.EndsWith(".", StringComparison.Ordinal) && !normalized.EndsWith("...", StringComparison.Ordinal)
            ? normalized[..^1]
            : normalized;
    }

    private static AgentToolPresentation BuildFallbackPresentation(AgentToolPresentationRequest request)
        => new(
            HeaderText: BuildHeaderPreview(request.ArgumentsJson),
            DetailMarkdown: BuildFallbackDetailMarkdown(request),
            OutputText: request.TextContent?.Trim());

    private static string? BuildHeaderPreview(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return CompactValue("arguments", document.RootElement);
            }

            foreach (var field in PreferredHeaderFields)
            {
                if (document.RootElement.TryGetProperty(field, out var property))
                {
                    return CompactValue(field, property);
                }
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    return CompactValue(property.Name, property.Value);
                }
            }

            var count = document.RootElement.EnumerateObject().Count();
            return count == 0 ? null : FormatCount(count, "argument");
        }
        catch
        {
            return null;
        }
    }

    private static string? CompactValue(string name, JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => CompactString(name, value.GetString()),
            JsonValueKind.Array => $"{name}: {FormatCount(value.GetArrayLength(), "item")}",
            JsonValueKind.Object => $"{name}: {FormatCount(value.EnumerateObject().Count(), "field")}",
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => null,
        };
    }

    private static string? CompactString(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Replace("\r\n", "\n").Trim();
        var lines = normalized.Split('\n');
        if (lines.Length > MaxHeaderLines)
        {
            return $"{name}: {FormatCount(lines.Length, "line")}";
        }

        return normalized.Length > MaxHeaderChars
            ? $"{name}: {FormatCount(normalized.Length, "char")}"
            : normalized;
    }

    private static string BuildFallbackDetailMarkdown(AgentToolPresentationRequest request)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(request.ArgumentsJson) && !string.Equals(request.ArgumentsJson.Trim(), "{}", StringComparison.Ordinal))
        {
            builder.AppendLine("**Arguments**");
            builder.AppendLine("```json");
            builder.AppendLine(request.ArgumentsJson.Trim());
            builder.AppendLine("```");
        }

        if (!string.IsNullOrWhiteSpace(request.StructuredPayloadJson))
        {
            AppendSectionSeparator(builder);
            builder.AppendLine("**Structured Payload**");
            builder.AppendLine("```json");
            builder.AppendLine(request.StructuredPayloadJson.Trim());
            builder.AppendLine("```");
        }

        if (!string.IsNullOrWhiteSpace(request.SourcesJson))
        {
            var sourceLines = BuildSourceLines(request.SourcesJson);
            if (sourceLines.Count > 0)
            {
                AppendSectionSeparator(builder);
                builder.AppendLine("**Sources**");
                foreach (var line in sourceLines)
                {
                    builder.AppendLine(line);
                }
            }
        }

        return builder.ToString().Trim();
    }

    private static void AppendSectionSeparator(StringBuilder builder)
    {
        if (builder.Length > 0)
        {
            builder.AppendLine();
        }
    }

    private static IReadOnlyList<string> BuildSourceLines(string sourcesJson)
    {
        try
        {
            var sources = JsonSerializer.Deserialize<IReadOnlyList<AgentToolSourceItem>>(sourcesJson);
            return sources is null
                ? []
                : sources.Select(source => $"- [{source.Title}]({source.Url})").ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static string FormatCount(int count, string noun)
        => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
