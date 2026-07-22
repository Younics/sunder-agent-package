using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Services;

internal sealed class AgentRunExecutionService(
    AgentSessionService sessionService,
    AgentWorkspaceService workspaceService,
    AgentMemoryCoordinator memoryCoordinator,
    AgentActiveRunRegistry activeRunRegistry,
    AgentRunEventLogger runEventLogger,
    AgentBehaviorLoopHostFactory behaviorLoopHostFactory,
    AgentBehaviorLoopResolver behaviorLoopResolver)
{
    private readonly AgentSessionService _sessionService = sessionService;
    private readonly AgentWorkspaceService _workspaceService = workspaceService;
    private readonly AgentMemoryCoordinator _memoryCoordinator = memoryCoordinator;
    private readonly AgentActiveRunRegistry _activeRunRegistry = activeRunRegistry;
    private readonly AgentRunEventLogger _runEventLogger = runEventLogger;
    private readonly AgentBehaviorLoopHostFactory _behaviorLoopHostFactory =
        behaviorLoopHostFactory;
    private readonly AgentBehaviorLoopResolver _behaviorLoopResolver = behaviorLoopResolver;

    internal async Task<AgentRunCheckpointRecord> ExecuteAsync(AgentRunStarted started)
    {
        var plan = started.Plan;
        AgentBehaviorLoopHost? host = null;
        try
        {
            var executionBinding = ResolveExecutionBinding(plan.Workspace);
            host = _behaviorLoopHostFactory.Create(
                plan.Provider,
                plan.Session,
                plan.Profile,
                plan.Workspace,
                plan.RunKey.RunId,
                plan.RunKey.RunRevision,
                plan.StartedAtUtc,
                plan.UserMessage,
                started.UserTurn.TurnId,
                executionBinding);
            var behaviorLoop = _behaviorLoopResolver.Resolve(plan.Profile);
            _runEventLogger.LogRunEvent(
                PackageLogLevel.Debug,
                plan.RunKey.SessionId,
                plan.RunKey.RunId,
                plan.RunKey.RunRevision,
                "behavior.loop.selected",
                "Behavior loop selected.",
                ElapsedMilliseconds(plan),
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["behavior.loop_id"] = behaviorLoop.Descriptor.LoopId,
                });
            var loopResult = await behaviorLoop.RunAsync(
                BuildContext(started, executionBinding),
                host,
                started.RunCancellationToken).ConfigureAwait(false);
            host.TryCompleteOpenAssistantTurn();
            loopResult = ResolveStoppedOrInterruptedRunResult(plan, loopResult);
            _runEventLogger.LogRunCompletion(
                plan.RunKey.SessionId,
                plan.RunKey.RunId,
                plan.RunKey.RunRevision,
                loopResult,
                ElapsedMilliseconds(plan));
            return loopResult.Checkpoint;
        }
        catch (OperationCanceledException)
        {
            host?.TryCompleteOpenAssistantTurn();
            _runEventLogger.LogRunEvent(
                PackageLogLevel.Warning,
                plan.RunKey.SessionId,
                plan.RunKey.RunId,
                plan.RunKey.RunRevision,
                "run.canceled",
                "Agent run was canceled.",
                ElapsedMilliseconds(plan));
            return GetTerminalCheckpoint(plan)
                ?? _sessionService.TryTransitionRun(
                    plan.RunHandle.DurableLease!,
                    AgentRunStatus.Interrupted,
                    "Agent run was canceled after provider execution started.")?.Checkpoint
                ?? GetTerminalCheckpoint(plan)
                ?? started.InterruptedCheckpoint
                ?? started.RunningCheckpoint;
        }
        catch (Exception ex)
        {
            host?.TryCompleteOpenAssistantTurn();
            return await HandleFailureAsync(started, ex).ConfigureAwait(false);
        }
        finally
        {
            _activeRunRegistry.CleanupCurrent(
                plan.RunKey.SessionId,
                plan.RunKey.RunId,
                plan.RunKey.RunRevision);
        }
    }

    private static AgentBehaviorLoopContext BuildContext(
        AgentRunStarted started,
        AgentWorkspaceBindingRecord? executionBinding)
    {
        var plan = started.Plan;
        return new AgentBehaviorLoopContext(
            plan.Session,
            plan.Profile,
            plan.Provider.Descriptor.ProviderId,
            plan.ChatBinding.ModelId!,
            plan.RunCapabilities,
            plan.Workspace,
            executionBinding,
            plan.RunKey.RunId,
            plan.RunKey.RunRevision,
            started.RunningCheckpoint,
            plan.StartedAtUtc,
            plan.UserMessage,
            started.UserTurn.TurnId,
            plan.ModelVariant,
            plan.ModelSpeedOption,
            plan.ModelModeOption);
    }

    private async Task<AgentRunCheckpointRecord> HandleFailureAsync(
        AgentRunStarted started,
        Exception exception)
    {
        var plan = started.Plan;
        if (!IsCurrent(plan))
        {
            return GetTerminalCheckpoint(plan)
                   ?? started.InterruptedCheckpoint
                   ?? started.RunningCheckpoint;
        }

        AgentTurnRecord assistantTurn;
        try
        {
            assistantTurn = _sessionService.AppendTextTurn(
                plan.RunHandle.DurableLease!,
                AgentMessageRole.Assistant,
                $"### Agent run failed\n\n{exception.Message}");
            assistantTurn = _sessionService.CompleteTextTurn(
                plan.RunHandle.DurableLease!,
                assistantTurn.TurnId);
        }
        catch (AgentRunTranscriptWriteRejectedException)
        {
            return GetTerminalCheckpoint(plan) ?? started.RunningCheckpoint;
        }
        var failedCheckpoint = _sessionService.TryTransitionRun(
                                   plan.RunHandle.DurableLease!,
                                   AgentRunStatus.Failed,
                                   exception.Message)?.Checkpoint
                                ?? GetTerminalCheckpoint(plan)
                               ?? started.RunningCheckpoint;
        _runEventLogger.LogRunEvent(
            PackageLogLevel.Error,
            plan.RunKey.SessionId,
            plan.RunKey.RunId,
            plan.RunKey.RunRevision,
            "run.failed",
            exception.Message,
            ElapsedMilliseconds(plan),
            exception: exception);
        await _memoryCoordinator.PublishLifecycleEventAsync(
            AgentLifecycleEventKind.RunFailed,
            plan.Session,
            plan.Profile,
            plan.RunKey.RunId,
            plan.RunKey.RunRevision,
            AgentRunStatus.Failed,
            plan.StartedAtUtc,
            plan.UserMessage,
            triggerTurn: assistantTurn,
            checkpoint: failedCheckpoint,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        return failedCheckpoint;
    }

    private AgentBehaviorLoopResult ResolveStoppedOrInterruptedRunResult(
        AgentRunPlan plan,
        AgentBehaviorLoopResult loopResult)
    {
        if (loopResult.Checkpoint.Status == AgentRunStatus.Running
            && loopResult.CompletionKind == AgentBehaviorLoopCompletionKind.Interrupted)
        {
            var interrupted = GetTerminalCheckpoint(plan)
                ?? _sessionService.TryTransitionRun(
                    plan.RunHandle.DurableLease!,
                    AgentRunStatus.Interrupted,
                    "Agent run was canceled after provider execution started.")?.Checkpoint;
            if (interrupted is not null)
            {
                return new AgentBehaviorLoopResult(
                    interrupted,
                    AgentBehaviorLoopCompletionKind.Interrupted);
            }
        }

        if (loopResult.Checkpoint.Status != AgentRunStatus.Running || IsCurrent(plan))
        {
            return loopResult;
        }

        var replacement = GetTerminalCheckpoint(plan);
        return replacement is null
            ? loopResult
            : new AgentBehaviorLoopResult(replacement, ToCompletionKind(replacement.Status));
    }

    private AgentRunCheckpointRecord? GetTerminalCheckpoint(AgentRunPlan plan)
    {
        var latest = _sessionService.GetLatestCheckpoint(
            plan.RunKey.SessionId,
            plan.RunKey.RunRevision);
        return latest is not null
            && latest.RunRevision == plan.RunKey.RunRevision
            && latest.Status is AgentRunStatus.Completed
                or AgentRunStatus.Failed
                or AgentRunStatus.Stopped
                or AgentRunStatus.Interrupted
                ? latest
                : null;
    }

    private AgentWorkspaceBindingRecord? ResolveExecutionBinding(AgentWorkspaceRecord workspace) =>
        _workspaceService.ListBindings(workspace.WorkspaceId)
            .FirstOrDefault(binding =>
                binding.IsEnabled
                && string.Equals(
                    binding.Role,
                    AgentWorkspaceBindingRoles.PrimaryExecutionTarget,
                    StringComparison.OrdinalIgnoreCase));

    private bool IsCurrent(AgentRunPlan plan) =>
        _activeRunRegistry.IsCurrent(
            plan.RunKey.SessionId,
            plan.RunKey.RunId,
            plan.RunKey.RunRevision);

    private static AgentBehaviorLoopCompletionKind ToCompletionKind(AgentRunStatus status) =>
        status switch
        {
            AgentRunStatus.Completed => AgentBehaviorLoopCompletionKind.Completed,
            AgentRunStatus.WaitingForApproval => AgentBehaviorLoopCompletionKind.WaitingForApproval,
            AgentRunStatus.Stopped => AgentBehaviorLoopCompletionKind.Stopped,
            AgentRunStatus.Interrupted => AgentBehaviorLoopCompletionKind.Interrupted,
            _ => AgentBehaviorLoopCompletionKind.Failed,
        };

    private static long ElapsedMilliseconds(AgentRunPlan plan) =>
        Math.Max(0, (long)(DateTimeOffset.UtcNow - plan.StartedAtUtc).TotalMilliseconds);
}
