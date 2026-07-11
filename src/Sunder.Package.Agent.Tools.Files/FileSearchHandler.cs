using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tools.Files;

internal static class FileSearchHandler
{
    private const int MaxResults = 1000;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task<AgentToolResult> ExecuteGrepAsync(
        IAgentExecutionTarget target,
        AgentExecutionTargetContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken)
    {
        if (!FileToolArguments.TryParseGrep(request.ArgumentsJson, out var args, out var error))
        {
            return FileToolResult.Error(request.ToolId, error!, "files-arguments-invalid");
        }

        var path = string.IsNullOrWhiteSpace(args.Path) ? "." : args.Path;
        var result = await ExecuteCommandAsync(target, context, BuildRgGrepCommand(args, path), cancellationToken);
        if (IsCommandMissing(result, "rg"))
        {
            result = await ExecuteCommandAsync(target, context, BuildGrepFallbackCommand(args, path), cancellationToken);
        }

        var allMatches = ParseGrepResults(result.Output);
        var matches = allMatches.Take(MaxResults).ToArray();
        var wasTruncated = result.WasTruncated || allMatches.Count > MaxResults;
        return new AgentToolResult(
            request.ToolId,
            result.ExitCode <= 1
                ? $"Found {FileToolResult.FormatCount(matches.Length, "match", "matches")}" + (wasTruncated ? " (truncated)" : string.Empty)
                : $"Search failed with exit code {result.ExitCode} and {FileToolResult.FormatCount(matches.Length, "match", "matches")}",
            Content: FormatGrepResults(matches, result.Output, wasTruncated),
            StructuredPayloadJson: JsonSerializer.Serialize(matches, JsonOptions),
            WasTruncated: wasTruncated,
            IsError: result.ExitCode > 1,
            BackendId: FileToolResult.BackendId(target));
    }

    public static async Task<AgentToolResult> ExecuteGlobAsync(
        IAgentExecutionTarget target,
        AgentExecutionTargetContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken)
    {
        if (!FileToolArguments.TryParseGlob(request.ArgumentsJson, out var args, out var error))
        {
            return FileToolResult.Error(request.ToolId, error!, "files-arguments-invalid");
        }

        var path = string.IsNullOrWhiteSpace(args.Path) ? "." : args.Path;
        var result = await ExecuteCommandAsync(target, context, BuildRgGlobCommand(args, path), cancellationToken);
        var usedFallback = false;
        if (IsCommandMissing(result, "rg"))
        {
            usedFallback = true;
            result = await ExecuteCommandAsync(target, context, BuildGlobFallbackCommand(args, path), cancellationToken);
        }

        IEnumerable<string> paths = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (usedFallback)
        {
            paths = paths.Where(candidate => MatchesGlobPattern(args.Pattern, path, candidate));
        }

        var boundedPaths = paths.Take(MaxResults + 1).ToArray();
        var wasTruncated = result.WasTruncated || boundedPaths.Length > MaxResults;
        var matches = boundedPaths.Take(MaxResults).Select(candidate => new FileGlobMatch(candidate)).ToArray();
        return new AgentToolResult(
            request.ToolId,
            result.ExitCode <= 1
                ? $"Found {FileToolResult.FormatCount(matches.Length, "match", "matches")}" + (wasTruncated ? " (truncated)" : string.Empty)
                : $"Glob failed with exit code {result.ExitCode} and {FileToolResult.FormatCount(matches.Length, "match", "matches")}",
            Content: string.Join(Environment.NewLine, matches.Select(match => match.Path)),
            StructuredPayloadJson: JsonSerializer.Serialize(matches, JsonOptions),
            WasTruncated: wasTruncated,
            IsError: result.ExitCode > 1,
            BackendId: FileToolResult.BackendId(target));
    }

    private static async ValueTask<AgentShellCommandResult> ExecuteCommandAsync(
        IAgentExecutionTarget target,
        AgentExecutionTargetContext context,
        FileProcessCommand command,
        CancellationToken cancellationToken)
    {
        if (target is IAgentProcessExecutionTarget processTarget)
        {
            return await processTarget.ExecuteProcessAsync(
                context,
                new AgentProcessCommandRequest(command.FileName, command.Arguments),
                cancellationToken);
        }

        return await target.ExecuteShellAsync(
            context,
            new AgentShellCommandRequest(BuildShellCommand(command)),
            cancellationToken);
    }

    private static FileProcessCommand BuildRgGrepCommand(FileGrepArgs args, string path)
    {
        var arguments = new List<string> { "--line-number", "--no-heading" };
        if (!string.IsNullOrWhiteSpace(args.Include))
        {
            arguments.Add("-g");
            arguments.Add(args.Include);
        }

        arguments.Add("--");
        arguments.Add(args.Pattern);
        arguments.Add(path);
        return new FileProcessCommand("rg", arguments);
    }

