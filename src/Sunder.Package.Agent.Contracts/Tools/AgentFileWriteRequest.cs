namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Requests creation or replacement of a text file through an execution target.
/// </summary>
/// <remarks>
/// The target resolves and revalidates <paramref name="Path" /> against its configured scope. A
/// canceled operation is not a rollback guarantee; implementations must use their strongest
/// available atomic-write behavior and report whether the mutation completed.
/// </remarks>
/// <param name="Path">The workspace-relative or execution-target-resolvable destination path.</param>
/// <param name="Content">The complete text content to write.</param>
/// <param name="Overwrite">Whether an existing regular file may be replaced.</param>
public sealed record AgentFileWriteRequest(
    string Path,
    string Content,
    bool Overwrite = true)
{
    /// <summary>
    /// Gets an optional lowercase SHA-256 hash of the expected current file content.
    /// </summary>
    /// <remarks>
    /// When supplied, the target performs a compare-and-swap check immediately before mutation and
    /// fails without writing if the current regular file does not match. The execution target defines
    /// whether its content representation is decoded text or raw bytes.
    /// </remarks>
    public string? ExpectedContentHash { get; init; }
}
