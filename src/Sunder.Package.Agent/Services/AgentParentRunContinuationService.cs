using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services.BehaviorLoops;

namespace Sunder.Package.Agent.Services;

public sealed class AgentParentRunContinuationService(
    AgentSessionService sessionService,
    AgentProfileService profileService,
    AgentWorkspaceService workspaceService,
    AgentRunProviderResolver providerResolver,
    AgentActiveRunRegistry activeRunRegistry,
    AgentBehaviorLoopHostFactory behaviorLoopHostFactory,
    AgentBehaviorLoopResolver behaviorLoopResolver,
    AgentChildRunSessionService childRunSessionService)
{
    private readonly AgentSessionService _sessionService = sessionService;
    private readonly AgentProfileService _profileService = profileService;
    private readonly AgentWorkspaceService _workspaceService = workspaceService;
    private readonly AgentRunProviderResolver _providerResolver = providerResolver;
    private readonly AgentActiveRunRegistry _activeRunRegistry = activeRunRegistry;
    private readonly AgentBehaviorLoopHostFactory _behaviorLoopHostFactory = behaviorLoopHostFactory;
    private readonly AgentBehaviorLoopResolver _behaviorLoopResolver = behaviorLoopResolver;
    private readonly AgentChildRunSessionService _childRunSessionService = childRunSessionService;

    public async Task<AgentRunCheckpointRecord?> TryResumeAfterChildCompletionAsync(
        AgentSessionRecord childSession,
        AgentRunCheckpointRecord childCheckpoint,
        string workspaceId,
        CancellationToken cancellationToken)
    {
        if (childCheckpoint.Status != AgentRunStatus.Completed
            || childSession.ParentSessionId is not { } parentSessionId
            || childSession.ParentRunId is not { } parentRunId
            || childSession.ParentRunRevision is not { } parentRunRevision
            || string.IsNullOrWhiteSpace(childSession.ParentToolCallId))
        {
            return null;
        }

        var parentSession = _sessionService.GetSession(parentSessionId);
        var parentProfile = ResolveProfile(parentSession?.ProfileId);
        if (parentSession is null || parentProfile is null || _activeRunRegistry.IsActive(parentSessionId))
        {
            return null;
        }

        var workspace = ResolveWorkspace(workspaceId);
        if (workspace is null)
        {
            return _sessionService.SaveCheckpoint(parentSessionId, parentRunRevision, AgentRunStatus.Failed, "The workspace used for the parent run was not found.");
        }

        var providerSelection = _providerResolver.ResolveChatProvider(parentProfile);
        var chatBinding = providerSelection.ChatBinding;
        var provider = providerSelection.Provider;
        if (provider is null || chatBinding is null || string.IsNullOrWhiteSpace(chatBinding.ModelId))
        {
            return _sessionService.SaveCheckpoint(parentSessionId, parentRunRevision, AgentRunStatus.Failed, "No installed provider matches this profile yet, or no model is selected.");
        }

        var parentUserTurn = FindLatestUserTurn(parentSessionId);
        if (parentUserTurn is null)
        {
            return _sessionService.SaveCheckpoint(parentSessionId, parentRunRevision, AgentRunStatus.Failed, "The parent run user turn was not found.");
        }

        var childContent = _childRunSessionService.RenderLastAssistantText(childSession.SessionId)
                           ?? childCheckpoint.Summary
                           ?? "Subagent completed without visible output.";
        if (!HasToolResult(parentSessionId, childSession.ParentToolCallId))
        {
            _sessionService.AppendToolResultTurn(
                parentSessionId,
                childSession.ParentToolCallId,
                "task",
                argumentsJson: null,
                childContent,
                $"Subagent '{childSession.Title}' completed.",
                structuredPayloadJson: null,
                sourcesJson: null,
                wasTruncated: false,
                isError: false,
                errorCode: null,
                backendId: childSession.SessionId.ToString("N"));
        }

        if (HasUnresolvedSiblingChildTasks(parentSession, parentRunId, parentRunRevision))
        {
            return _sessionService.SaveCheckpoint(parentSessionId, parentRunRevision, AgentRunStatus.WaitingForApproval, "Waiting for remaining subagent tasks to complete.");
        }

        AgentProviderRunCapabilities runCapabilities;
        try
        {
            runCapabilities = await _providerResolver.ResolveRunCapabilitiesAsync(provider, chatBinding, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return _sessionService.SaveCheckpoint(parentSessionId, parentRunRevision, AgentRunStatus.Failed, ex.Message);
        }

        if (!runCapabilities.SupportsNativeToolCalling)
        {
            return _sessionService.SaveCheckpoint(parentSessionId, parentRunRevision, AgentRunStatus.Completed, "Subagent result recorded.");
        }

        var runStartedAtUtc = DateTimeOffset.UtcNow;
        var userMessage = RenderTurnText(parentUserTurn);
        var runningCheckpoint = _sessionService.SaveCheckpoint(parentSessionId, parentRunRevision, AgentRunStatus.Running, "Subagent completed. Continuing provider execution.");
        var runHandle = new AgentActiveRunHandle(parentRunId, parentRunRevision, runStartedAtUtc, parentProfile.ProfileId, userMessage, new CancellationTokenSource());
        _activeRunRegistry.Set(parentSessionId, runHandle);

        try
        {
            var executionBinding = ResolveExecutionBinding(workspace);
            var host = _behaviorLoopHostFactory.Create(provider, parentSession, parentProfile, workspace, parentRunId, parentRunRevision, runStartedAtUtc, userMessage, parentUserTurn.TurnId);
            var behaviorLoop = _behaviorLoopResolver.Resolve(parentProfile);
            var loopResult = await behaviorLoop.RunAsync(
                new AgentBehaviorLoopContext(
                    parentSession,
                    parentProfile,
                    provider.Descriptor.ProviderId,
                    chatBinding.ModelId,
                    runCapabilities,
                    workspace,
                    executionBinding,
                    parentRunId,
                    parentRunRevision,
                    runningCheckpoint,
                    runStartedAtUtc,
                    userMessage,
                    parentUserTurn.TurnId),
                host,
                runHandle.CancellationTokenSource.Token).ConfigureAwait(false);
            loopResult = ResolveStoppedOrInterruptedRunResult(parentSessionId, parentRunRevision, loopResult);
            return loopResult.Checkpoint;
        }
        finally
        {
            _activeRunRegistry.CleanupCurrent(parentSessionId, parentRunRevision);
        }
    }

    public void TryRecordParentTaskFailureAfterChildStop(AgentSessionRecord childSession, AgentRunCheckpointRecord childCheckpoint)
    {
        if (childSession.ParentSessionId is not { } parentSessionId
            || childSession.ParentRunRevision is not { } parentRunRevision
            || string.IsNullOrWhiteSpace(childSession.ParentToolCallId)
            || HasToolResult(parentSessionId, childSession.ParentToolCallId))
        {
            return;
        }

        _sessionService.AppendToolResultTurn(
            parentSessionId,
            childSession.ParentToolCallId,
            "task",
            argumentsJson: null,
            $"Subagent '{childSession.Title}' stopped: {childCheckpoint.Summary}",
            $"Subagent '{childSession.Title}' stopped.",
            structuredPayloadJson: null,
            sourcesJson: null,
            wasTruncated: false,
            isError: true,
            errorCode: "subagent-stopped",
            backendId: childSession.SessionId.ToString("N"));
        _sessionService.SaveCheckpoint(parentSessionId, parentRunRevision, AgentRunStatus.Stopped, childCheckpoint.Summary ?? "Subagent stopped.");
    }

    private AgentTurnRecord? FindLatestUserTurn(Guid sessionId)
        => _sessionService.ListTurns(sessionId)
            .Where(turn => turn.Role == AgentMessageRole.User && turn.Kind == AgentTurnKind.Message)
            .OrderByDescending(turn => turn.CreatedAtUtc)
            .ThenByDescending(turn => turn.TurnId)
            .FirstOrDefault();

    private bool HasUnresolvedSiblingChildTasks(AgentSessionRecord parentSession, Guid parentRunId, long parentRunRevision)
        => _sessionService.ListSessions()
            .Any(session => session.ParentSessionId == parentSession.SessionId
                            && session.ParentRunId == parentRunId
                            && session.ParentRunRevision == parentRunRevision
                            && !string.IsNullOrWhiteSpace(session.ParentToolCallId)
                            && !HasToolResult(parentSession.SessionId, session.ParentToolCallId));

    private bool HasToolResult(Guid sessionId, string callId)
        => _sessionService.ListTurns(sessionId)
            .SelectMany(turn => turn.Items)
            .Any(item => item.Kind == AgentTurnItemKind.ToolResult
                         && string.Equals(item.CallId, callId, StringComparison.Ordinal));

    private static string RenderTurnText(AgentTurnRecord turn)
        => string.Join("\n\n", turn.Items
            .Where(item => item.Kind == AgentTurnItemKind.Text && !string.IsNullOrWhiteSpace(item.TextContent))
            .Select(item => item.TextContent!.Trim()));

    private AgentBehaviorLoopResult ResolveStoppedOrInterruptedRunResult(
        Guid sessionId,
        long runRevision,
        AgentBehaviorLoopResult loopResult)
    {
        if (loopResult.Checkpoint.Status != AgentRunStatus.Running || _activeRunRegistry.IsCurrent(sessionId, runRevision))
        {
            return loopResult;
        }

        var replacement = GetStoppedOrInterruptedCheckpoint(sessionId, runRevision);
        return replacement is null
            ? loopResult
            : new AgentBehaviorLoopResult(replacement, ToCompletionKind(replacement.Status));
    }

    private AgentRunCheckpointRecord? GetStoppedOrInterruptedCheckpoint(Guid sessionId, long runRevision)
    {
        var latest = _sessionService.GetLatestCheckpoint(sessionId);
        return latest is not null
               && latest.RunRevision == runRevision
               && latest.Status is AgentRunStatus.Stopped or AgentRunStatus.Interrupted
            ? latest
            : null;
    }

    private static AgentBehaviorLoopCompletionKind ToCompletionKind(AgentRunStatus status)
        => status switch
        {
            AgentRunStatus.Completed => AgentBehaviorLoopCompletionKind.Completed,
            AgentRunStatus.WaitingForApproval => AgentBehaviorLoopCompletionKind.WaitingForApproval,
            AgentRunStatus.Stopped => AgentBehaviorLoopCompletionKind.Stopped,
            AgentRunStatus.Interrupted => AgentBehaviorLoopCompletionKind.Interrupted,
            _ => AgentBehaviorLoopCompletionKind.Failed,
        };

    private AgentWorkspaceRecord? ResolveWorkspace(string? workspaceId)
        => string.IsNullOrWhiteSpace(workspaceId)
            ? null
            : _workspaceService.GetWorkspace(workspaceId.Trim());

    private AgentProfileRecord? ResolveProfile(string? profileId)
        => string.IsNullOrWhiteSpace(profileId)
            ? null
            : _profileService.GetProfile(profileId);

    private AgentWorkspaceBindingRecord? ResolveExecutionBinding(AgentWorkspaceRecord? workspace)
        => workspace is null
            ? null
            : _workspaceService.ListBindings(workspace.WorkspaceId)
                .FirstOrDefault(binding => binding.IsEnabled
                                           && string.Equals(binding.Role, AgentWorkspaceBindingRoles.PrimaryExecutionTarget, StringComparison.OrdinalIgnoreCase));
}
