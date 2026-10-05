using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderRpcService(SunderRpcExposure.Public, ContractId = "sunder.agent.workspace.path.migrator",
    ServiceId = "workspace-path-migrator", IncludeSchemaDialect = true)]
[SunderRpcRuntimeUse(Actions = ["discover", "invoke"])]
public interface WorkspacePathMigratorService
{
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<AgentRpcIdentity> DescribeAsync(Unit request, CancellationToken cancellationToken = default);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<Flag> CanMigrateAsync(AgentWorkspacePathMigrationContext request, CancellationToken cancellationToken = default);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<AgentWorkspacePathMigrationItemCollection> ListLegacyPathsAsync(AgentWorkspacePathMigrationContext request, CancellationToken cancellationToken = default);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<Unit> CompleteAsync(AgentWorkspacePathMigrationContext request, CancellationToken cancellationToken = default);
}

public sealed class AgentWorkspacePathMigrationContext
{
    public required AgentWorkspaceBindingRecord Binding { get; init; }
    public required AgentWorkspaceRecord Workspace { get; init; }
}

public sealed class AgentWorkspacePathMigrationItem
{
    public required PathText HostPath { get; init; }
    public required Flag IsDefault { get; init; }
    public required SortOrder SortOrder { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentWorkspacePathMigrationItemCollection
{
    public required IReadOnlyList<AgentWorkspacePathMigrationItem> Value { get; init; }
}
