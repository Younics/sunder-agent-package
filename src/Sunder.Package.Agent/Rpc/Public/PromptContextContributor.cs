using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Rpc;
using Mapping = Sunder.Package.Agent.Rpc.Public.Control.RunControlMapping;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderRpcService(SunderRpcExposure.Public, ContractId = "sunder.agent.prompt.context.contributor",
    ServiceId = "prompt-context-contributor", IncludeSchemaDialect = true)]
[SunderRpcProvider(ProviderId = "agent.workspace.documentation")]
[SunderRpcRuntimeUse(Actions = ["discover", "invoke"])]
public sealed partial class PromptContextContributorService(IAgentPromptContextContributor contributor, Protocol.AgentRpcCatalog? catalog = null)
{
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<PromptContextContributorDescription> DescribeAsync(Unit request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new PromptContextContributorDescription
        {
            AcknowledgmentSinkId = Mapping.FromReference((contributor as IAgentPromptContextAcknowledgmentSink)?.AcknowledgmentSinkId, value => (Identifier)value),
            ContributorId = contributor.ContributorId, DisplayName = contributor.DisplayName,
        });

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public async ValueTask<PromptContextContributionResult> ContributeAsync(PromptContextRequestWire request, CancellationToken cancellationToken = default)
    {
        var result = await contributor.ContributeContextAsync(PromptContextMapping.ToDomain(request, catalog), cancellationToken).ConfigureAwait(false);
        return new()
        {
            Value = result is null ? null : new AgentPromptContextContribution
            { Blocks = new() { Value = result.Blocks.Select(Mapping.FromDomain).ToArray() } },
        };
    }

    [SunderRpcMethod]
    public async ValueTask<Unit> AcknowledgeAsync(AgentPromptContextReceipt request, CancellationToken cancellationToken = default)
    {
        if (contributor is IAgentPromptContextAcknowledgmentSink sink)
            await sink.AcknowledgePromptContextAsync(PromptContextMapping.ToDomain(request), cancellationToken).ConfigureAwait(false);
        return new();
    }
}

public sealed class PromptContextContributorDescription
{
    public SunderRpcOptional<Identifier> AcknowledgmentSinkId { get; init; }
    public required Identifier ContributorId { get; init; }
    public required DisplayText DisplayName { get; init; }
}

public sealed class AgentPromptContextContribution
{
    public required AgentPromptContextBlockCollection Blocks { get; init; }
}

[SunderRpcTransparent]
public sealed class PromptContextContributionResult
{
    public required AgentPromptContextContribution? Value { get; init; }
}
