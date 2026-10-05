using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Contracts.Models;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Prompt_Context_Contributor;

namespace Sunder.Package.Agent.Tools.Shell.Rpc;

[SunderRpcProvider(typeof(IPromptContextService), ProviderId = "shell.prompt.context")]
internal sealed partial class PromptContextProvider(ShellToolSource source, ShellExecutionBindings execution) : IPromptContextService
{
    public ValueTask<Wire.PromptContextContributorDescription> DescribeAsync(SunderRpcInvocationContext context, Wire.Unit request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new Wire.PromptContextContributorDescription { ContributorId = source.ContributorId, DisplayName = source.DisplayName }); }
    public async ValueTask<Wire.AgentPromptContextContribution?> ContributeAsync(SunderRpcInvocationContext context, Wire.PromptContextRequestWire request, CancellationToken token)
    {
        var session = ShellProviderValues.Convert<AgentSessionContextRecord>(request.Session);
        var run = ShellProviderValues.Convert<AgentRunContextRecord>(request.Run);
        var plan = new Domain.AgentPromptContextPlan(request.ContextPlan.Intent, request.ContextPlan.QueryText, request.ContextPlan.Reason,
            request.ContextPlan.PreferredCategories, checked((int)request.ContextPlan.MaxEntryCount), checked((int)request.ContextPlan.MaxChars));
        var value = new Domain.AgentPromptContextRequest(session, run, new AgentTurnContextRecord(session, run, request.Turn.UserMessage, request.Turn.WorkingSummary),
            ShellProviderValues.Convert<Domain.AgentTurnRecord[]>(request.Turns), ShellProviderValues.Convert<Domain.AgentTurnRecord[]>(request.RecentLiveBufferTurns), plan)
        {
            Profile = ShellProviderValues.Optional<Domain.AgentProfileRecord>(request.Profile),
            Workspace = ShellProviderValues.Optional<Domain.AgentWorkspaceRecord>(request.Workspace),
            ExecutionBinding = ShellProviderValues.Optional<Domain.AgentWorkspaceBindingRecord>(request.ExecutionBinding),
            AvailableTools = ShellProviderValues.Convert<Domain.AgentToolDescriptor[]>(request.AvailableTools),
            MemoryConsistencyBarrier = ShellProviderValues.Optional<Domain.AgentMemoryConsistencyBarrier>(request.MemoryConsistencyBarrier),
            TranscriptEpoch = request.TranscriptEpoch,
        };
        return ShellProviderValues.Optional<Wire.AgentPromptContextContribution>(await source.ContributeContextAsync(value,
            execution.Select(ShellProviderValues.Optional<AgentRpcProviderHandle>(request.ExecutionTarget), context.DeadlineUtc), token).ConfigureAwait(false));
    }
    public ValueTask<Wire.Unit> AcknowledgeAsync(SunderRpcInvocationContext context, Wire.AgentPromptContextReceipt request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new Wire.Unit()); }
}
