using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

public interface IAgentPermissionSurface
{
    string SurfaceId { get; }

    string DisplayName { get; }

    IReadOnlyList<AgentPermissionActionDescriptor> ListActions();
}
