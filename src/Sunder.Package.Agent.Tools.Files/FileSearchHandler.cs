using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;

namespace Sunder.Package.Agent.Tools.Files;

internal static class FileSearchHandler
{
    private const int MaxResults = 1000;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task<FileSearchToolResult> ExecuteGrepAsync(
        IAgentExecutionTarget target,
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
        if (AgentExecutionTargetRpc.SupportsFacet(target, AgentExecutionFacetIds.StructuredFileSearch)
            && target is IAgentStructuredFileSearchExecutionTarget structuredTarget)
        {
            return await ExecuteStructuredGrepAsync(structuredTarget, context, request.ToolId, path, args, cancellationToken);
        }
        if (!AgentExecutionTargetRpc.SupportsFacet(target, AgentExecutionFacetIds.LegacyFileSearch)
            || target is not IAgentFileSearchExecutionTarget searchTarget)
        {
            return SearchBindingUnavailable(request.ToolId, target);
        }

        var execution = await ExecuteCommandAsync(searchTarget, context, path, BuildRgGrepCommand(args), cancellationToken);
        var result = execution.ProcessResult;
        var usedFallback = false;
        if (IsCommandMissing(result, "rg"))
        {
            usedFallback = true;
            execution = await ExecuteCommandAsync(searchTarget, context, path, BuildGrepFallbackCommand(args), cancellationToken);
            result = execution.ProcessResult;
        }
        if (result.WasTruncated)
        {
            return IncompleteSearchResult(request.ToolId, target);
        }
        if (result.ExitCode > 1)
        {
            return BackendFailure(request.ToolId, target, "Search", result);
        }

        var parsed = usedFallback
            ? ParseNullDelimitedGrepResults(result.Output)
            : ParseJsonGrepResults(result.Output);
        if (!parsed.IsComplete)
        {
            return InvalidSearchResult(request.ToolId, target);
        }

        var mappedMatches = new List<(FileGrepMatch Match, string CanonicalPath)>(parsed.Matches.Count);
        foreach (var match in parsed.Matches)
        {
            if (!TryMapResultPath(execution, match.Path, out var mappedPath))
            {
                return InvalidSearchResult(request.ToolId, target);
            }
            mappedMatches.Add((match with { Path = mappedPath.ReportedPath }, mappedPath.CanonicalPath));
        }

        var selectedMatches = mappedMatches.Take(MaxResults).ToArray();
        var matches = selectedMatches.Select(match => match.Match).ToArray();
        var wasTruncated = mappedMatches.Count > MaxResults;
        var toolResult = new AgentToolResult(
            request.ToolId,
            $"Found {FileToolResult.FormatCount(matches.Length, "match", "matches")}" + (wasTruncated ? " (truncated)" : string.Empty),
            Content: FormatGrepResults(matches, string.Empty, wasTruncated),
            StructuredPayloadJson: JsonSerializer.Serialize(matches, JsonOptions),
            WasTruncated: wasTruncated,
            BackendId: FileToolResult.BackendId(target));
        return new FileSearchToolResult(
            toolResult,
            selectedMatches.Select(match => match.CanonicalPath).Distinct(StringComparer.Ordinal).ToArray());
    }

