using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderRpcService(SunderRpcExposure.Public, ContractId = "sunder.agent.permission.surface",
    ServiceId = "permission-surface", IncludeSchemaDialect = true)]
[SunderRpcRuntimeUse(Actions = ["discover", "invoke"])]
public interface PermissionSurfaceService
{
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<PermissionSurfaceSnapshot> DescribeAsync(Unit request, CancellationToken cancellationToken = default);
}

public enum AgentPermissionDecision
{
    [JsonStringEnumMemberName("allow")] Allow,
    [JsonStringEnumMemberName("ask")] Ask,
    [JsonStringEnumMemberName("deny")] Deny,
}

public sealed class AgentPermissionBoundaryDescriptor
{
    public required Identifier BoundaryId { get; init; }
    public required AgentPermissionDecision DefaultDecision { get; init; }
    public required DisplayText Description { get; init; }
    public required DisplayText DisplayName { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentPermissionBoundaryDescriptorCollection
{
    public required IReadOnlyList<AgentPermissionBoundaryDescriptor> Value { get; init; }
}

public sealed class AgentPermissionActionDescriptor
{
    public required Identifier ActionId { get; init; }
    public required AgentPermissionBoundaryDescriptorCollection Boundaries { get; init; }
    public required DisplayText Description { get; init; }
    public required DisplayText DisplayName { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentPermissionActionDescriptorCollection
{
    public required IReadOnlyList<AgentPermissionActionDescriptor> Value { get; init; }
}

public sealed class PermissionSurfaceSnapshot
{
    public required AgentPermissionActionDescriptorCollection Actions { get; init; }
    public required DisplayText DisplayName { get; init; }
    public required Identifier SurfaceId { get; init; }
}
