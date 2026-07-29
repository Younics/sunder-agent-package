namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>Requests scoped instruction discovery for an ordered, bounded set of target-visible paths.</summary>
/// <param name="Probes">Paths to inspect. Callers should submit no more than 64 distinct paths per batch.</param>
public sealed record AgentScopedInstructionDiscoveryRequest(
    IReadOnlyList<AgentScopedInstructionProbe> Probes);

/// <summary>Identifies one target-visible path and whether it denotes a directory rather than a file target.</summary>
/// <param name="Path">An absolute target-visible path or a path relative to the target's default working directory.</param>
/// <param name="IsDirectory">Whether <paramref name="Path"/> itself is the applicable directory.</param>
public sealed record AgentScopedInstructionProbe(
    string Path,
    bool IsDirectory = false)
{
    /// <summary>
    /// Gets whether a compatibility target derives scope from a followed final link or its containing entry.
    /// Strict no-follow targets reject either form when the target is a link; Local does not use this flag to permit links.
    /// </summary>
    public bool FollowFinalSymbolicLink { get; init; } = true;
}

/// <summary>Returns scoped instruction discovery results and binding-sensitive target fingerprints.</summary>
/// <param name="TargetFingerprint">An opaque fingerprint of the selected target/container identity.</param>
/// <param name="ScopeFingerprint">An opaque fingerprint of the canonical configured workspace roots.</param>
/// <param name="Scopes">Canonical in-scope probe results. Out-of-scope probes are omitted.</param>
/// <param name="WasTruncated">Whether any requested probe, ancestor, document, or result was omitted by a bound.</param>
public sealed record AgentScopedInstructionDiscoveryResult(
    string TargetFingerprint,
    string ScopeFingerprint,
    IReadOnlyList<AgentScopedInstructionScope> Scopes,
    bool WasTruncated = false)
{
    /// <summary>Gets the number of requested probes the target conclusively processed, including out-of-scope probes.</summary>
    /// <remarks>Callers fail closed unless this exactly matches the request count.</remarks>
    public int ProcessedProbeCount { get; init; }
}

/// <summary>Describes one canonical configured-root-to-target instruction scope.</summary>
/// <param name="RequestedPath">The path supplied by the caller.</param>
/// <param name="TargetDirectory">The canonical target-visible directory claimed by this probe.</param>
/// <param name="ScopeRoot">The most-specific canonical configured workspace root containing the target directory.</param>
/// <param name="Documents">Exact <c>AGENTS.md</c> documents found on the bounded ancestor chain.</param>
/// <param name="WasTruncated">Whether ancestors or documents were omitted by a bound.</param>
/// <param name="OmittedAncestorCount">The number of root-side ancestors omitted when the 64-directory bound applied.</param>
public sealed record AgentScopedInstructionScope(
    string RequestedPath,
    string TargetDirectory,
    string ScopeRoot,
    IReadOnlyList<AgentScopedInstructionDocument> Documents,
    bool WasTruncated = false,
    int OmittedAncestorCount = 0);

/// <summary>Contains one bounded exact <c>AGENTS.md</c> document in the target's path namespace.</summary>
/// <param name="Path">The canonical target-visible document path.</param>
/// <param name="ScopeRoot">The canonical configured root that bounded discovery.</param>
/// <param name="AppliesToDirectory">The canonical directory subtree to which the content applies.</param>
/// <param name="Content">At most 12,000 characters of complete text content.</param>
/// <param name="ContentHash">A lowercase SHA-256 hash of the complete accepted content.</param>
/// <param name="WasTruncated">Compatibility metadata. First-party targets fail instead of returning partial content.</param>
public sealed record AgentScopedInstructionDocument(
    string Path,
    string ScopeRoot,
    string AppliesToDirectory,
    string Content,
    string ContentHash,
    bool WasTruncated = false);
