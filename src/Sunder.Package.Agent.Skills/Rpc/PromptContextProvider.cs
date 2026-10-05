using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Skills.Runtime;
using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Contracts.Models;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Prompt_Context_Contributor;

namespace Sunder.Package.Agent.Skills.Rpc;

[SunderRpcProvider(typeof(IPromptContextService), ProviderId = "skills.prompt.context")]
public sealed partial class PromptContextProvider(IAgentPromptContextContributor contributor, ISkillsExecutionFeature execution) : IPromptContextService
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
        var session = SkillsProviderValues.Convert<AgentSessionContextRecord>(request.Session);
        var run = SkillsProviderValues.Convert<AgentRunContextRecord>(request.Run);
        var plan = new Domain.AgentPromptContextPlan(request.ContextPlan.Intent, request.ContextPlan.QueryText, request.ContextPlan.Reason,
            request.ContextPlan.PreferredCategories, checked((int)request.ContextPlan.MaxEntryCount), checked((int)request.ContextPlan.MaxChars));
        var value = new Domain.AgentPromptContextRequest(session, run,
            new AgentTurnContextRecord(session, run, request.Turn.UserMessage, request.Turn.WorkingSummary),
            SkillsProviderValues.Convert<Domain.AgentTurnRecord[]>(request.Turns), SkillsProviderValues.Convert<Domain.AgentTurnRecord[]>(request.RecentLiveBufferTurns), plan)
        {
            Profile = SkillsProviderValues.Optional<Domain.AgentProfileRecord>(request.Profile),
            Workspace = SkillsProviderValues.Optional<Domain.AgentWorkspaceRecord>(request.Workspace),
            ExecutionBinding = SkillsProviderValues.Optional<Domain.AgentWorkspaceBindingRecord>(request.ExecutionBinding),
            AvailableTools = SkillsProviderValues.Convert<Domain.AgentToolDescriptor[]>(request.AvailableTools),
            MemoryConsistencyBarrier = SkillsProviderValues.Optional<Domain.AgentMemoryConsistencyBarrier>(request.MemoryConsistencyBarrier),
            TranscriptEpoch = request.TranscriptEpoch,
        };
        return SkillsProviderValues.Optional<Wire.AgentPromptContextContribution>(await execution.ContributeContextAsync(value,
            SkillsProviderValues.Optional<AgentRpcProviderHandle>(request.ExecutionTarget), token).ConfigureAwait(false));
    }

    public async ValueTask<Wire.Unit> AcknowledgeAsync(SunderRpcInvocationContext context, Wire.AgentPromptContextReceipt request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (contributor is IAgentPromptContextAcknowledgmentSink sink)
            await sink.AcknowledgePromptContextAsync(SkillsProviderValues.Convert<Domain.AgentPromptContextReceipt>(request), token).ConfigureAwait(false);
        return new();
    }
}
