using System.Text.Json.Serialization;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Describes the durable, stable identity and expectation for one resource in a tool invocation.
/// </summary>
/// <remarks>
/// Claims are data, not authority. They may be fingerprinted and persisted. Process-local capability
/// tokens and retained handles must never be placed in a claim.
/// </remarks>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record AgentResourceClaim(
    [property: SunderValueConstraints(Minimum = "0")] int Version,
    string NamespaceId,
    string LogicalPath,
    string? ConfiguredRoot,
    [property: SunderValueConstraints(MinLength = 64, MaxLength = 64)] string RootIdentityChainFingerprint,
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
    [property: SunderValueConstraints(Minimum = "-1")] int ResourceIndex,
    string ToolOwnerPackageId,
    string ExecutionTargetOwnerPackageId);

/// <summary>
/// Binds transient resource authority to one exact process-local tool activation and invocation.
/// </summary>
/// <remarks>
/// This context is intentionally transient. <see cref="AuthorityActivationId"/> must not be copied
/// into durable claims, permission fingerprints, or persisted permission records.
/// </remarks>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record AgentResourceOperationContext(
    Guid RunId,
    [property: SunderValueConstraints(Minimum = "0")] long RunRevision,
    string ToolCallId,
    string ActionId,
    [property: SunderValueConstraints(Minimum = "-1")] int ResourceIndex,
    string WorkspaceGeneration,
    string BindingGeneration,
    string ToolOwnerPackageId,
    string ExecutionTargetOwnerPackageId,
    [property: JsonIgnore]
    [property: SunderServiceMember] string AuthorityActivationId,
    [property: SunderValueConstraints(Minimum = "0")] int AuthorityUseCount = 1,
    bool CanIssueOutsideAuthority = false);
