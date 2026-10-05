using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Contracts.Models;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Prompt_Context_Contributor;

namespace Sunder.Package.Agent.Tools.Files.Rpc;

[SunderRpcProvider(typeof(IPromptContextService), ProviderId = "files.prompt.context")]
internal sealed partial class PromptContextProvider(FilesToolSource source, FilesExecutionBindings execution) : IPromptContextService
{
    public ValueTask<Wire.PromptContextContributorDescription> DescribeAsync(SunderRpcInvocationContext context, Wire.Unit request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new Wire.PromptContextContributorDescription { ContributorId = source.ContributorId, DisplayName = source.DisplayName, AcknowledgmentSinkId = source.AcknowledgmentSinkId }); }
    public async ValueTask<Wire.AgentPromptContextContribution?> ContributeAsync(SunderRpcInvocationContext context, Wire.PromptContextRequestWire request, CancellationToken token)
    {
        var session = FilesProviderValues.Convert<AgentSessionContextRecord>(request.Session);
        var run = FilesProviderValues.Convert<AgentRunContextRecord>(request.Run);
        var plan = new Domain.AgentPromptContextPlan(request.ContextPlan.Intent, request.ContextPlan.QueryText, request.ContextPlan.Reason,
            request.ContextPlan.PreferredCategories, checked((int)request.ContextPlan.MaxEntryCount), checked((int)request.ContextPlan.MaxChars));
        var value = new Domain.AgentPromptContextRequest(session, run, new AgentTurnContextRecord(session, run, request.Turn.UserMessage, request.Turn.WorkingSummary),
            FilesProviderValues.Convert<Domain.AgentTurnRecord[]>(request.Turns), FilesProviderValues.Convert<Domain.AgentTurnRecord[]>(request.RecentLiveBufferTurns), plan)
        {
            Profile = FilesProviderValues.Optional<Domain.AgentProfileRecord>(request.Profile),
            Workspace = FilesProviderValues.Optional<Domain.AgentWorkspaceRecord>(request.Workspace),
            ExecutionBinding = FilesProviderValues.Optional<Domain.AgentWorkspaceBindingRecord>(request.ExecutionBinding),
            AvailableTools = FilesProviderValues.Convert<Domain.AgentToolDescriptor[]>(request.AvailableTools),
            MemoryConsistencyBarrier = FilesProviderValues.Optional<Domain.AgentMemoryConsistencyBarrier>(request.MemoryConsistencyBarrier),
            TranscriptEpoch = request.TranscriptEpoch,
        };
        return FilesProviderValues.Optional<Wire.AgentPromptContextContribution>(await source.ContributeContextAsync(value,
            execution.Select(FilesProviderValues.Optional<AgentRpcProviderHandle>(request.ExecutionTarget), context.DeadlineUtc), token).ConfigureAwait(false));
    }
    public async ValueTask<Wire.Unit> AcknowledgeAsync(SunderRpcInvocationContext context, Wire.AgentPromptContextReceipt request, CancellationToken token)
    { await source.AcknowledgePromptContextAsync(FilesProviderValues.Convert<Domain.AgentPromptContextReceipt>(request), token).ConfigureAwait(false); return new(); }
}