    public static async Task<FileSearchToolResult> ExecuteGlobAsync(
        IAgentExecutionTarget target,
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
        if (AgentExecutionTargetRpc.SupportsFacet(target, AgentExecutionFacetIds.StructuredFileSearch)
            && target is IAgentStructuredFileSearchExecutionTarget structuredTarget)
        {
            return await ExecuteStructuredGlobAsync(structuredTarget, context, request.ToolId, path, args, cancellationToken);
        }
        if (!AgentExecutionTargetRpc.SupportsFacet(target, AgentExecutionFacetIds.LegacyFileSearch)
            || target is not IAgentFileSearchExecutionTarget searchTarget)
        {
            return SearchBindingUnavailable(request.ToolId, target);
        }

        var execution = await ExecuteCommandAsync(searchTarget, context, path, BuildRgGlobCommand(args), cancellationToken);
        var result = execution.ProcessResult;
        var usedFallback = false;
        if (IsCommandMissing(result, "rg"))
        {
            usedFallback = true;
            execution = await ExecuteCommandAsync(searchTarget, context, path, BuildGlobFallbackCommand(), cancellationToken);
            result = execution.ProcessResult;
        }
        if (result.WasTruncated)
        {
            return IncompleteSearchResult(request.ToolId, target);
        }
        if (result.ExitCode > 1)
        {
            return BackendFailure(request.ToolId, target, "Glob", result);
        }

        var mappedPaths = new List<MappedSearchPath>();
        foreach (var candidate in SplitPathResults(result.Output))
        {
            if (!TryMapResultPath(execution, candidate, out var mappedPath))
            {
                return InvalidSearchResult(request.ToolId, target);
            }
            mappedPaths.Add(mappedPath);
        }
        if (usedFallback)
        {
            mappedPaths = mappedPaths
                .Where(candidate => MatchesGlobPattern(args.Pattern, execution.ExpectedPath, candidate.ReportedPath))
                .ToList();
        }

        var boundedPaths = mappedPaths.Take(MaxResults + 1).ToArray();
        var wasTruncated = boundedPaths.Length > MaxResults;
        var selectedPaths = boundedPaths.Take(MaxResults).ToArray();
        var matches = selectedPaths.Select(candidate => new FileGlobMatch(candidate.ReportedPath)).ToArray();
        var toolResult = new AgentToolResult(
            request.ToolId,
            $"Found {FileToolResult.FormatCount(matches.Length, "match", "matches")}" + (wasTruncated ? " (truncated)" : string.Empty),
            Content: string.Join(Environment.NewLine, matches.Select(match => match.Path)),
            StructuredPayloadJson: JsonSerializer.Serialize(matches, JsonOptions),
            WasTruncated: wasTruncated,
            BackendId: FileToolResult.BackendId(target));
        return new FileSearchToolResult(
            toolResult,
            selectedPaths.Select(match => match.CanonicalPath).Distinct(StringComparer.Ordinal).ToArray());
    }

    private static async ValueTask<AgentFileSearchProcessResult> ExecuteCommandAsync(
        IAgentFileSearchExecutionTarget target,
        AgentExecutionTargetContext context,
        string path,
        FileProcessCommand command,
        CancellationToken cancellationToken)
        => await target.ExecuteFileSearchProcessAsync(
            context,
            new AgentFileSearchProcessRequest(
                path,
                new AgentProcessCommandRequest(command.FileName, command.Arguments),
                command.PathArgumentIndex),
            cancellationToken);

    private static async Task<FileSearchToolResult> ExecuteStructuredGrepAsync(
        IAgentStructuredFileSearchExecutionTarget target,
        AgentExecutionTargetContext context,
        string toolId,
        string path,
        FileGrepArgs args,
        CancellationToken cancellationToken)
    {
        var result = await target.ExecuteFileSearchAsync(
            context,
            new AgentFileSearchRequest(path, AgentFileSearchKind.Grep, args.Pattern, args.Include),
            cancellationToken);
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
            .Select(match => new FileGrepMatch(match.Path, match.LineNumber!.Value, match.Text!))
            .ToArray();
        return new FileSearchToolResult(
            new AgentToolResult(
                toolId,
                $"Found {FileToolResult.FormatCount(matches.Length, "match", "matches")}" + (wasTruncated ? " (truncated)" : string.Empty),
                Content: FormatGrepResults(matches, string.Empty, wasTruncated),
                StructuredPayloadJson: JsonSerializer.Serialize(matches, JsonOptions),
                WasTruncated: wasTruncated,
                BackendId: FileToolResult.BackendId(target)),
            selected.Select(match => match.Path).Distinct(StringComparer.Ordinal).ToArray());
    }

