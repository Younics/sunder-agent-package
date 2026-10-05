using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Contracts.Models;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Prompt_Context_Contributor;

namespace Sunder.Package.Agent.Memory.Semantic.Rpc;

[SunderRpcProvider(typeof(IPromptContextService), ProviderId = "semantic.memory.prompt.context")]
public sealed partial class PromptContextProvider(IAgentPromptContextContributor contributor) : IPromptContextService
{
    public ValueTask<Wire.PromptContextContributorDescription> DescribeAsync(SunderRpcInvocationContext context, Wire.Unit request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new Wire.PromptContextContributorDescription
        {
            ContributorId = contributor.ContributorId, DisplayName = contributor.DisplayName,
            AcknowledgmentSinkId = (contributor as IAgentPromptContextAcknowledgmentSink)?.AcknowledgmentSinkId,
        });
    }

    public async ValueTask<Wire.AgentPromptContextContribution?> ContributeAsync(SunderRpcInvocationContext context, Wire.PromptContextRequestWire request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var session = SemanticProviderValues.Convert<AgentSessionContextRecord>(request.Session);
        var run = SemanticProviderValues.Convert<AgentRunContextRecord>(request.Run);
        // ShouldContribute remains derived from the domain plan's Intent; the
        // caller's redundant flag cannot override that business decision.
        var plan = new Domain.AgentPromptContextPlan(request.ContextPlan.Intent, request.ContextPlan.QueryText,
            request.ContextPlan.Reason, request.ContextPlan.PreferredCategories,
            checked((int)request.ContextPlan.MaxEntryCount), checked((int)request.ContextPlan.MaxChars));
        var domain = new Domain.AgentPromptContextRequest(session, run,
            new AgentTurnContextRecord(session, run, request.Turn.UserMessage, request.Turn.WorkingSummary),
            SemanticProviderValues.Convert<Domain.AgentTurnRecord[]>(request.Turns),
            SemanticProviderValues.Convert<Domain.AgentTurnRecord[]>(request.RecentLiveBufferTurns), plan)
        {
            Profile = request.Profile is { } profile ? SemanticProviderValues.Convert<Domain.AgentProfileRecord>(profile) : null,
            Workspace = request.Workspace is { } workspace ? SemanticProviderValues.Convert<Domain.AgentWorkspaceRecord>(workspace) : null,
            ExecutionBinding = request.ExecutionBinding is { } binding ? SemanticProviderValues.Convert<Domain.AgentWorkspaceBindingRecord>(binding) : null,
            AvailableTools = SemanticProviderValues.Convert<Domain.AgentToolDescriptor[]>(request.AvailableTools),
            MemoryConsistencyBarrier = request.MemoryConsistencyBarrier is { } barrier ? new(barrier.EventId, barrier.PayloadHash) : null,
            TranscriptEpoch = request.TranscriptEpoch,
        };
        var result = await contributor.ContributeContextAsync(domain, token).ConfigureAwait(false);
        return result is null ? null : SemanticProviderValues.Convert<Wire.AgentPromptContextContribution>(result);
    }

    public async ValueTask<Wire.Unit> AcknowledgeAsync(SunderRpcInvocationContext context, Wire.AgentPromptContextReceipt request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (contributor is IAgentPromptContextAcknowledgmentSink sink)
            await sink.AcknowledgePromptContextAsync(SemanticProviderValues.Convert<Domain.AgentPromptContextReceipt>(request), token).ConfigureAwait(false);
        return new();
    }
}
