namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Requests creation or complete-content overwrite of a text file through an execution target.
/// </summary>
/// <remarks>
/// The target resolves and revalidates <paramref name="Path" /> against its configured scope. A
/// canceled operation is not a rollback guarantee. First-party host targets update existing
/// files in place to retain their ownership and access policy. Readers can observe intermediate
/// content, and interruption or I/O failure can leave partial content; results must distinguish
/// successful completion from an incomplete or uncertain mutation.
/// </remarks>
/// <param name="Path">The workspace-relative or execution-target-resolvable destination path.</param>
/// <param name="Content">The complete text content to write.</param>
/// <param name="Overwrite">Whether an existing regular file's contents may be overwritten.</param>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record AgentFileWriteRequest(
    string Path,
    string Content,
    bool Overwrite = true)
{
    /// <summary>
    /// Gets an optional lowercase SHA-256 hash of the expected current file content.
    /// </summary>
    /// <remarks>
    /// When supplied, the target checks the expected content immediately before mutation and fails
    /// without writing if the current regular file does not match. This does not exclude arbitrary
    /// external writers or writable mappings throughout a multi-step update. The execution target
    /// defines whether its content representation is decoded text or raw bytes.
    /// </remarks>
    [Sunder.Sdk.Services.SunderValueConstraints(MinLength = 64, MaxLength = 64)]
    public string? ExpectedContentHash { get; init; }
}
