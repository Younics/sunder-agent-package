using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Publishes permission actions and default boundaries owned by one extension surface.
/// </summary>
/// <remarks>
/// Action and boundary identifiers are persisted and must remain stable. Metadata is displayed to
/// users and must not contain secrets or invocation-specific untrusted content.
/// </remarks>
public interface IAgentPermissionSurface
{
    /// <summary>Gets the stable identifier of the surface that owns the action definitions.</summary>
    string SurfaceId { get; }

    /// <summary>Gets the surface name shown in permission settings.</summary>
    string DisplayName { get; }

    /// <summary>Lists the actions and fail-safe defaults controlled by the surface.</summary>
    /// <returns>A stable read-only snapshot with action identifiers unique across all surfaces.</returns>
    IReadOnlyList<AgentPermissionActionDescriptor> ListActions();
}
