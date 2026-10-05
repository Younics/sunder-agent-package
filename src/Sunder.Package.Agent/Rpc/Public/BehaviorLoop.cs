using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Rpc;
using Mapping = Sunder.Package.Agent.Rpc.Public.Control.RunControlMapping;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderRpcService(SunderRpcExposure.Public, ContractId = "sunder.agent.behavior.loop",
    ServiceId = "behavior-loop", IncludeSchemaDialect = true)]
[SunderRpcProvider(ProviderId = "agent.behavior.loop")]
[SunderRpcRuntimeUse(Required = true, Actions = ["discover", "invoke"])]
public sealed partial class BehaviorLoopService(IAgentBehaviorLoop loop, Protocol.AgentRpcCatalog catalog)
{
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<AgentBehaviorLoopDescriptor> DescribeAsync(Unit request, CancellationToken cancellationToken = default)
    {
        var descriptor = loop.Descriptor;
        return ValueTask.FromResult(new AgentBehaviorLoopDescriptor
        {
            Description = descriptor.Description, DisplayName = descriptor.DisplayName, LoopId = descriptor.LoopId,
            FeatureKinds = Mapping.FromReference(descriptor.FeatureKinds, TextValues.FromDomain),
            SettingsSchemaJson = Mapping.FromReference(descriptor.SettingsSchemaJson, value => new JsonText { Value = value }),
            SourceId = Mapping.FromReference(descriptor.SourceId, value => (Identifier)value),
        });
    }

    [SunderRpcMethod]
    public async ValueTask<AgentBehaviorLoopResult> RunAsync(BehaviorLoopRunRequest request, CancellationToken cancellationToken = default)
        => Mapping.FromDomain(await loop.RunAsync(Mapping.ToDomain(request.Context),
            new Runtime.Rpc.GeneratedBehaviorRuntime(catalog, request.InvocationId.Value, request.RunControl.ToDomain()),
            cancellationToken).ConfigureAwait(false));
}

public sealed class AgentBehaviorLoopDescriptor
{
    public required DisplayText Description { get; init; }
    public required DisplayText DisplayName { get; init; }
    public SunderRpcOptional<TextValues> FeatureKinds { get; init; }
    public required Identifier LoopId { get; init; }
    public SunderRpcOptional<JsonText> SettingsSchemaJson { get; init; }
    public SunderRpcOptional<Identifier> SourceId { get; init; }
}

public sealed class BehaviorLoopRunRequest
{
    public required AgentBehaviorLoopContext Context { get; init; }
    public required EntityId InvocationId { get; init; }
    public required AgentRpcProviderHandle RunControl { get; init; }
}
