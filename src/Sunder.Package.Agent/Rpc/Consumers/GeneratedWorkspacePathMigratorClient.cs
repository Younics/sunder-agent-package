using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Workspace_Path_Migrator;
using Wire = Sunder.Generated.Rpc.WorkspacePathMigrator;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal sealed class GeneratedWorkspacePathMigratorClient(ISunderRpcClient rpc, SunderRpcProviderSnapshot provider)
    : IAgentWorkspacePathMigrationContributor, IDisposable, IAsyncDisposable
{
    private readonly GeneratedProviderCallLifetime _calls = new(rpc, provider, "agent.workspace.migrator-stale");
    private string? _contributorId;

    public string ContributorId
    {
        get
        {
            _calls.ThrowIfStopped();
            return _contributorId ??= _calls.Invoke(async (scope, token) => (await new PackageApi(scope).WorkspacePathMigrator
                .CreateClient(scope, new Api.Provider(provider)).SWorkspace_2dpath_2dmigrator.DescribeAsync(new(), token)
                .ConfigureAwait(false)).Id, default).AsTask().GetAwaiter().GetResult();
        }
    }

    public bool CanMigrate(AgentWorkspacePathMigrationContext context)
        => _calls.Invoke((scope, token) => new PackageApi(scope).WorkspacePathMigrator
            .CreateClient(scope, new Api.Provider(provider)).SWorkspace_2dpath_2dmigrator.CanMigrateAsync(
                AgentConsumerValues.Convert<Wire.AgentWorkspacePathMigrationContext>(context), token), default)
            .AsTask().GetAwaiter().GetResult();

    public async Task<IReadOnlyList<AgentWorkspacePathMigrationItem>> GetLegacyWorkspacePathsAsync(
        AgentWorkspacePathMigrationContext context, CancellationToken cancellationToken = default)
        => AgentConsumerValues.Convert<AgentWorkspacePathMigrationItem[]>(await _calls.Invoke((scope, token) => new PackageApi(scope).WorkspacePathMigrator
            .CreateClient(scope, new Api.Provider(provider)).SWorkspace_2dpath_2dmigrator.ListLegacyPathsAsync(
                AgentConsumerValues.Convert<Wire.AgentWorkspacePathMigrationContext>(context), token), cancellationToken).ConfigureAwait(false));

    public async Task CompleteWorkspacePathMigrationAsync(
        AgentWorkspacePathMigrationContext context, CancellationToken cancellationToken = default)
        => _ = await _calls.Invoke((scope, token) => new PackageApi(scope).WorkspacePathMigrator
            .CreateClient(scope, new Api.Provider(provider)).SWorkspace_2dpath_2dmigrator.CompleteAsync(
                AgentConsumerValues.Convert<Wire.AgentWorkspacePathMigrationContext>(context), token), cancellationToken).ConfigureAwait(false);

    public void Dispose() => _calls.Dispose();
    public ValueTask DisposeAsync() => _calls.DisposeAsync();
}
