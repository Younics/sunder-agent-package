namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Requests deletion of a file or directory through an execution target.
/// </summary>
/// <remarks>
/// The target resolves and revalidates <paramref name="Path" /> against its configured scope.
/// Cancellation is not a rollback guarantee once deletion has occurred.
/// </remarks>
/// <param name="Path">The workspace-relative or execution-target-resolvable path to delete.</param>
/// <param name="Recursive">Whether a directory and all of its descendants may be removed.</param>
public sealed record AgentFileDeleteRequest(string Path, bool Recursive = false)
{
    /// <summary>
    /// Gets an optional lowercase SHA-256 hash of the expected current file content.
    /// </summary>
    /// <remarks>
    /// When supplied, deletion fails without mutation unless the target is a matching regular file;
    /// content hashes do not authorize directory deletion. The execution target defines whether its
    /// content representation is decoded text or raw bytes.
    /// </remarks>
    public string? ExpectedContentHash { get; init; }
}
