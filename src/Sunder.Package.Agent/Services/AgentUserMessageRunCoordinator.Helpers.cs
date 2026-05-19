using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services.BehaviorLoops;

namespace Sunder.Package.Agent.Services;

public sealed partial class AgentUserMessageRunCoordinator
{
    private AgentWorkspaceRecord? ResolveWorkspace(string? workspaceId) =>
        string.IsNullOrWhiteSpace(workspaceId)
            ? null
            : _workspaceService.GetWorkspace(workspaceId.Trim());

    private AgentProfileRecord? ResolveProfile(string? profileId) =>
        string.IsNullOrWhiteSpace(profileId) ? null : _profileService.GetProfile(profileId);

    private AgentWorkspaceBindingRecord? ResolveExecutionBinding(AgentWorkspaceRecord? workspace) =>
        workspace is null
            ? null
            : _workspaceService
                .ListBindings(workspace.WorkspaceId)
                .FirstOrDefault(binding =>
                    binding.IsEnabled
                    && string.Equals(
                        binding.Role,
                        AgentWorkspaceBindingRoles.PrimaryExecutionTarget,
                        StringComparison.OrdinalIgnoreCase
                    )
                );

    private AgentBehaviorLoopResult ResolveStoppedOrInterruptedRunResult(
        Guid sessionId,
        long runRevision,
        AgentBehaviorLoopResult loopResult
    )
    {
        if (
            loopResult.Checkpoint.Status != AgentRunStatus.Running
            || _activeRunRegistry.IsCurrent(sessionId, runRevision)
        )
        {
            return loopResult;
        }

        var replacement = GetStoppedOrInterruptedCheckpoint(sessionId, runRevision);
        return replacement is null
            ? loopResult
            : new AgentBehaviorLoopResult(replacement, ToCompletionKind(replacement.Status));
    }

    private AgentRunCheckpointRecord? GetStoppedOrInterruptedCheckpoint(
        Guid sessionId,
        long runRevision
    )
    {
        var latest = _sessionService.GetLatestCheckpoint(sessionId);
        return
            latest is not null
            && latest.RunRevision == runRevision
            && latest.Status is AgentRunStatus.Stopped or AgentRunStatus.Interrupted
            ? latest
            : null;
    }

    private static AgentBehaviorLoopCompletionKind ToCompletionKind(AgentRunStatus status) =>
        status switch
        {
            AgentRunStatus.Completed => AgentBehaviorLoopCompletionKind.Completed,
            AgentRunStatus.WaitingForApproval => AgentBehaviorLoopCompletionKind.WaitingForApproval,
            AgentRunStatus.Stopped => AgentBehaviorLoopCompletionKind.Stopped,
            AgentRunStatus.Interrupted => AgentBehaviorLoopCompletionKind.Interrupted,
            _ => AgentBehaviorLoopCompletionKind.Failed,
        };
}
