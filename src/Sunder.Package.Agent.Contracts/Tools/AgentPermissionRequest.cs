using System.Text.Json.Serialization;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Classifies a concrete tool invocation for permission policy evaluation and durable approval.
/// </summary>
/// <remarks>
/// A request describes an operation; it does not grant permission. Security-relevant fields are
/// persisted and fingerprinted so the host can reject execution when the tool, arguments, workspace,
/// binding, provider, model, or resolved resource changes after approval. User-facing fields may be
/// written to logs and approval UI and must not contain credentials or unredacted secret values.
/// </remarks>
/// <param name="ActionId">The stable action identifier published by a permission surface.</param>
/// <param name="BoundaryId">The stable boundary classification within the action.</param>
/// <param name="Summary">A concise explanation of the exact operation presented for approval.</param>
/// <param name="ToolId">The canonical tool identifier, when available.</param>
/// <param name="Command">The command text used for shell approval context, when applicable.</param>
/// <param name="Path">The requested path used for file approval context, when applicable.</param>
/// <param name="WorkspaceId">The workspace whose resources the invocation targets.</param>
/// <param name="BindingId">The selected execution binding that will perform the operation.</param>
/// <param name="ResourceDisplayName">An optional user-facing name for the resolved resource.</param>
/// <param name="ResourceReference">An optional canonical, non-secret resource identity used to bind approval.</param>
/// <param name="IsMutation">Whether the invocation can change external or persisted state; this is audit metadata, not authorization.</param>
public sealed record AgentPermissionRequest(
    string ActionId,
    string BoundaryId,
    string Summary,
    string? ToolId = null,
    string? Command = null,
    string? Path = null,
    string? WorkspaceId = null,
    string? BindingId = null,
    string? ResourceDisplayName = null,
    string? ResourceReference = null,
    bool IsMutation = false)
{
    /// <summary>Gets how the execution target established the resource boundary.</summary>
    public AgentPermissionScopeClassificationBasis ScopeClassificationBasis { get; init; }

    /// <summary>Gets every structured durable resource claim covered by this operation.</summary>
    public IReadOnlyList<AgentResourceClaim> ResourceClaims { get; init; } = [];

    /// <summary>Gets transient process-local authority references for immediate execution.</summary>
    /// <remarks>These values are deliberately excluded from JSON snapshots and durable permission state.</remarks>
    [JsonIgnore]
    public IReadOnlyList<string> ResourceCapabilities { get; init; } = [];

    /// <summary>Gets every immutable canonical resource identity covered by this operation.</summary>
    /// <remarks>Multi-resource operations should prefer <see cref="ResourceClaims"/>. This list remains for compatibility.</remarks>
    public IReadOnlyList<string> ResourceReferences { get; init; } = [];
}
