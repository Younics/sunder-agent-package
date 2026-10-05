using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Contracts.Models;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Workspace_Path_Migrator;

namespace Sunder.Package.Agent.Execution.Local.Rpc;

[SunderRpcProvider(typeof(IWorkspacePathMigratorService), ProviderId = "local.workspace.path.migrator")]
public sealed partial class WorkspacePathMigratorProvider(IAgentWorkspacePathMigrationContributor migrator) : IWorkspacePathMigratorService
{
    public ValueTask<Wire.AgentRpcIdentity> DescribeAsync(SunderRpcInvocationContext context, Wire.Unit request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new Wire.AgentRpcIdentity { Id = migrator.ContributorId, DisplayName = migrator.ContributorId }); }
    public ValueTask<bool> CanMigrateAsync(SunderRpcInvocationContext context, Wire.AgentWorkspacePathMigrationContext request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(migrator.CanMigrate(ExecutionValues.Convert<Domain.AgentWorkspacePathMigrationContext>(request))); }
    public async ValueTask<IReadOnlyList<Wire.AgentWorkspacePathMigrationItem>> ListLegacyPathsAsync(SunderRpcInvocationContext context, Wire.AgentWorkspacePathMigrationContext request, CancellationToken token)
        => ExecutionValues.Convert<Wire.AgentWorkspacePathMigrationItem[]>(await migrator.GetLegacyWorkspacePathsAsync(ExecutionValues.Convert<Domain.AgentWorkspacePathMigrationContext>(request), token).ConfigureAwait(false));
    public async ValueTask<Wire.Unit> CompleteAsync(SunderRpcInvocationContext context, Wire.AgentWorkspacePathMigrationContext request, CancellationToken token)
    { await migrator.CompleteWorkspacePathMigrationAsync(ExecutionValues.Convert<Domain.AgentWorkspacePathMigrationContext>(request), token).ConfigureAwait(false); return new(); }
}
