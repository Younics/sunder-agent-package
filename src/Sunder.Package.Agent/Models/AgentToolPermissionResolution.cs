using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Models;

internal sealed record AgentToolPermissionResolution(
    AgentToolDescriptor Descriptor,
    AgentPermissionRequest? PermissionRequest,
    AgentWorkspaceBindingRecord? ExecutionBinding,
    AgentExecutionTargetDescriptor? ExecutionTarget,
    string OwnerPackageId,
    string? DeniedReason = null);

internal static class AgentToolSecurityErrorCodes
{
    public const string NotAdvertised = "tool-not-advertised";
    public const string AmbiguousOwnership = "tool-owner-ambiguous";
    public const string PermissionContextChanged = "permission-context-changed";
    public const string PermissionContextInsufficient = "permission-context-insufficient";
}
