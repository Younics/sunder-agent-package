using System.Text;
using System.Text.RegularExpressions;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Agent.Execution.Common;

internal static class HostSecureFileSearch
{
    private const int MaxResults = 1000;
    private const int MaxOutputCharacters = 51_200;
    private const int MaxExpressionCharacters = 4096;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    public static async ValueTask<AgentFileSearchResult> ExecuteAsync(
        HostFileSystemPathContext pathContext,
        AgentFileSearchRequest request,
        CancellationToken cancellationToken,
        ILocalSecureFileSystemHooks? hooks = null)
    {
        using var approvedAuthority = pathContext.ApprovedAuthority;
        if (!TryValidateRequest(request, out var validationError))
        {
            return AgentFileSearchResult.Failure(
                AgentFileSearchErrorCodes.InvalidRequest,
                validationError!);
        }

        Regex? regex = null;
        if (request.Kind == AgentFileSearchKind.Grep)
        {
            try
            {
                regex = new Regex(
                    request.Pattern,
                    RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                    RegexTimeout);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return AgentFileSearchResult.Failure(AgentFileSearchErrorCodes.InvalidRequest, ex.Message);
            }
        }

        try
        {
            using var session = HostSecurePathEngine.OpenAuthorized(
                pathContext.ConfiguredRoots,
                pathContext.HostPath,
                approvedAuthority,
                createParents: false,
                hooks,
                cancellationToken);
            using var target = session.OpenTarget();
            var state = new SearchState(
                request,
                regex,
                pathContext.ReportedPath,
                pathContext.ReportedPathStyle,
                cancellationToken);
            if (target.Handle.Kind == LocalSecureNodeKind.RegularFile)
            {
                await SearchFileAsync(
                    target.Handle,
                    Path.GetFileName(pathContext.HostPath),
                    pathContext.ReportedPath,
                    state,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SearchDirectoryAsync(
                    session,
                    target.Handle,
                    relativeDirectory: string.Empty,
                    state,
                    depth: 0,
                    cancellationToken).ConfigureAwait(false);
            }
            return new AgentFileSearchResult(state.Matches, state.WasTruncated);
        }
        catch (LocalSecurePathNotFoundException ex)
        {
            return AgentFileSearchResult.Failure(AgentFileSearchErrorCodes.PathNotFound, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return AgentFileSearchResult.Failure(AgentFileSearchErrorCodes.OutsideConfiguredScope, ex.Message);
        }
        catch (RegexMatchTimeoutException ex)
        {
            return AgentFileSearchResult.Failure(
                AgentFileSearchErrorCodes.InvalidRequest,
                $"The grep regular expression exceeded its match-time bound: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return AgentFileSearchResult.Failure(AgentFileSearchErrorCodes.PathUnresolvable, ex.Message);
        }
    }

    internal static bool TryValidateRequest(
        AgentFileSearchRequest request,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(request.Path)
            || string.IsNullOrWhiteSpace(request.Pattern)
            || request.Pattern.Length > MaxExpressionCharacters
            || request.Include?.Length > MaxExpressionCharacters
            || !Enum.IsDefined(request.Kind)
            || (request.Kind == AgentFileSearchKind.Glob
                && !LocalGlobMatcher.IsWithinExpansionBounds(request.Pattern))
            || (request.Kind == AgentFileSearchKind.Grep
                && request.Include is not null
                && !LocalGlobMatcher.IsWithinExpansionBounds(request.Include)))
        {
            error = "A structured file search requires a path, search kind, and bounded non-empty expression.";
            return false;
        }

        if (request.Kind == AgentFileSearchKind.Grep)
        {
            try
            {
                _ = new Regex(
                    request.Pattern,
                    RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                    RegexTimeout);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                error = ex.Message;
                return false;
            }
        }

        error = null;
        return true;
    }

    private static async Task SearchDirectoryAsync(
        LocalSecurePathSession session,
        LocalSecureHandle directory,
        string relativeDirectory,
        SearchState state,
        int depth,
        CancellationToken cancellationToken)
    {
        foreach (var name in session.EnumerateRawNames(directory))
        {
            state.VisitEntry(depth, name);
            if (state.WasTruncated)
            {
                return;
            }
            if (HostSecurePathEngine.IsReservedName(name))
            {
                continue;
            }
            using var child = session.OpenChild(directory, name);
            var relativePath = string.IsNullOrEmpty(relativeDirectory)
                ? name
                : Path.Combine(relativeDirectory, name);
            if (child.Kind == LocalSecureNodeKind.Directory)
            {
                await SearchDirectoryAsync(
                    session,
                    child,
                    relativePath,
                    state,
                    depth + 1,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            await SearchFileAsync(
                child,
                relativePath,
                state.GetReportedPath(relativePath),
                state,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SearchFileAsync(
        LocalSecureHandle file,
        string relativePath,
        string reportedPath,
        SearchState state,
        CancellationToken cancellationToken)
    {
        if (state.Request.Kind == AgentFileSearchKind.Glob)
        {
            if (LocalGlobMatcher.Matches(
                    state.Request.Pattern,
                    relativePath,
                    state.GlobBudget,
                    state.UsesPosixPaths))
            {
                state.Add(new AgentFileSearchMatch(reportedPath));
            }
            return;
        }
        if (!string.IsNullOrWhiteSpace(state.Request.Include)
            && !LocalGlobMatcher.Matches(
                state.Request.Include,
                relativePath,
                state.GlobBudget,
                state.UsesPosixPaths))
        {
            return;
        }

        await using var stream = new FileStream(file.DuplicateHandle(), FileAccess.Read, 64 * 1024, isAsync: false);
        if (await IsBinaryAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        if (!state.TryReserveScan(stream.Length))
        {
            return;
        }

        var fileMatches = new List<AgentFileSearchMatch>();
        var fileOutputCharacters = 0;
        var fileWasTruncated = false;
        var scan = await HostBoundedLineReader.ScanAsync(
            stream,
            HostFileSystemLimits.MaxSearchFileBytes,
            HostFileSystemLimits.MaxLineCharacters,
            throwOnInvalidBytes: true,
            (lineNumber, line) =>
            {
                if (!state.Regex!.IsMatch(line))
                {
                    return;
                }
                var match = new AgentFileSearchMatch(reportedPath, lineNumber, line);
                var characters = SearchState.CountCharacters(match);
                if (fileMatches.Count >= MaxResults
                    || fileOutputCharacters + characters > MaxOutputCharacters)
                {
                    fileWasTruncated = true;
                    return;
                }
                fileMatches.Add(match);
                fileOutputCharacters += characters;
            },
            cancellationToken).ConfigureAwait(false);

        if (scan.ContainsNull || scan.InvalidEncoding)
        {
            return;
        }
        foreach (var match in fileMatches)
        {
            state.Add(match);
            if (state.WasTruncated)
            {
                return;
            }
        }
        if (fileWasTruncated || scan.ExceededByteLimit || scan.ExceededLineLimit)
        {
            state.MarkTruncated();
        }
    }

    private static async Task<bool> IsBinaryAsync(FileStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(8192, checked((int)Math.Min(stream.Length, 8192)))];
        stream.Position = 0;
        var read = buffer.Length == 0
            ? 0
            : await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        return buffer.AsSpan(0, read).Contains((byte)0);
    }

    private sealed class SearchState(
        AgentFileSearchRequest request,
        Regex? regex,
        string rootPath,
        HostReportedPathStyle reportedPathStyle,
        CancellationToken cancellationToken)
    {
        private int _outputCharacters;
        private long _scannedBytes;
        private readonly HostTraversalBudget _traversalBudget = new(cancellationToken);

        public AgentFileSearchRequest Request { get; } = request;

        public Regex? Regex { get; } = regex;

        public LocalGlobMatcher.LocalGlobMatchBudget GlobBudget { get; } = new(cancellationToken);

        public bool UsesPosixPaths { get; } = reportedPathStyle == HostReportedPathStyle.Posix;

        public string RootPath { get; } = rootPath;

        public string GetReportedPath(string relativePath)
            => reportedPathStyle == HostReportedPathStyle.Posix
                ? RootPath == "/"
                    ? "/" + relativePath
                    : RootPath.TrimEnd('/') + "/" + relativePath
                : Path.Combine(RootPath, relativePath);

        public List<AgentFileSearchMatch> Matches { get; } = [];

        public bool WasTruncated { get; private set; }

        public static int CountCharacters(AgentFileSearchMatch match)
            => match.Path.Length + (match.Text?.Length ?? 0) + 32;

        public void Add(AgentFileSearchMatch match)
        {
            var characters = CountCharacters(match);
            if (Matches.Count >= MaxResults || _outputCharacters + characters > MaxOutputCharacters)
            {
                WasTruncated = true;
                return;
            }
            Matches.Add(match);
            _outputCharacters += characters;
        }

        public void MarkTruncated() => WasTruncated = true;

        public void VisitEntry(int depth, string name)
            => _traversalBudget.Visit(depth, name);

        public bool TryReserveScan(long bytes)
        {
            if (bytes < 0
                || bytes > HostFileSystemLimits.MaxSearchFileBytes
                || _scannedBytes > HostFileSystemLimits.MaxSearchTotalBytes - bytes)
            {
                WasTruncated = true;
                return false;
            }
            _scannedBytes += bytes;
            return true;
        }
    }
}
