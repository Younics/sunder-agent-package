using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Models;

internal sealed record AgentToolPermissionResolution(
    AgentToolDescriptor Descriptor,
    IAgentToolSource Source,
    AgentPermissionRequest? PermissionRequest,
    AgentWorkspaceBindingRecord? ExecutionBinding,
    AgentExecutionTargetDescriptor? ExecutionTarget,
    string? DeniedReason = null);

internal static class AgentToolSecurityErrorCodes
{
    public const string NotAdvertised = "tool-not-advertised";
    public const string PermissionContextChanged = "permission-context-changed";
    public const string PermissionContextInsufficient = "permission-context-insufficient";
}
