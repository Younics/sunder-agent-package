using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Services;

internal sealed class AgentRunStartService(
    AgentSessionService sessionService,
    AgentMemoryCoordinator memoryCoordinator,
    AgentRunAttachmentStore attachmentStore,
    AgentActiveRunRegistry activeRunRegistry,
    AgentRunEventLogger runEventLogger,
    AgentSessionTitleService? sessionTitleService = null,
    AgentSessionTransitionGate? transitionGate = null)
{
    private const string SupersededDuringPreparation =
        "Superseded by a newer run while provider preparation was completing.";
    private const string SupersededBeforeExecution =
        "Superseded by a newer run before provider execution started.";

    private readonly AgentSessionService _sessionService = sessionService;
    private readonly AgentMemoryCoordinator _memoryCoordinator = memoryCoordinator;
    private readonly AgentRunAttachmentStore _attachmentStore = attachmentStore;
    private readonly AgentActiveRunRegistry _activeRunRegistry = activeRunRegistry;
    private readonly AgentRunEventLogger _runEventLogger = runEventLogger;
    private readonly AgentSessionTitleService? _sessionTitleService = sessionTitleService;
    private readonly AgentSessionTransitionGate _transitionGate =
        transitionGate ?? AgentSessionTransitionGate.Shared;

    internal async Task<AgentRunStartResult> StartAsync(
        AgentRunPlan plan,
        CancellationToken cancellationToken)
        => await StartAsync(plan, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);

    internal async Task<AgentRunStartResult> StartAsync(
        AgentRunPlan plan,
        Guid userTurnId,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            CleanupUncommittedAttachments(plan);
            cancellationToken.ThrowIfCancellationRequested();
        }

        var runCancellationToken = plan.RunHandle.CancellationTokenSource.Token;

        var userTurnCommitted = false;
        try
        {
            runCancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrent(plan))
            {
                CleanupUncommittedAttachments(plan);
                return Interrupted(plan, SupersededBeforeExecution);
            }

            AgentRunStartPersistenceResult persistedStart;
            var runningSummary = BuildRunningSummary(plan);
            using (await _transitionGate
                       .EnterAsync(plan.RunKey.SessionId, runCancellationToken)
                       .ConfigureAwait(false))
            {
                runCancellationToken.ThrowIfCancellationRequested();
                AgentRunStartPersistenceResult? start;
                try
                {
                    start = IsCurrent(plan)
                        ? _sessionService.TryStartRun(
                            plan.RunHandle.DurableLease!,
                            userTurnId,
                            plan.UserMessage,
                            plan.Attachments,
                            plan.RollbackAnchorTurnId,
                            runningSummary)
                        : null;
                }
                catch (AgentRunStartCleanupException)
                {
                    userTurnCommitted = true;
                    throw;
                }

                if (start is null)
                {
                    return Interrupted(plan, SupersededBeforeExecution);
                }

                persistedStart = start;
                userTurnCommitted = true;
            }

            var userTurn = persistedStart.UserTurn;
            if (persistedStart.Rollback is not null)
            {
                var rolledBackSession = _sessionService.GetSession(plan.RunKey.SessionId)
                    ?? throw new InvalidOperationException(
                        $"Session '{plan.RunKey.SessionId}' was not found after rollback.");
                plan = plan with { Session = rolledBackSession };
            }

            if (plan.ShouldGenerateSessionTitle)
            {
                _sessionTitleService?.ScheduleTitleFromFirstUserMessage(
                    plan.Session,
                    plan.Profile,
                    plan.UserMessage,
                    plan.RunKey.RunId,
                    plan.RunKey.RunRevision);
            }

            LogUserTurnAppended(plan);
            await _memoryCoordinator.PublishLifecycleEventAsync(
                AgentLifecycleEventKind.UserTurnAdded,
                plan.Session,
                plan.Profile,
                plan.RunKey.RunId,
                plan.RunKey.RunRevision,
                AgentRunStatus.Running,
                plan.StartedAtUtc,
                plan.UserMessage,
                triggerTurn: userTurn,
                cancellationToken: runCancellationToken).ConfigureAwait(false);
            runCancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrent(plan))
            {
                return Interrupted(plan, SupersededBeforeExecution);
            }

            var runningCheckpoint = persistedStart.Transition.Checkpoint;
            _runEventLogger.LogRunEvent(
                PackageLogLevel.Debug,
                plan.RunKey.SessionId,
                plan.RunKey.RunId,
                plan.RunKey.RunRevision,
                "run.running_checkpoint.saved",
                runningSummary,
                ElapsedMilliseconds(plan));
            return new AgentRunStarted(
                plan,
                userTurn,
                runningCheckpoint,
                runCancellationToken,
                InterruptedCheckpoint: null);
        }
        catch (OperationCanceledException ex)
        {
            if (CleanupFailedStart(plan, userTurnCommitted))
            {
                TryPersistStartTermination(
                    plan,
                    AgentRunStatus.Interrupted,
                    "Agent run was canceled before provider execution started.",
                    ex);
            }

            throw;
        }
        catch (Exception ex)
        {
            if (CleanupFailedStart(plan, userTurnCommitted))
            {
                TryPersistStartTermination(plan, AgentRunStatus.Failed, ex.Message, ex);
            }

            throw;
        }
    }

    private bool CleanupFailedStart(AgentRunPlan plan, bool userTurnCommitted)
    {
        if (!userTurnCommitted)
        {
            CleanupUncommittedAttachments(plan);
        }

        return IsCurrent(plan);
    }

    private void CleanupUncommittedAttachments(AgentRunPlan plan)
    {
        var failures = _attachmentStore.Cleanup(plan.Attachments);
        if (failures.Count == 0)
        {
            return;
        }

        _runEventLogger.LogRunEvent(
            PackageLogLevel.Warning,
            plan.RunKey.SessionId,
            plan.RunKey.RunId,
            plan.RunKey.RunRevision,
            "attachment.cleanup.failed",
            "One or more uncommitted run attachments could not be removed.",
            ElapsedMilliseconds(plan),
            exception: new AggregateException(failures));
    }

    private void TryPersistStartTermination(
        AgentRunPlan plan,
        AgentRunStatus status,
        string summary,
        Exception originalException)
    {
        try
        {
            _sessionService.TryTransitionRun(
                plan.RunHandle.DurableLease!,
                status,
                summary);
        }
        catch (Exception persistenceException)
        {
            _runEventLogger.LogRunEvent(
                PackageLogLevel.Error,
                plan.RunKey.SessionId,
                plan.RunKey.RunId,
                plan.RunKey.RunRevision,
                "run.termination_checkpoint.failed",
                "Run termination checkpoint could not be persisted.",
                ElapsedMilliseconds(plan),
                exception: persistenceException);
        }

        _runEventLogger.LogRunEvent(
            status == AgentRunStatus.Failed ? PackageLogLevel.Error : PackageLogLevel.Warning,
            plan.RunKey.SessionId,
            plan.RunKey.RunId,
            plan.RunKey.RunRevision,
            status == AgentRunStatus.Failed ? "run.failed" : "run.interrupted",
            summary,
            ElapsedMilliseconds(plan),
            exception: originalException);
    }

    private AgentRunStartInterrupted Interrupted(AgentRunPlan plan, string summary)
    {
        var checkpoint = GetStoppedOrInterruptedCheckpoint(plan)
            ?? _sessionService.TryTransitionRun(
                plan.RunHandle.DurableLease!,
                AgentRunStatus.Interrupted,
                summary)?.Checkpoint
            ?? _sessionService.GetLatestCheckpoint(plan.RunKey.SessionId)
            ?? throw new InvalidOperationException("Interrupted run has no durable checkpoint.");
        return new AgentRunStartInterrupted(checkpoint);
    }

    private AgentRunCheckpointRecord? GetStoppedOrInterruptedCheckpoint(AgentRunPlan plan)
    {
        var latest = _sessionService.GetLatestCheckpoint(plan.RunKey.SessionId);
        return latest is not null
            && latest.RunRevision == plan.RunKey.RunRevision
            && latest.Status is AgentRunStatus.Stopped or AgentRunStatus.Interrupted
                ? latest
                : null;
    }

    private void LogUserTurnAppended(AgentRunPlan plan) =>
        _runEventLogger.LogRunEvent(
            PackageLogLevel.Debug,
            plan.RunKey.SessionId,
            plan.RunKey.RunId,
            plan.RunKey.RunRevision,
            "turn.user.appended",
            "User turn appended.",
            ElapsedMilliseconds(plan),
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["turn.content_length"] = plan.UserMessage.Length,
                ["turn.attachment_count"] = plan.Attachments.Count,
                ["turn.attachment_bytes"] = plan.Attachments.Sum(attachment =>
                    attachment.Metadata.SizeBytes),
            });

    private bool IsCurrent(AgentRunPlan plan) =>
        _activeRunRegistry.IsCurrent(
            plan.RunKey.SessionId,
            plan.RunKey.RunId,
            plan.RunKey.RunRevision);

    private static string BuildRunningSummary(AgentRunPlan plan)
    {
        var profileHasCapabilityAssignments =
            (plan.Profile.SelectableCapabilityAssignments?.Count ?? 0) > 0;
        return plan.RunCapabilities.SupportsNativeToolCalling
            ? "User message queued. Provider execution is starting."
            : profileHasCapabilityAssignments
                ? $"User message queued. Provider execution is starting in text-only mode. {plan.RunCapabilities.Summary}"
                : "User message queued. Provider execution is starting.";
    }

    private static long ElapsedMilliseconds(AgentRunPlan plan) =>
        Math.Max(0, (long)(DateTimeOffset.UtcNow - plan.StartedAtUtc).TotalMilliseconds);
}
