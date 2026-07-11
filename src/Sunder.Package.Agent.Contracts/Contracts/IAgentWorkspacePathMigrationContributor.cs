using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

public interface IAgentWorkspacePathMigrationContributor
{
    string ContributorId { get; }

    bool CanMigrate(AgentWorkspacePathMigrationContext context);

    Task<IReadOnlyList<AgentWorkspacePathMigrationItem>> GetLegacyWorkspacePathsAsync(
        AgentWorkspacePathMigrationContext context,
        CancellationToken cancellationToken = default);

    Task CompleteWorkspacePathMigrationAsync(
        AgentWorkspacePathMigrationContext context,
        CancellationToken cancellationToken = default);
}