    private static async Task<FileSearchToolResult> ExecuteStructuredGlobAsync(
        IAgentStructuredFileSearchExecutionTarget target,
        AgentExecutionTargetContext context,
        string toolId,
        string path,
        FileGlobArgs args,
        CancellationToken cancellationToken)
    {
        var result = await target.ExecuteFileSearchAsync(
            context,
            new AgentFileSearchRequest(path, AgentFileSearchKind.Glob, args.Pattern),
            cancellationToken);
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
            new AgentToolResult(
                toolId,
                $"Found {FileToolResult.FormatCount(matches.Length, "match", "matches")}" + (wasTruncated ? " (truncated)" : string.Empty),
                Content: string.Join(Environment.NewLine, matches.Select(match => EscapeDisplayPath(match.Path))),
                StructuredPayloadJson: JsonSerializer.Serialize(matches, JsonOptions),
                WasTruncated: wasTruncated,
                BackendId: FileToolResult.BackendId(target)),
            selected.Select(match => match.Path).Distinct(StringComparer.Ordinal).ToArray());
    }

    private static FileSearchToolResult StructuredFailure(
        string toolId,
        IAgentExecutionTarget target,
        AgentFileSearchResult result)
        => new(
            new AgentToolResult(
                toolId,
                "Search failed before a governed result could be returned.",
                Content: result.ErrorMessage ?? "The execution target rejected the structured search.",
                IsError: true,
                ErrorCode: result.ErrorCode ?? "files-search-backend-failed",
                BackendId: FileToolResult.BackendId(target)),
            []);

    private static FileProcessCommand BuildRgGrepCommand(FileGrepArgs args)
    {
        var arguments = new List<string> { "--json", "--line-number", "--no-heading" };
        if (!string.IsNullOrWhiteSpace(args.Include))
        {
            arguments.Add("-g");
            arguments.Add(args.Include);
        }

        arguments.Add("--");
        arguments.Add(args.Pattern);
        return new FileProcessCommand("rg", arguments, arguments.Count);
    }

    private static FileProcessCommand BuildRgGlobCommand(FileGlobArgs args)
        => new("rg", ["--null", "--files", "-g", args.Pattern, "--"], PathArgumentIndex: 5);

    private static FileProcessCommand BuildGrepFallbackCommand(FileGrepArgs args)
        => string.IsNullOrWhiteSpace(args.Include)
            ? new FileProcessCommand("grep", ["-E", "-rIn", "-H", "--null", "--", args.Pattern], PathArgumentIndex: 6)
            : new FileProcessCommand("find", ["-type", "f", "-name", args.Include, "-exec", "grep", "-E", "-In", "-H", "--null", "--", args.Pattern, "{}", "+"], PathArgumentIndex: 0);

    private static FileProcessCommand BuildGlobFallbackCommand()
        => new("find", ["-type", "f", "-print0"], PathArgumentIndex: 0);

    private static bool IsCommandMissing(AgentShellCommandResult result, string commandName)
        => result.ExitCode == 127
           || result.Output.Contains($"{commandName}: not found", StringComparison.OrdinalIgnoreCase)
           || result.Output.Contains($"{commandName}: command not found", StringComparison.OrdinalIgnoreCase)
           || result.Output.Contains("executable file not found", StringComparison.OrdinalIgnoreCase);

    private static GrepParseResult ParseJsonGrepResults(string output)
    {
        var matches = new List<FileGrepMatch>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var typeProperty)
                    || typeProperty.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Object)
                {
                    return new GrepParseResult(matches, IsComplete: false);
                }
                var type = typeProperty.GetString();
                if (type is "begin" or "end" or "summary" or "context")
                {
                    continue;
                }
                if (type != "match"
                    || !data.TryGetProperty("path", out var pathValue)
                    || !TryReadRgText(pathValue, out var path)
                    || !data.TryGetProperty("lines", out var linesValue)
                    || !TryReadRgText(linesValue, out var text)
                    || !data.TryGetProperty("line_number", out var lineNumberValue)
                    || !lineNumberValue.TryGetInt32(out var lineNumber)
                    || lineNumber <= 0)
                {
                    return new GrepParseResult(matches, IsComplete: false);
                }
                matches.Add(new FileGrepMatch(path!, lineNumber, RemoveSingleLineTerminator(text!)));
            }
            catch (JsonException)
            {
                return new GrepParseResult(matches, IsComplete: false);
            }
        }

        return new GrepParseResult(matches, IsComplete: true);
    }

    private static GrepParseResult ParseNullDelimitedGrepResults(string output)
    {
        if (output.Length == 0)
        {
            return new GrepParseResult([], IsComplete: true);
        }

        var matches = new List<FileGrepMatch>();
        var offset = 0;
        while (offset < output.Length)
        {
            var separator = output.IndexOf('\0', offset);
            if (separator <= offset)
            {
                return new GrepParseResult(matches, IsComplete: false);
            }
            var lineEnd = output.IndexOf('\n', separator + 1);
            if (lineEnd < 0)
            {
                lineEnd = output.Length;
            }
            var payload = output[(separator + 1)..lineEnd].TrimEnd('\r');
            var colon = payload.IndexOf(':');
            if (colon <= 0
                || !int.TryParse(payload[..colon], out var lineNumber)
                || lineNumber <= 0)
            {
                return new GrepParseResult(matches, IsComplete: false);
            }

            matches.Add(new FileGrepMatch(
                output[offset..separator],
                lineNumber,
                payload[(colon + 1)..]));
            offset = lineEnd < output.Length ? lineEnd + 1 : lineEnd;
        }

        return new GrepParseResult(matches, IsComplete: true);
    }

    private static bool TryReadRgText(JsonElement value, out string? text)
    {
        text = null;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        if (value.TryGetProperty("text", out var textProperty)
            && textProperty.ValueKind == JsonValueKind.String)
        {
            text = textProperty.GetString();
            return !string.IsNullOrEmpty(text) && !text.Contains('\0');
        }
        if (!value.TryGetProperty("bytes", out var bytesProperty)
            || bytesProperty.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        try
        {
            text = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(bytesProperty.GetString()!));
            return text.Length > 0 && !text.Contains('\0');
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static string RemoveSingleLineTerminator(string value)
        => value.EndsWith("\r\n", StringComparison.Ordinal)
            ? value[..^2]
            : value.EndsWith('\n')
                ? value[..^1]
                : value;

    private static FileSearchToolResult IncompleteSearchResult(
        string toolId,
        IAgentExecutionTarget target)
        => new(
            new AgentToolResult(
                toolId,
                "Search output was incomplete.",
                Content: "The search backend truncated its structured output, so no matches were returned.",
                IsError: true,
                ErrorCode: "files-search-result-incomplete",
                BackendId: FileToolResult.BackendId(target)),
            []);

    private static FileSearchToolResult SearchBindingUnavailable(
        string toolId,
        IAgentExecutionTarget target)
        => new(
            new AgentToolResult(
                toolId,
                "The selected target cannot safely bind file-search paths.",
                Content: "The search was not started because this target cannot execute against a revalidated canonical path.",
                IsError: true,
                ErrorCode: "files-search-binding-unavailable",
                BackendId: FileToolResult.BackendId(target)),
            []);

    private static FileSearchToolResult BackendFailure(
        string toolId,
        IAgentExecutionTarget target,
        string operation,
        AgentShellCommandResult result)
        => new(
            new AgentToolResult(
                toolId,
                $"{operation} failed with exit code {result.ExitCode}.",
                Content: "The search backend failed before a governed result could be returned.",
                WasTruncated: result.WasTruncated,
                IsError: true,
                ErrorCode: "files-search-backend-failed",
                BackendId: FileToolResult.BackendId(target)),
            []);

    private static FileSearchToolResult InvalidSearchResult(
        string toolId,
        IAgentExecutionTarget target)
        => new(
            new AgentToolResult(
                toolId,
                "Search results could not be parsed safely.",
                Content: "The search backend returned incomplete, ambiguous, or out-of-scope structured output, so no matches were returned.",
                IsError: true,
                ErrorCode: "files-search-result-invalid",
                BackendId: FileToolResult.BackendId(target)),
            []);

    private static IEnumerable<string> SplitPathResults(string output)
        => output.Contains('\0')
            ? output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            : output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryMapResultPath(
        AgentFileSearchProcessResult execution,
        string candidate,
        out MappedSearchPath mapped)
    {
        mapped = default!;
        if (string.IsNullOrEmpty(candidate) || candidate.Contains('\0'))
        {
            return false;
        }

        return execution.PathStyle switch
        {
            AgentFileSearchPathStyle.Host => TryMapHostResultPath(execution, candidate, out mapped),
            AgentFileSearchPathStyle.Posix => TryMapPosixResultPath(execution, candidate, out mapped),
            _ => false,
        };
    }

    private static bool TryMapHostResultPath(
        AgentFileSearchProcessResult execution,
        string candidate,
        out MappedSearchPath mapped)
    {
        mapped = default!;
        try
        {
            if (!Path.IsPathFullyQualified(execution.CanonicalPath)
                || !Path.IsPathFullyQualified(execution.ExpectedPath)
                || !Path.IsPathFullyQualified(candidate))
            {
                return false;
            }

            var canonicalRoot = Path.GetFullPath(execution.CanonicalPath);
            var expectedRoot = Path.GetFullPath(execution.ExpectedPath);
            var canonicalCandidate = Path.GetFullPath(candidate);
            var relative = Path.GetRelativePath(canonicalRoot, canonicalCandidate);
            if (Path.IsPathRooted(relative)
                || relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                return false;
            }

            var reported = relative == "."
                ? expectedRoot
                : Path.GetFullPath(Path.Combine(expectedRoot, relative));
            mapped = new MappedSearchPath(canonicalCandidate, reported);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryMapPosixResultPath(
        AgentFileSearchProcessResult execution,
        string candidate,
        out MappedSearchPath mapped)
    {
        mapped = default!;
        if (!TryNormalizePosixAbsolutePath(execution.CanonicalPath, out var canonicalRoot)
            || !TryNormalizePosixAbsolutePath(execution.ExpectedPath, out var expectedRoot)
            || !TryNormalizePosixAbsolutePath(candidate, out var canonicalCandidate)
            || !TryGetPosixRelativePath(canonicalRoot, canonicalCandidate, out var relative))
        {
            return false;
        }

        var reported = relative.Length == 0
            ? expectedRoot
            : expectedRoot == "/" ? "/" + relative : expectedRoot + "/" + relative;
        mapped = new MappedSearchPath(canonicalCandidate, reported);
        return true;
    }

    private static bool TryNormalizePosixAbsolutePath(string path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(path) || path[0] != '/' || path.Contains('\0'))
        {
            return false;
        }

        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return false;
                }
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }

        normalized = segments.Count == 0 ? "/" : "/" + string.Join('/', segments);
        return true;
    }

    private static bool TryGetPosixRelativePath(string root, string candidate, out string relative)
    {
        relative = string.Empty;
        if (candidate == root)
        {
            return true;
        }
        if (root == "/" && candidate.StartsWith("/", StringComparison.Ordinal))
        {
            relative = candidate[1..];
            return true;
        }
        if (!candidate.StartsWith(root + "/", StringComparison.Ordinal))
        {
            return false;
        }

        relative = candidate[(root.Length + 1)..];
        return true;
    }

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

    private static string NormalizeFindBasePath(string path)
    {
        var normalized = string.IsNullOrWhiteSpace(path) ? "." : path.Replace('\\', '/');
        while (normalized.Length > 1 && normalized.EndsWith("/", StringComparison.Ordinal))
        {
            normalized = normalized[..^1];
        }

        return string.IsNullOrWhiteSpace(normalized) ? "." : normalized;
    }

    private static bool MatchesGlobPattern(string pattern, string path, string candidate)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(candidate))
        {
            return true;
        }

        var normalizedPattern = pattern.Trim().Replace('\\', '/');
        var normalizedCandidate = candidate.Replace('\\', '/');
        var relativeCandidate = NormalizeRelativeCandidate(NormalizeFindBasePath(path), normalizedCandidate);
        return ExpandGlobBraces(normalizedPattern)
            .Any(expanded => MatchesSingleGlobPattern(expanded, normalizedCandidate, relativeCandidate));
    }

    private static string NormalizeRelativeCandidate(string basePath, string candidate)
    {
        if (candidate.StartsWith("./", StringComparison.Ordinal))
        {
            candidate = candidate[2..];
        }

        if (basePath == ".")
        {
            return candidate.TrimStart('/');
        }

        if (candidate.Equals(basePath, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return candidate.StartsWith(basePath + "/", StringComparison.Ordinal)
            ? candidate[(basePath.Length + 1)..]
            : candidate.TrimStart('/');
    }

    private static bool MatchesSingleGlobPattern(string pattern, string candidate, string relative)
    {
        if (pattern.StartsWith("./", StringComparison.Ordinal))
        {
            return MatchesGlobSegments(pattern[2..], relative);
        }

        if (pattern.StartsWith("/", StringComparison.Ordinal))
        {
            return MatchesGlobSegments(pattern.TrimStart('/'), candidate.TrimStart('/'));
        }

        if (!pattern.Contains('/', StringComparison.Ordinal))
        {
            return MatchesGlobSegment(pattern, relative.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? relative);
        }

        return MatchesGlobSegments(pattern, relative);
    }

    private static bool MatchesGlobSegments(string pattern, string candidate)
        => MatchesGlobSegments(
            pattern.Split('/', StringSplitOptions.RemoveEmptyEntries),
            0,
            candidate.Split('/', StringSplitOptions.RemoveEmptyEntries),
            0);

    private static bool MatchesGlobSegments(IReadOnlyList<string> pattern, int patternIndex, IReadOnlyList<string> candidate, int candidateIndex)
    {
        while (patternIndex < pattern.Count)
        {
            if (pattern[patternIndex] == "**")
            {
                if (patternIndex == pattern.Count - 1)
                {
                    return true;
                }

                return Enumerable.Range(candidateIndex, candidate.Count - candidateIndex + 1)
                    .Any(next => MatchesGlobSegments(pattern, patternIndex + 1, candidate, next));
            }

            if (candidateIndex >= candidate.Count || !MatchesGlobSegment(pattern[patternIndex], candidate[candidateIndex]))
            {
                return false;
            }

            patternIndex++;
            candidateIndex++;
        }

        return candidateIndex == candidate.Count;
    }

    private static bool MatchesGlobSegment(string pattern, string candidate)
        => Regex.IsMatch(candidate, "^" + GlobSegmentToRegex(pattern) + "$", RegexOptions.CultureInvariant);

    private static string GlobSegmentToRegex(string pattern)
    {
        var builder = new StringBuilder();
        foreach (var character in pattern)
        {
            builder.Append(character switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(character.ToString()),
            });
        }

        return builder.ToString();
    }

    private static IEnumerable<string> ExpandGlobBraces(string pattern)
    {
        var open = pattern.IndexOf('{', StringComparison.Ordinal);
        var close = open < 0 ? -1 : pattern.IndexOf('}', open + 1);
        if (open < 0 || close < 0)
        {
            yield return pattern;
            yield break;
        }

        foreach (var option in pattern[(open + 1)..close].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var suffix in ExpandGlobBraces(pattern[(close + 1)..]))
            {
                yield return pattern[..open] + option + suffix;
            }
        }
    }
}

internal sealed record FileGrepMatch(string Path, int LineNumber, string Text);

internal sealed record FileGlobMatch(string Path);

internal sealed record FileSearchToolResult(
    AgentToolResult Result,
    IReadOnlyList<string> MatchedPaths);

internal sealed record GrepParseResult(
    IReadOnlyList<FileGrepMatch> Matches,
    bool IsComplete);

internal sealed record MappedSearchPath(string CanonicalPath, string ReportedPath);

internal sealed record FileProcessCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    int PathArgumentIndex);
