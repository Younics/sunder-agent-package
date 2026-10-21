using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;

namespace Sunder.Package.Agent.Runtime;

internal static class AgentWorkspaceDefaults
{
    internal const string UnassignedSessionsWorkspaceId = "unassigned-sessions";

    internal static string BuildPrimaryBindingId(
        string workspaceId,
        string role = AgentWorkspaceBindingRoles.PrimaryExecutionTarget)
        => $"{workspaceId}:{role}";
}
