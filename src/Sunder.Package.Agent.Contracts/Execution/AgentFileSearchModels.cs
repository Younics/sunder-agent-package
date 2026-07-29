namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>Identifies the structured Files search operation owned by an execution target.</summary>
public enum AgentFileSearchKind
{
    /// <summary>Search regular text-file lines with a regular expression.</summary>
    Grep = 0,

    /// <summary>Search regular file paths with a glob pattern.</summary>
    Glob = 1,
}

/// <summary>Requests a bounded, target-owned structured file search.</summary>
/// <param name="Path">The file or directory at which search starts.</param>
/// <param name="Kind">The search operation.</param>
/// <param name="Pattern">The regular expression or glob pattern.</param>
/// <param name="Include">An optional grep file-include glob.</param>
public sealed record AgentFileSearchRequest(
    string Path,
    AgentFileSearchKind Kind,
    string Pattern,
    string? Include = null);

/// <summary>Contains one structured file-search match.</summary>
/// <param name="Path">The target-visible matched file path.</param>
/// <param name="LineNumber">The one-based line number for grep, or <see langword="null"/> for glob.</param>
/// <param name="Text">The complete matched line for grep, or <see langword="null"/> for glob.</param>
public sealed record AgentFileSearchMatch(
    string Path,
    int? LineNumber = null,
    string? Text = null);

/// <summary>Returns bounded structured matches or a structured search failure.</summary>
/// <param name="Matches">At most 1,000 complete matches.</param>
/// <param name="WasTruncated">Whether additional matches or output characters were omitted.</param>
public sealed record AgentFileSearchResult(
    IReadOnlyList<AgentFileSearchMatch> Matches,
    bool WasTruncated = false)
{
    /// <summary>Gets whether the search failed and no matches may be consumed.</summary>
    public bool IsError { get; init; }

    /// <summary>Gets the stable failure code.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Gets the bounded user-facing failure explanation.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Creates a failed search with no matches.</summary>
    public static AgentFileSearchResult Failure(string errorCode, string errorMessage)
        => new([])
        {
            IsError = true,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
        };
}

/// <summary>Defines stable execution-target search failure codes.</summary>
public static class AgentFileSearchErrorCodes
{
    /// <summary>The request was malformed or its expression was invalid.</summary>
    public const string InvalidRequest = "file-search-invalid";

    /// <summary>The search root was missing.</summary>
    public const string PathNotFound = "file-search-path-not-found";

    /// <summary>The search root was outside authorized scope.</summary>
    public const string OutsideConfiguredScope = "file-search-outside-scope";

    /// <summary>A no-follow path or filesystem-identity check failed.</summary>
    public const string PathUnresolvable = "file-search-path-unresolvable";

    /// <summary>The search backend failed after authorization.</summary>
    public const string SearchFailed = "file-search-failed";
}
