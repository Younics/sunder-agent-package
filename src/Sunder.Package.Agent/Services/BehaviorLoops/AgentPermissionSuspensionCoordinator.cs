using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

internal sealed class AgentPermissionSuspensionCoordinator(AgentBehaviorLoopHost host)
{
    private readonly AgentBehaviorLoopHost _host = host;

    public Task<AgentToolCallOutcome?> EvaluateAsync(
        AgentToolCallRequest toolCall,
        AgentTurnRecord? assistantTurn,
        IReadOnlyDictionary<string, AgentToolDescriptor> availableToolsById,
        CancellationToken cancellationToken)
        => _host.EvaluateToolPermissionCoreAsync(
            toolCall,
            assistantTurn,
            availableToolsById,
            cancellationToken);

    public Task<AgentToolCallOutcome> ResumeAsync(
        AgentPendingPermissionRequestRecord pending,
        CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask<bool>> beginExecutionAsync)
        => _host.HandleApprovedToolCallCoreAsync(
            pending,
            cancellationToken,
            beginExecutionAsync);
}