    private static FileProcessCommand BuildRgGlobCommand(FileGlobArgs args, string path)
        => new("rg", ["--files", "-g", args.Pattern, "--", path]);

    private static FileProcessCommand BuildGrepFallbackCommand(FileGrepArgs args, string path)
        => string.IsNullOrWhiteSpace(args.Include)
            ? new FileProcessCommand("grep", ["-E", "-RIn", "--", args.Pattern, path])
            : new FileProcessCommand("find", [path, "-type", "f", "-name", args.Include, "-exec", "grep", "-E", "-In", "--", args.Pattern, "{}", "+"]);

    private static FileProcessCommand BuildGlobFallbackCommand(FileGlobArgs args, string path)
    {
        var patterns = BuildFindPathPatterns(path, args.Pattern);
        if (patterns.Count == 0)
        {
            return new FileProcessCommand("find", [path, "-type", "f", "-print"]);
        }

        var arguments = new List<string> { path, "-type", "f", "(" };
        for (var index = 0; index < patterns.Count; index++)
        {
            if (index > 0)
            {
                arguments.Add("-o");
            }

            arguments.Add("-path");
            arguments.Add(patterns[index]);
        }

        arguments.Add(")");
        arguments.Add("-print");
        return new FileProcessCommand("find", arguments);
    }

    private static string BuildShellCommand(FileProcessCommand command)
        => command.Arguments.Count == 0
            ? command.FileName
            : $"{command.FileName} {string.Join(" ", command.Arguments.Select(Quote))}";

    private static string Quote(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static bool IsCommandMissing(AgentShellCommandResult result, string commandName)
        => result.ExitCode == 127
           || result.Output.Contains($"{commandName}: not found", StringComparison.OrdinalIgnoreCase)
           || result.Output.Contains($"{commandName}: command not found", StringComparison.OrdinalIgnoreCase)
           || result.Output.Contains("executable file not found", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<FileGrepMatch> ParseGrepResults(string output)
    {
        var matches = new List<FileGrepMatch>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var firstColon = line.IndexOf(':');
            var secondColon = firstColon < 0 ? -1 : line.IndexOf(':', firstColon + 1);
            if (firstColon >= 0
                && secondColon >= 0
                && int.TryParse(line[(firstColon + 1)..secondColon], out var lineNumber))
            {
                matches.Add(new FileGrepMatch(line[..firstColon], lineNumber, line[(secondColon + 1)..]));
            }
        }

        return matches;
    }

    private static string FormatGrepResults(IReadOnlyList<FileGrepMatch> matches, string fallbackOutput, bool wasTruncated)
    {
        var content = matches.Count == 0
            ? fallbackOutput
            : string.Join(Environment.NewLine, matches.Select(match => $"{match.Path}:{match.LineNumber}: {match.Text}"));
        return wasTruncated && !string.IsNullOrWhiteSpace(content)
            ? content + Environment.NewLine + $"[Results truncated at {MaxResults} matches]"
            : content;
    }

    private static IReadOnlyList<string> BuildFindPathPatterns(string path, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return [];
        }

        var basePath = NormalizeFindBasePath(path);
        var normalizedPattern = pattern.Trim().Replace('\\', '/');
        return ExpandDoubleStarZeroDirectoryVariants(normalizedPattern)
            .Select(candidate => PrefixFindPathPattern(basePath, candidate))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<string> ExpandDoubleStarZeroDirectoryVariants(string pattern)
    {
        var pending = new Queue<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { pattern };
        pending.Enqueue(pattern);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            yield return current;
            var index = current.IndexOf("**/", StringComparison.Ordinal);
            if (index >= 0 && seen.Add(current.Remove(index, 3)))
            {
                pending.Enqueue(current.Remove(index, 3));
            }
        }
    }

    private static string NormalizeFindBasePath(string path)
    {
        var normalized = string.IsNullOrWhiteSpace(path) ? "." : path.Trim().Replace('\\', '/');
        while (normalized.Length > 1 && normalized.EndsWith("/", StringComparison.Ordinal))
        {
            normalized = normalized[..^1];
        }

        return string.IsNullOrWhiteSpace(normalized) ? "." : normalized;
    }

    private static string PrefixFindPathPattern(string basePath, string pattern)
    {
        if (pattern.StartsWith("/", StringComparison.Ordinal) || pattern.StartsWith("./", StringComparison.Ordinal))
        {
            return pattern;
        }

        return basePath switch
        {
            "." => "./" + pattern,
            "/" => "/" + pattern,
            _ => basePath + "/" + pattern,
        };
    }

    private static bool MatchesGlobPattern(string pattern, string path, string candidate)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(candidate))
        {
            return true;
        }

        var normalizedPattern = pattern.Trim().Replace('\\', '/');
        var normalizedCandidate = candidate.Trim().Replace('\\', '/');
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

internal sealed record FileProcessCommand(string FileName, IReadOnlyList<string> Arguments);
