namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>Identifies the structured Files search operation owned by an execution target.</summary>
public enum AgentFileSearchKind
{
    /// <summary>Search regular text-file lines with a regular expression.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("grep")] Grep = 0,

    /// <summary>Search regular file paths with a glob pattern.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("glob")] Glob = 1,
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
    string? Include = null)
{
    /// <summary>Gets whether grep treats the pattern as literal text rather than a bounded regular expression.</summary>
    public bool Literal { get; init; }
    /// <summary>Gets whether grep uses ordinal case-sensitive matching.</summary>
    public bool? CaseSensitive { get; init; }
    /// <summary>Gets whether glob wildcards may match hidden path segments. Explicit dot segments still match.</summary>
    public bool Hidden { get; init; }
    /// <summary>Gets the requested result count, bounded to 1–1,000 by the execution target.</summary>
    [Sunder.Sdk.Services.SunderValueConstraints(Minimum = "1", Maximum = "1000")]
    public int? Limit { get; init; }
}

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

    /// <summary>Gets the first bound that prevented a complete search, or null for complete results.</summary>
    [Sunder.Sdk.Services.SunderValueConstraints(AllowedStrings = ["result-limit", "output-limit", "scan-limit", "line-limit"])]
    public string? TruncationReason { get; init; }

    /// <summary>Creates a failed search with no matches.</summary>
    public static AgentFileSearchResult Failure(string errorCode, string errorMessage)
        => new([])
        {
            IsError = true,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
        };
}
