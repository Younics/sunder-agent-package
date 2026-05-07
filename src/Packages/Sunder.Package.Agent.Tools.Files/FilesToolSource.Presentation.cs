using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tools.Files;

public sealed partial class FilesToolSource
{
    private static AgentToolPresentation ResolveReadPresentation(AgentToolPresentationRequest request)
    {
        var args = TryDeserializeArguments<ReadArgs>(request.ArgumentsJson);
        return new AgentToolPresentation(
            HeaderText: request.ResultSummary ?? args?.Path,
            DetailMarkdown: args is null
                ? BuildUnavailableRequestMarkdown()
                : BuildRequestMarkdown(
                    ("Path", args.Path),
                    ("Offset", args.Offset?.ToString()),
                    ("Limit", args.Limit?.ToString())),
            OutputText: request.TextContent);
    }

    private static AgentToolPresentation ResolveWritePresentation(AgentToolPresentationRequest request)
    {
        var args = TryDeserializeArguments<WriteArgs>(request.ArgumentsJson);
        return new AgentToolPresentation(
            HeaderText: request.ResultSummary ?? args?.Path,
            DetailMarkdown: args is null
                ? BuildUnavailableRequestMarkdown()
                : BuildRequestMarkdown(
                    ("Path", args.Path),
                    ("Content", FormatTextStats(args.Content))),
            OutputText: request.TextContent);
    }

    private static AgentToolPresentation ResolveEditPresentation(AgentToolPresentationRequest request)
    {
        var args = TryDeserializeArguments<EditArgs>(request.ArgumentsJson);
        return new AgentToolPresentation(
            HeaderText: request.ResultSummary ?? args?.Path,
            DetailMarkdown: args is null
                ? BuildUnavailableRequestMarkdown()
                : BuildRequestMarkdown(
                    ("Path", args.Path),
                    ("Old text", FormatTextStats(args.OldString)),
                    ("New text", FormatTextStats(args.NewString)),
                    ("Replace all", args.ReplaceAll ? "yes" : "no")),
            OutputText: request.TextContent);
    }

    private static AgentToolPresentation ResolveGlobPresentation(AgentToolPresentationRequest request)
    {
        var args = TryDeserializeArguments<GlobArgs>(request.ArgumentsJson);
        var pattern = args?.Pattern;
        var path = args?.Path;
        var header = string.IsNullOrWhiteSpace(path)
            ? pattern
            : string.IsNullOrWhiteSpace(pattern)
                ? path
                : $"{pattern} in {path}";
        return new AgentToolPresentation(
            HeaderText: request.ResultSummary ?? header,
            DetailMarkdown: args is null
                ? BuildUnavailableRequestMarkdown()
                : BuildRequestMarkdown(
                    ("Pattern", args.Pattern),
                    ("Path", string.IsNullOrWhiteSpace(args.Path) ? "." : args.Path)),
            OutputText: request.TextContent);
    }

    private static AgentToolPresentation ResolveGrepPresentation(AgentToolPresentationRequest request)
    {
        var args = TryDeserializeArguments<GrepArgs>(request.ArgumentsJson);
        var pattern = args?.Pattern;
        var include = args?.Include;
        var path = args?.Path;
        var qualifiers = new List<string>();
        if (!string.IsNullOrWhiteSpace(include))
        {
            qualifiers.Add(include);
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            qualifiers.Add(path);
        }

        var header = qualifiers.Count == 0
            ? pattern
            : $"{pattern} in {string.Join(" / ", qualifiers)}";
        return new AgentToolPresentation(
            HeaderText: request.ResultSummary ?? header,
            DetailMarkdown: args is null
                ? BuildUnavailableRequestMarkdown()
                : BuildRequestMarkdown(
                    ("Pattern", args.Pattern),
                    ("Path", string.IsNullOrWhiteSpace(args.Path) ? "." : args.Path),
                    ("Include", args.Include)),
            OutputText: request.TextContent);
    }

    private static AgentToolPresentation ResolveApplyPatchPresentation(AgentToolPresentationRequest request)
    {
        var patchText = TryExtractString(request.ArgumentsJson, "patchText");
        var header = request.ResultSummary;
        if (string.IsNullOrWhiteSpace(header) && !string.IsNullOrWhiteSpace(patchText))
        {
            try
            {
                header = BuildPatchSummary(ParsePatch(patchText));
            }
            catch
            {
                header = "Patch workspace files";
            }
        }

        var detail = string.IsNullOrWhiteSpace(patchText)
            ? BuildUnavailableRequestMarkdown()
            : BuildFencedMarkdown("Patch", "diff", patchText);
        return new AgentToolPresentation(
            HeaderText: header,
            DetailMarkdown: detail,
            OutputText: request.TextContent);
    }

    private static string BuildPatchSummary(IReadOnlyList<PatchOperation> operations)
    {
        var fileCount = operations
            .Select(operation => operation.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return $"Applied {FormatCount(operations.Count, "patch operation")} to {FormatCount(fileCount, "file")}";
    }

    private static T? TryDeserializeArguments<T>(string argumentsJson)
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

    private static string BuildUnavailableRequestMarkdown()
        => "**Request**\nRequest details are unavailable.";

    private static string BuildRequestMarkdown(params (string Label, string? Value)[] values)
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

    private static string FormatTextStats(string text)
    {
        var lineCount = string.IsNullOrEmpty(text)
            ? 0
            : text.Replace("\r\n", "\n").Split('\n').Length;
        return $"{FormatCount(text.Length, "char")}, {FormatCount(lineCount, "line")}";
    }

    private static string BuildFencedMarkdown(string title, string language, string content)
    {
        var builder = new StringBuilder();
        builder.Append("**").Append(title).AppendLine("**");
        builder.Append("```").AppendLine(language);
        builder.AppendLine(content.Trim());
        builder.AppendLine("```");
        return builder.ToString().Trim();
    }

    private static string? TryExtractString(string argumentsJson, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(propertyName, out var property)
                   && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }
}
