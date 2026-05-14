using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tools.Files;

public sealed partial class FilesToolSource
{
    private static AgentToolPresentation ResolveReadPresentation(AgentToolPresentationRequest request)
    {
        var parsed = TryParseReadArgs(request.ArgumentsJson, out var args, out var error);
        return new AgentToolPresentation(
            HeaderText: request.ResultSummary ?? (parsed ? args.Path : null),
            DetailMarkdown: !parsed
                ? BuildRawRequestMarkdown(request.ArgumentsJson, error)
                : BuildRequestMarkdown(
                    ("Path", args.Path),
                    ("Offset", args.Offset?.ToString()),
                    ("Limit", args.Limit?.ToString())),
            OutputText: request.TextContent);
    }

    private static AgentToolPresentation ResolveWritePresentation(AgentToolPresentationRequest request)
    {
        var parsed = TryParseWriteArgs(request.ArgumentsJson, out var args, out var error);
        return new AgentToolPresentation(
            HeaderText: request.ResultSummary ?? (parsed ? args.Path : null),
            DetailMarkdown: !parsed
                ? BuildRawRequestMarkdown(request.ArgumentsJson, error)
                : BuildRequestMarkdown(
                    ("Path", args.Path),
                    ("Content", FormatTextStats(args.Content))),
            OutputText: request.TextContent);
    }

    private static AgentToolPresentation ResolveEditPresentation(AgentToolPresentationRequest request)
    {
        var parsed = TryParseEditArgs(request.ArgumentsJson, out var args, out var error);
        return new AgentToolPresentation(
            HeaderText: request.ResultSummary ?? (parsed ? args.Path : null),
            DetailMarkdown: !parsed
                ? BuildRawRequestMarkdown(request.ArgumentsJson, error)
                : BuildRequestMarkdown(
                    ("Path", args.Path),
                    ("Old text", FormatTextStats(args.OldString)),
                    ("New text", FormatTextStats(args.NewString)),
                    ("Replace all", args.ReplaceAll ? "yes" : "no")),
            OutputText: request.TextContent);
    }

    private static AgentToolPresentation ResolveGlobPresentation(AgentToolPresentationRequest request)
    {
        var parsed = TryParseGlobArgs(request.ArgumentsJson, out var args, out var error);
        var pattern = parsed ? args.Pattern : null;
        var path = parsed ? args.Path : null;
        var header = string.IsNullOrWhiteSpace(path)
            ? pattern
            : string.IsNullOrWhiteSpace(pattern)
                ? path
                : $"{pattern} in {path}";
        return new AgentToolPresentation(
            HeaderText: request.ResultSummary ?? header,
            DetailMarkdown: !parsed
                ? BuildRawRequestMarkdown(request.ArgumentsJson, error)
                : BuildRequestMarkdown(
                    ("Pattern", args.Pattern),
                    ("Path", string.IsNullOrWhiteSpace(args.Path) ? "." : args.Path)),
            OutputText: request.TextContent);
    }

    private static AgentToolPresentation ResolveGrepPresentation(AgentToolPresentationRequest request)
    {
        var parsed = TryParseGrepArgs(request.ArgumentsJson, out var args, out var error);
        var pattern = parsed ? args.Pattern : null;
        var include = parsed ? args.Include : null;
        var path = parsed ? args.Path : null;
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
            DetailMarkdown: !parsed
                ? BuildRawRequestMarkdown(request.ArgumentsJson, error)
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
            ? BuildRawRequestMarkdown(request.ArgumentsJson, "The `patchText` parameter is required.")
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

    private static string BuildRawRequestMarkdown(string argumentsJson, string? error = null)
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
