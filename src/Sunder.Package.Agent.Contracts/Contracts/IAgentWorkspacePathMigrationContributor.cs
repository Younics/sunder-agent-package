using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

public interface IAgentWorkspacePathMigrationContributor
{
    string ContributorId { get; }

    bool CanMigrate(AgentWorkspacePathMigrationContext context);

    IReadOnlyList<AgentWorkspacePathMigrationItem> GetLegacyWorkspacePaths(AgentWorkspacePathMigrationContext context);

    void CompleteWorkspacePathMigration(AgentWorkspacePathMigrationContext context);
}
