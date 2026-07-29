namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Advises how a path in an execution target's namespace maps to a host path at one point in time.
/// </summary>
/// <remarks>
/// A mapping is descriptive and does not grant file permission, retain a handle, or make later host path APIs safe. Security-sensitive consumers
/// must use a target-owned structured operation instead of opening or mutating <paramref name="HostPath"/>.
/// </remarks>
/// <param name="ExecutionPath">The normalized target-visible path that was mapped.</param>
/// <param name="HostPath">The normalized host path corresponding to <paramref name="ExecutionPath"/> at mapping time.</param>
/// <param name="IsInsideAllowedRoot">
/// Whether the physical host path is the configured root itself or is contained beneath an allowed host root after canonicalization.
/// </param>
public sealed record AgentExecutionPathMapping(
    string ExecutionPath,
    string HostPath,
    bool IsInsideAllowedRoot);
