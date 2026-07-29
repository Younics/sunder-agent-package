using System.Text.Json.Serialization;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Describes the durable, stable identity and expectation for one resource in a tool invocation.
/// </summary>
/// <remarks>
/// Claims are data, not authority. They may be fingerprinted and persisted. Process-local capability
/// tokens and retained handles must never be placed in a claim.
/// </remarks>
public sealed record AgentResourceClaim(
    int Version,
    string NamespaceId,
    string LogicalPath,
    string? ConfiguredRoot,
    string RootIdentityChainFingerprint,
    bool TargetExists,
    string? TargetKind,
    string? TargetIdentity,
    bool TargetIsAuthorityRoot,
    bool CaseSensitivePath,
    string ActionId,
    string WorkspaceId,
    string WorkspaceGeneration,
    string BindingId,
    string BindingGeneration,
    string ToolCallId,
    int ResourceIndex,
    string ToolOwnerPackageId,
    string ExecutionTargetOwnerPackageId);

/// <summary>
/// Binds transient resource authority to one exact process-local tool activation and invocation.
/// </summary>
/// <remarks>
/// This context is intentionally transient. <see cref="AuthorityActivationId"/> must not be copied
/// into durable claims, permission fingerprints, or persisted permission records.
/// </remarks>
public sealed record AgentResourceOperationContext(
    Guid RunId,
    long RunRevision,
    string ToolCallId,
    string ActionId,
    int ResourceIndex,
    string WorkspaceGeneration,
    string BindingGeneration,
    string ToolOwnerPackageId,
    string ExecutionTargetOwnerPackageId,
    [property: JsonIgnore] string AuthorityActivationId,
    int AuthorityUseCount = 1,
    bool CanIssueOutsideAuthority = false);
