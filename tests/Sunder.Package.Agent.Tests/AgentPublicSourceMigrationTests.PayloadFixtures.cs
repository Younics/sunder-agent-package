using System.Text.Json;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    // Historical request shapes used only to author/inspect scenario JSON. They
    // carry no dispatcher, client, provider factory or production CLR identity.
    // The generated handlers validate them against the immutable descriptor oracle.
    internal sealed record BehaviorLoopRunRequest(Guid InvocationId, AgentBehaviorLoopContext Context);
    internal sealed record PromptContextTurnWire(string UserMessage, string? WorkingSummary);
    internal sealed record PromptContextRequestWire(AgentSessionContextRecord Session, AgentRunContextRecord Run, PromptContextTurnWire Turn,
        IReadOnlyList<AgentTurnRecord> Turns, IReadOnlyList<AgentTurnRecord> RecentLiveBufferTurns, AgentPromptContextPlan ContextPlan,
        AgentProfileRecord? Profile, AgentWorkspaceRecord? Workspace, AgentWorkspaceBindingRecord? ExecutionBinding,
        IReadOnlyList<AgentToolDescriptor> AvailableTools, AgentMemoryConsistencyBarrier? MemoryConsistencyBarrier,
        long TranscriptEpoch, Sunder.Sdk.Rpc.SunderRpcProviderSnapshot? ExecutionTarget)
    {
        // The frozen descriptor still includes the released continuity-summary field.
#pragma warning disable CS0618
        internal static PromptContextRequestWire FromRequest(AgentPromptContextRequest request)
            => new(request.Session, request.Run, new(request.Turn.UserMessage, request.Turn.WorkingSummary), request.Turns,
                request.RecentLiveBufferTurns, request.ContextPlan, request.Profile, request.Workspace, request.ExecutionBinding,
                request.AvailableTools, request.MemoryConsistencyBarrier, request.TranscriptEpoch, null);
#pragma warning restore CS0618

        internal AgentPromptContextRequest DomainRequestWithoutTarget()
        {
            if (ExecutionTarget is not null) throw new InvalidOperationException("The direct domain oracle cannot resolve target authority.");
            return new(Session, Run, new(Session, Run, Turn.UserMessage, Turn.WorkingSummary), Turns, RecentLiveBufferTurns, ContextPlan)
            {
                Profile = Profile, Workspace = Workspace, ExecutionBinding = ExecutionBinding, AvailableTools = AvailableTools,
                MemoryConsistencyBarrier = MemoryConsistencyBarrier, TranscriptEpoch = TranscriptEpoch,
            };
        }
    }
}
