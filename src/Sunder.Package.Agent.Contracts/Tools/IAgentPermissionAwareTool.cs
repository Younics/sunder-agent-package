using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Allows an individually contributed tool to classify an invocation for permission evaluation.
/// </summary>
/// <remarks>
/// Permission planning occurs before execution and must not perform the protected operation. The
/// planner should parse and canonicalize the same security-relevant arguments as execution and fail
/// closed with an unknown or approval-required boundary when classification is uncertain. Returning
/// <see langword="null" /> means no tool-specific policy was supplied; it is not an authorization
/// grant, and the host applies a generic approval policy to tools declared as mutating.
/// </remarks>
public interface IAgentPermissionAwareTool
{
    /// <summary>Builds the permission action, boundary, and resource identity for an invocation.</summary>
    /// <param name="context">The host-verified session, workspace, execution binding, and run identity.</param>
    /// <param name="request">The selected tool and untrusted model-supplied arguments.</param>
    /// <param name="cancellationToken">A token that cancels parsing or resource classification.</param>
    /// <returns>A request for policy evaluation, or <see langword="null" /> when this tool has no specific permission requirement.</returns>
    ValueTask<AgentPermissionRequest?> BuildPermissionRequestAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default);
}
