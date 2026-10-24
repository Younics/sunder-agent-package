using System.Text;
using System.Text.RegularExpressions;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Agent.Execution.Common;

internal static class HostSecureFileSearch
{
    private const int MaxOutputCharacters = 51_200;

    public static async ValueTask<AgentFileSearchResult> ExecuteAsync(
        HostFileSystemPathContext pathContext,
        AgentFileSearchRequest request,
        CancellationToken cancellationToken,
        ILocalSecureFileSystemHooks? hooks = null)
    {
        using var approvedAuthority = pathContext.ApprovedAuthority;
        var callerCancellation = cancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        cancellationToken = deadline.Token;
        if (!TryValidateRequest(request, out var validationError))
        {
            return FileOperation.SearchFailure(
                AgentFileSearchErrorCodes.InvalidRequest,
                validationError!);
        }

        Regex? regex = null;
        if (request.Kind == AgentFileSearchKind.Grep)
        {
            try
            {
                regex = FileSearchRequestValidation.CreateGrepRegex(request.Pattern, request.Literal, request.CaseSensitive != false);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return FileOperation.SearchFailure(AgentFileSearchErrorCodes.InvalidRequest, ex.Message);
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
            return FileOperation.SearchResult(state.Matches, state.WasTruncated, state.TruncationReason);
        }
        catch (OperationCanceledException) when (!callerCancellation.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return FileOperation.SearchFailure("file-search-timeout", "Search exceeded its 30-second deadline. Narrow the path, include filter, or pattern; no incomplete matches were returned.");
        }
        catch (LocalSecurePathNotFoundException ex)
        {
            return FileOperation.SearchFailure(AgentFileSearchErrorCodes.PathNotFound, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return FileOperation.SearchFailure(AgentFileSearchErrorCodes.OutsideConfiguredScope, ex.Message);
        }
        catch (RegexMatchTimeoutException ex)
        {
            return FileOperation.SearchFailure(
                AgentFileSearchErrorCodes.InvalidRequest,
                $"The grep regular expression exceeded its match-time bound: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return FileOperation.SearchFailure(AgentFileSearchErrorCodes.PathUnresolvable, ex.Message);
        }
    }

    internal static bool TryValidateRequest(
        AgentFileSearchRequest request,
        out string? error)
        => FileSearchRequestValidation.TryValidateRequest(request, out error);

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
            var relativePath = string.IsNullOrEmpty(relativeDirectory)
                ? name
                : Path.Combine(relativeDirectory, name);
            var matches = state.Glob?.Matches(relativePath, state.GlobBudget) ?? true;
            var descend = state.Glob?.MayMatchDescendants(relativePath, state.GlobBudget) ?? true;
            if (!matches && !descend) continue;
            LocalSecureHandle child;
            try { child = session.OpenChild(directory, name); }
            catch (LocalSecureSymbolicLinkException) { continue; }
            catch (LocalSecurePathNotFoundException) { continue; } // An enumerated entry may disappear; the requested root must still exist.
            using var ownedChild = child;
            if (child.Kind == LocalSecureNodeKind.Directory)
            {
                if (matches && state.Request.Kind == AgentFileSearchKind.Glob && state.Glob!.DirectoriesOnly)
                    state.AddDirectory(relativePath);
                if (!descend || state.WasTruncated) continue;
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
            if (!state.Glob!.DirectoriesOnly && state.Glob.Matches(relativePath, state.GlobBudget))
            {
#if SUNDER_AGENT_PACKAGE_MODELS
                state.Add(new AgentFileSearchMatch { Path = reportedPath, LineNumber = null, Text = null });
#else
                state.Add(new AgentFileSearchMatch(reportedPath));
#endif
            }
            return;
        }
        if (state.Glob is not null && !state.Glob.Matches(relativePath, state.GlobBudget))
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
#if SUNDER_AGENT_PACKAGE_MODELS
                var match = new AgentFileSearchMatch { Path = reportedPath, LineNumber = lineNumber, Text = line };
#else
                var match = new AgentFileSearchMatch(reportedPath, lineNumber, line);
#endif
                var characters = SearchState.CountCharacters(match);
                if (fileMatches.Count >= state.ResultLimit + 1
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
            state.MarkTruncated(scan.ExceededLineLimit ? "line-limit" : fileWasTruncated ? "output-limit" : "scan-limit");
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

        private LocalGlobMatcher.CompiledPattern? _glob;
        public LocalGlobMatcher.CompiledPattern? Glob => Request.Kind == AgentFileSearchKind.Glob || !string.IsNullOrWhiteSpace(Request.Include)
            ? _glob ??= LocalGlobMatcher.Compile(Request.Kind == AgentFileSearchKind.Glob ? Request.Pattern : Request.Include!, GlobBudget, UsesPosixPaths,
                includeHidden: Request.Kind != AgentFileSearchKind.Glob || Request.Hidden)
            : null;

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
        public string? TruncationReason { get; private set; }
        public int ResultLimit => checked((int)(Request.Limit ?? FileSearchRequestValidation.DefaultResults));

        public static int CountCharacters(AgentFileSearchMatch match)
            => match.Path.Length + (match.Text?.Length ?? 0) + 32;

        public void Add(AgentFileSearchMatch match)
        {
            var characters = CountCharacters(match);
            if (Matches.Count >= ResultLimit || _outputCharacters + characters > MaxOutputCharacters)
            {
                MarkTruncated(Matches.Count >= ResultLimit ? "result-limit" : "output-limit");
                return;
            }
            Matches.Add(match);
            _outputCharacters += characters;
        }

        public void MarkTruncated(string reason)
        {
            WasTruncated = true;
            TruncationReason ??= reason;
        }

        public void AddDirectory(string relativePath)
        {
            var reported = GetReportedPath(relativePath);
#if SUNDER_AGENT_PACKAGE_MODELS
            Add(new AgentFileSearchMatch { Path = reported, LineNumber = null, Text = null });
#else
            Add(new AgentFileSearchMatch(reported));
#endif
        }

        public void VisitEntry(int depth, string name)
            => _traversalBudget.Visit(depth, name);

        public bool TryReserveScan(long bytes)
        {
            if (bytes < 0
                || bytes > HostFileSystemLimits.MaxSearchFileBytes
                || _scannedBytes > HostFileSystemLimits.MaxSearchTotalBytes - bytes)
            {
                MarkTruncated("scan-limit");
                return false;
            }
            _scannedBytes += bytes;
            return true;
        }
    }
}
