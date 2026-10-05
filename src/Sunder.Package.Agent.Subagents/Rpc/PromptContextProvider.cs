using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Contracts.Models;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Prompt_Context_Contributor;

namespace Sunder.Package.Agent.Subagents.Rpc;

[SunderRpcProvider(typeof(IPromptContextService), ProviderId = "subagents.prompt.context")]
public sealed partial class PromptContextProvider(IAgentPromptContextContributor contributor) : IPromptContextService
{
    public ValueTask<Wire.PromptContextContributorDescription> DescribeAsync(SunderRpcInvocationContext context, Wire.Unit request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new Wire.PromptContextContributorDescription
        { ContributorId = contributor.ContributorId, DisplayName = contributor.DisplayName, AcknowledgmentSinkId = (contributor as IAgentPromptContextAcknowledgmentSink)?.AcknowledgmentSinkId });
    }

    public async ValueTask<Wire.AgentPromptContextContribution?> ContributeAsync(SunderRpcInvocationContext context, Wire.PromptContextRequestWire request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var session = SubagentProviderValues.Convert<AgentSessionContextRecord>(request.Session);
        var run = SubagentProviderValues.Convert<AgentRunContextRecord>(request.Run);
        var plan = new Domain.AgentPromptContextPlan(request.ContextPlan.Intent, request.ContextPlan.QueryText, request.ContextPlan.Reason,
            request.ContextPlan.PreferredCategories, checked((int)request.ContextPlan.MaxEntryCount), checked((int)request.ContextPlan.MaxChars));
        var value = new Domain.AgentPromptContextRequest(session, run,
            new AgentTurnContextRecord(session, run, request.Turn.UserMessage, request.Turn.WorkingSummary),
            SubagentProviderValues.Convert<Domain.AgentTurnRecord[]>(request.Turns), SubagentProviderValues.Convert<Domain.AgentTurnRecord[]>(request.RecentLiveBufferTurns), plan)
        {
            Profile = SubagentProviderValues.Optional<Domain.AgentProfileRecord>(request.Profile),
            Workspace = SubagentProviderValues.Optional<Domain.AgentWorkspaceRecord>(request.Workspace),
            ExecutionBinding = SubagentProviderValues.Optional<Domain.AgentWorkspaceBindingRecord>(request.ExecutionBinding),
            AvailableTools = SubagentProviderValues.Convert<Domain.AgentToolDescriptor[]>(request.AvailableTools),
            MemoryConsistencyBarrier = SubagentProviderValues.Optional<Domain.AgentMemoryConsistencyBarrier>(request.MemoryConsistencyBarrier),
            TranscriptEpoch = request.TranscriptEpoch,
        };
        return SubagentProviderValues.Optional<Wire.AgentPromptContextContribution>(await contributor.ContributeContextAsync(value, token).ConfigureAwait(false));
    }

    public async ValueTask<Wire.Unit> AcknowledgeAsync(SunderRpcInvocationContext context, Wire.AgentPromptContextReceipt request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (contributor is IAgentPromptContextAcknowledgmentSink sink)
            await sink.AcknowledgePromptContextAsync(SubagentProviderValues.Convert<Domain.AgentPromptContextReceipt>(request), token).ConfigureAwait(false);
        return new();
    }
}
