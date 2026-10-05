namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Requests a bounded text-file read or directory listing through an execution target.
/// </summary>
/// <remarks>The target resolves and revalidates <paramref name="Path" /> against its configured resource scope.</remarks>
/// <param name="Path">The workspace-relative or execution-target-resolvable file or directory path.</param>
/// <param name="Offset">An optional one-based file line at which to start; it does not apply to directory listings.</param>
/// <param name="Limit">An optional maximum number of file lines; targets enforce their own upper bound and default.</param>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record AgentFileReadRequest(
    string Path,
    int? Offset = null,
    int? Limit = null);
