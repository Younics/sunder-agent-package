namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Represents an immutable persisted snapshot of an Agent workspace and its configured context locations.
/// </summary>
/// <remarks>
/// The record normalizes null path and document collections to empty collections but does not clone non-null
/// collections. A catalog result is not live; consumers must re-query after a workspace-change notification.
/// </remarks>
/// <param name="WorkspaceId">The opaque, stable workspace identifier.</param>
/// <param name="DisplayName">The user-facing workspace name.</param>
/// <param name="Description">An optional user-authored workspace description.</param>
/// <param name="CreatedAtUtc">The UTC time at which the workspace was created.</param>
/// <param name="UpdatedAtUtc">The UTC time of the latest persisted workspace change.</param>
/// <param name="Paths">Configured host paths, or <see langword="null"/> to expose an empty collection. The supplied collection is retained.</param>
/// <param name="Documents">Configured workspace documents, or <see langword="null"/> to expose an empty collection. The supplied collection is retained.</param>
public sealed record AgentWorkspaceRecord(
    string WorkspaceId,
    string DisplayName,
    string? Description,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<AgentWorkspacePathRecord>? Paths = null,
    IReadOnlyList<AgentWorkspaceDocumentRecord>? Documents = null)
{
    /// <summary>
    /// Gets the configured host-path snapshots in persistence order. The collection must be treated as immutable.
    /// </summary>
    public IReadOnlyList<AgentWorkspacePathRecord> Paths { get; init; } = Paths ?? [];

    /// <summary>
    /// Gets the configured document snapshots in persistence order. The collection must be treated as immutable.
    /// </summary>
    public IReadOnlyList<AgentWorkspaceDocumentRecord> Documents { get; init; } = Documents ?? [];
}

/// <summary>
/// Describes one host path associated with a workspace.
/// </summary>
/// <remarks>A host path is configuration data and does not guarantee that an execution target can access it.</remarks>
/// <param name="PathId">The opaque, stable identifier of the path entry.</param>
/// <param name="WorkspaceId">The identifier of the owning workspace.</param>
/// <param name="HostPath">The path as interpreted on the host machine.</param>
/// <param name="IsDefault">Whether this is the preferred path when a consumer needs one workspace root.</param>
/// <param name="SortOrder">The ascending display order; equal values are secondarily ordered by path by the built-in store.</param>
/// <param name="CreatedAtUtc">The UTC time at which the path entry was created.</param>
/// <param name="UpdatedAtUtc">The UTC time at which the path entry was last changed.</param>
public sealed record AgentWorkspacePathRecord(
    string PathId,
    string WorkspaceId,
    string HostPath,
    bool IsDefault,
    int SortOrder,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Describes one document configured as workspace context.
/// </summary>
/// <param name="DocumentId">The opaque, stable identifier of the document entry.</param>
/// <param name="WorkspaceId">The identifier of the owning workspace.</param>
/// <param name="FilePath">The configured document path; its absolute or workspace-relative interpretation is defined by the consuming feature.</param>
/// <param name="SortOrder">The ascending display order; equal values are secondarily ordered by path by the built-in store.</param>
/// <param name="CreatedAtUtc">The UTC time at which the document entry was created.</param>
/// <param name="UpdatedAtUtc">The UTC time at which the document entry was last changed.</param>
public sealed record AgentWorkspaceDocumentRecord(
    string DocumentId,
    string WorkspaceId,
    string FilePath,
    int SortOrder,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
