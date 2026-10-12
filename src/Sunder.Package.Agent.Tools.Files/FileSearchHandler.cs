using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;

namespace Sunder.Package.Agent.Tools.Files;

internal static class FileSearchHandler
{
    private const int MaxResults = 1000;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task<FileSearchToolResult> ExecuteGrepAsync(
        FileExecutionTarget target,
        AgentExecutionTargetContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken)
    {
        if (!FileToolArguments.TryParseGrep(request.ArgumentsJson, out var args, out var error))
        {
            return new FileSearchToolResult(
                FileToolResult.Error(request.ToolId, error!, "files-arguments-invalid"),
                []);
        }

        var path = string.IsNullOrWhiteSpace(args.Path) ? "." : args.Path;
        if (FilesExecutionFacets.SupportsFacet(target, AgentExecutionFacetIds.StructuredFileSearch))
        {
            return await ExecuteStructuredGrepAsync(target, context, request.ToolId, path, args, cancellationToken);
        }
        return SearchBindingUnavailable(request.ToolId, target);
    }

    public static async Task<FileSearchToolResult> ExecuteGlobAsync(
        FileExecutionTarget target,
        AgentExecutionTargetContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken)
    {
        if (!FileToolArguments.TryParseGlob(request.ArgumentsJson, out var args, out var error))
        {
            return new FileSearchToolResult(
                FileToolResult.Error(request.ToolId, error!, "files-arguments-invalid"),
                []);
        }

        var path = string.IsNullOrWhiteSpace(args.Path) ? "." : args.Path;
        if (FilesExecutionFacets.SupportsFacet(target, AgentExecutionFacetIds.StructuredFileSearch))
        {
            return await ExecuteStructuredGlobAsync(target, context, request.ToolId, path, args, cancellationToken);
        }
        return SearchBindingUnavailable(request.ToolId, target);
    }

    private static async Task<FileSearchToolResult> ExecuteStructuredGrepAsync(
        FileExecutionTarget target,
        AgentExecutionTargetContext context,
        string toolId,
        string path,
        FileGrepArgs args,
        CancellationToken cancellationToken)
    {
        var result = await target.Service.StructuredFileSearchAsync(new()
        { Context = context, Request = new() { Path = path, Kind = AgentFileSearchKind.Grep, Pattern = args.Pattern, Include = args.Include } }, cancellationToken);
        if (result.IsError)
        {
            return StructuredFailure(toolId, target, result);
        }
        if (result.Matches.Any(match => string.IsNullOrEmpty(match.Path)
                                        || match.LineNumber is null or <= 0
                                        || match.Text is null))
        {
            return InvalidSearchResult(toolId, target);
        }

        var bounded = result.Matches.Take(MaxResults + 1).ToArray();
        var wasTruncated = result.WasTruncated || bounded.Length > MaxResults;
        var selected = bounded.Take(MaxResults).ToArray();
        var matches = selected
            .Select(match => new FileGrepMatch(match.Path, checked((int)match.LineNumber!.Value), match.Text!))
            .ToArray();
        return new FileSearchToolResult(
            FileToolResult.Success(
                toolId,
                $"Found {FileToolResult.FormatCount(matches.Length, "match", "matches")}" + (wasTruncated ? " (truncated)" : string.Empty),
                FormatGrepResults(matches, string.Empty, wasTruncated), FileToolResult.BackendId(target), wasTruncated,
                JsonSerializer.Serialize(matches, JsonOptions)),
            selected.Select(match => match.Path).Distinct(StringComparer.Ordinal).ToArray());
    }

    private static async Task<FileSearchToolResult> ExecuteStructuredGlobAsync(
        FileExecutionTarget target,
        AgentExecutionTargetContext context,
        string toolId,
        string path,
        FileGlobArgs args,
        CancellationToken cancellationToken)
    {
        var result = await target.Service.StructuredFileSearchAsync(new()
        { Context = context, Request = new() { Path = path, Kind = AgentFileSearchKind.Glob, Pattern = args.Pattern, Include = null } }, cancellationToken);
        if (result.IsError)
        {
            return StructuredFailure(toolId, target, result);
        }
        if (result.Matches.Any(match => string.IsNullOrEmpty(match.Path)
                                        || match.LineNumber is not null
                                        || match.Text is not null))
        {
            return InvalidSearchResult(toolId, target);
        }

        var bounded = result.Matches.Take(MaxResults + 1).ToArray();
        var wasTruncated = result.WasTruncated || bounded.Length > MaxResults;
        var selected = bounded.Take(MaxResults).ToArray();
        var matches = selected.Select(match => new FileGlobMatch(match.Path)).ToArray();
        return new FileSearchToolResult(
            FileToolResult.Success(
                toolId,
                $"Found {FileToolResult.FormatCount(matches.Length, "match", "matches")}" + (wasTruncated ? " (truncated)" : string.Empty),
                string.Join(Environment.NewLine, matches.Select(match => EscapeDisplayPath(match.Path))), FileToolResult.BackendId(target), wasTruncated,
                JsonSerializer.Serialize(matches, JsonOptions)),
            selected.Select(match => match.Path).Distinct(StringComparer.Ordinal).ToArray());
    }

    private static FileSearchToolResult StructuredFailure(
        string toolId,
        FileExecutionTarget target,
        AgentFileSearchResult result)
        => new(
            FileToolResult.Error(
                toolId,
                "Search failed before a governed result could be returned.",
                result.ErrorCode ?? "files-search-backend-failed") with
            { Content = result.ErrorMessage ?? "The execution target rejected the structured search.", BackendId = FileToolResult.BackendId(target) },
            []);

    private static FileSearchToolResult SearchBindingUnavailable(
        string toolId,
        FileExecutionTarget target)
        => new(
            FileToolResult.Error(
                toolId,
                "The selected target does not support structured file search.",
                "files-search-binding-unavailable") with
            { Content = "The search was not started because Files does not fall back to process or shell execution.", BackendId = FileToolResult.BackendId(target) },
            []);

    private static FileSearchToolResult InvalidSearchResult(
        string toolId,
        FileExecutionTarget target)
        => new(
            FileToolResult.Error(
                toolId,
                "Search results could not be parsed safely.",
                "files-search-result-invalid") with
            { Content = "The search backend returned incomplete, ambiguous, or out-of-scope structured output, so no matches were returned.", BackendId = FileToolResult.BackendId(target) },
            []);

    private static string FormatGrepResults(IReadOnlyList<FileGrepMatch> matches, string fallbackOutput, bool wasTruncated)
    {
        var content = matches.Count == 0
            ? fallbackOutput
            : string.Join(
                Environment.NewLine,
                matches.Select(match => $"{EscapeDisplayPath(match.Path)}:{match.LineNumber}: {match.Text}"));
        return wasTruncated && !string.IsNullOrWhiteSpace(content)
            ? content + Environment.NewLine + $"[Results truncated at {MaxResults} matches]"
            : content;
    }

    private static string EscapeDisplayPath(string path)
        => path.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

}

internal sealed record FileGrepMatch(string Path, int LineNumber, string Text);

internal sealed record FileGlobMatch(string Path);

internal sealed record FileSearchToolResult(
    AgentToolResult Result,
    IReadOnlyList<string> MatchedPaths);
