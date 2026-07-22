namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Contains a bounded text-file slice, directory listing, or structured read failure.
/// </summary>
/// <remarks>
/// Paths, content, and error text can be persisted and returned to the model. Consumers must treat
/// them as untrusted and potentially sensitive workspace data. A target must keep the returned data
/// stable after the operation completes.
/// </remarks>
/// <param name="Path">The target-resolved path, or the requested path when resolution failed.</param>
/// <param name="Content">Text content or newline-delimited directory entries; empty for failures.</param>
/// <param name="IsDirectory">Whether <paramref name="Content" /> is a directory listing rather than file text.</param>
/// <param name="WasTruncated">Whether entries or file content were omitted because a target limit was reached.</param>
public sealed record AgentFileReadResult(
    string Path,
    string Content,
    bool IsDirectory = false,
    bool WasTruncated = false)
{
    /// <summary>Gets whether the read failed and <see cref="Content" /> is not a successful payload.</summary>
    public bool IsError { get; init; }

    /// <summary>Gets the stable machine-readable failure code, or <see langword="null" /> for a successful read.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Gets the user-facing failure explanation, or <see langword="null" /> for a successful read.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Gets the one-based first file line represented in <see cref="Content" />, when line metadata applies.</summary>
    public int? StartLine { get; init; }

    /// <summary>Gets the one-based inclusive last file line represented in <see cref="Content" />, when line metadata applies.</summary>
    public int? EndLine { get; init; }

    /// <summary>Gets the total number of lines observed in the file, when known.</summary>
    public int? TotalLines { get; init; }

    /// <summary>Creates a structured failure with empty content.</summary>
    /// <param name="path">The resolved or requested path associated with the failure.</param>
    /// <param name="errorCode">The stable machine-readable failure code.</param>
    /// <param name="errorMessage">The user-facing failure explanation.</param>
    /// <returns>A result with <see cref="IsError" /> set and no content.</returns>
    public static AgentFileReadResult Failure(string path, string errorCode, string errorMessage)
        => new(path, string.Empty)
        {
            IsError = true,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
        };
}

/// <summary>
/// Defines common execution-target file-read failure codes.
/// </summary>
public static class AgentFileReadErrorCodes
{
    /// <summary>The resolved path does not exist.</summary>
    public const string FileNotFound = "file-not-found";

    /// <summary>The operation required a regular file but resolved another resource kind.</summary>
    public const string NotAFile = "file-not-regular";

    /// <summary>The target detected binary content where a text read was required.</summary>
    public const string BinaryFile = "file-binary";

    /// <summary>The resolved path is outside configured workspace scope without explicit authorization.</summary>
    public const string OutsideConfiguredScope = "file-outside-scope";

    /// <summary>The requested one-based offset or line limit is invalid.</summary>
    public const string InvalidRange = "file-range-invalid";

    /// <summary>The requested starting line is beyond the available file content.</summary>
    public const string RangeOutsideFile = "file-range-out-of-bounds";

    /// <summary>The target could not securely canonicalize and classify the requested path.</summary>
    public const string PathCanonicalizationFailed = "file-path-unresolvable";

    /// <summary>An I/O or access failure occurred after path resolution.</summary>
    public const string ReadFailed = "file-read-failed";

    /// <summary>The execution target's read timeout elapsed.</summary>
    public const string TimedOut = "file-read-timeout";

    /// <summary>A full read exceeded the target's byte limit and a ranged read is required.</summary>
    public const string TooLarge = "file-too-large";
}
