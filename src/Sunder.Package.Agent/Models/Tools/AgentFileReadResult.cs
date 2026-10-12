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
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
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
