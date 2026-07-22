namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Maps a canonical path in an execution target's namespace to the corresponding canonical physical host path.
/// </summary>
/// <remarks>
/// A mapping is descriptive and does not grant file permission. Producers must account for symbolic links and mount traversal when computing
/// containment. Security-sensitive consumers must reject mappings for which <paramref name="IsInsideAllowedRoot"/> is <see langword="false"/>
/// and must still tolerate the mapped file changing after resolution.
/// </remarks>
/// <param name="ExecutionPath">The normalized target-visible path that was mapped.</param>
/// <param name="HostPath">The normalized physical host path corresponding to <paramref name="ExecutionPath"/>.</param>
/// <param name="IsInsideAllowedRoot">
/// Whether the physical host path is the configured root itself or is contained beneath an allowed host root after canonicalization.
/// </param>
public sealed record AgentExecutionPathMapping(
    string ExecutionPath,
    string HostPath,
    bool IsInsideAllowedRoot);
